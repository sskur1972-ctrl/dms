//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 3a - document file store (ported from Bcl.Dms LocalFileStorageService / FileStoreRoot).
 *
 * Root: appsettings "Dms:Storage:Root" - the SAME key as the old DMS, so the old store folder can be
 * reused as it is (the paths recorded in document_versions / attachments / document_templates are
 * relative to it). A relative value is resolved against the application folder; default App_Data/filestore.
 *
 * Every upload passes one funnel (SaveUploadAsync): size limit (storage.maxUploadMb), accepted formats
 * (upload.allowedExtensions), write-once under <category>/<yyyy>/<MM>/<guid>-<name>, SHA-256 read back
 * from disk, optional virus scan (storage.virusScan.*). Nothing is ever overwritten or deleted - a rejected
 * file is moved to _archive.
 */
using System.Diagnostics;
using System.Security.Cryptography;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;

namespace DMS.Web.Services
{
    public class FileStoreException : Exception
    {
        public FileStoreException(string message) : base(message) { }
    }

    public class FileStore
    {
        public const string DefaultRelativeRoot = "App_Data/filestore";

        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<FileStore> _logger;
        private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache? _cache;
        private readonly string _cacheScope;
        private readonly long _cacheLimit;
        private readonly long _largeCacheLimit;

        public FileStore(IConfiguration configuration, IWebHostEnvironment environment, IUnitOfWork unitOfWork, ILogger<FileStore> logger,
            Microsoft.Extensions.Caching.Distributed.IDistributedCache? cache = null)
        {
            _unitOfWork = unitOfWork;
            _logger = logger;
            Root = ResolveRoot(configuration["Dms:Storage:Root"], environment.ContentRootPath);
            _cache = cache;
            var database = new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DMSConnection")).Database;
            _cacheScope = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(database + "|" + Root)));
            _cacheLimit = configuration.GetValue<long?>("Dms:Redis:MaxDocumentCacheBytes") ?? 32 * 1024 * 1024;
            _largeCacheLimit = configuration.GetValue<long?>("Dms:Redis:MaxLargeDocumentCacheBytes") ?? 128 * 1024 * 1024;
        }

        /// <summary>Absolute path of the store.</summary>
        public string Root { get; }

        public static string ResolveRoot(string? configured, string contentRoot)
        {
            var value = string.IsNullOrWhiteSpace(configured) ? DefaultRelativeRoot : configured.Trim();
            if (Path.IsPathRooted(value) && !Path.IsPathFullyQualified(value))
                throw new FileStoreException($"Dms:Storage:Root '{value}' is rooted but has no drive letter. " +
                                             @"Use a full path such as 'D:\DMS\filestore' or a relative path such as 'App_Data/filestore'.");
            return Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(contentRoot, value));
        }

        // ------------------------------------------------------------------ uploads

        /// <summary>Validates, stores and scans an uploaded file. Throws FileStoreException with a user message.</summary>
        public async Task<StoredFile> SaveUploadAsync(IFormFile file, string category, CancellationToken ct = default, string? allowedExtensionsOverride = null)
        {
            var config = await GetConfigAsync();
            var maxMb = int.TryParse(Get(config, "storage.maxUploadMb", "100"), out var mb) ? mb : 100;
            if (file.Length <= 0) throw new FileStoreException("The uploaded file is empty.");
            if (file.Length > (long)maxMb * 1024 * 1024) throw new FileStoreException($"The file exceeds the {maxMb} MB upload limit.");

            ValidateFormat(file.FileName, allowedExtensionsOverride ?? Get(config, "upload.allowedExtensions", ".pdf,.docx,.xlsx,.pptx"));

            StoredFile stored;
            await using (var content = file.OpenReadStream())
                stored = await SaveAsync(content, Path.GetFileName(file.FileName), file.ContentType, category, ct);

            var scan = await ScanAsync(stored.RelativePath, config, ct);
            stored.VirusScanStatus = scan.Status;
            if (scan.Status == "INFECTED")
            {
                Archive(stored.RelativePath);
                throw new FileStoreException("The uploaded file was rejected by the virus scanner: " + scan.Detail);
            }
            return stored;
        }

        /// <summary>
        /// Phase 5b - a file that did not come through a form (a legacy import package): the same format, size and
        /// virus checks as an upload, then stored write-once.
        /// </summary>
        public async Task<StoredFile> SaveCheckedAsync(Stream content, long length, string fileName, string category, CancellationToken ct = default)
        {
            var config = await GetConfigAsync();
            var maxMb = int.TryParse(Get(config, "storage.maxUploadMb", "100"), out var mb) ? mb : 100;
            if (length <= 0) throw new FileStoreException($"'{fileName}' is empty.");
            if (length > (long)maxMb * 1024 * 1024) throw new FileStoreException($"'{fileName}' exceeds the {maxMb} MB upload limit.");
            ValidateFormat(fileName, Get(config, "upload.allowedExtensions", ".pdf,.docx,.xlsx,.pptx"));
            var stored = await SaveAsync(content, Path.GetFileName(fileName), MimeFromExtension(fileName), category, ct);
            var scan = await ScanAsync(stored.RelativePath, config, ct);
            stored.VirusScanStatus = scan.Status;
            if (scan.Status == "INFECTED")
            {
                Archive(stored.RelativePath);
                throw new FileStoreException($"'{fileName}' was rejected by the virus scanner: " + scan.Detail);
            }
            return stored;
        }

        /// <summary>Format check only, with the configured list (used by a dry run).</summary>
        public async Task CheckFormatAsync(string fileName)
            => ValidateFormat(fileName, Get(await GetConfigAsync(), "upload.allowedExtensions", ".pdf,.docx,.xlsx,.pptx"));

        public static void ValidateFormat(string fileName, string allowedList)
        {
            var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension))
                throw new FileStoreException("The file has no extension, so its format cannot be established.");

            var allowed = (allowedList ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()).ToArray();
            if (allowed.Length == 0 || allowed.Contains(extension)) return;

            var modern = extension switch { ".doc" => ".docx", ".xls" => ".xlsx", ".ppt" => ".pptx", _ => null };
            if (modern != null)
                throw new FileStoreException($"'{fileName}' is in the older binary {extension} format, which this system cannot display " +
                                             $"or convert reliably. Open it and use Save As to produce a {modern} file, then upload that.");
            throw new FileStoreException($"{extension} files are not accepted. Upload one of: {string.Join(", ", allowed)}.");
        }

        // ------------------------------------------------------------------ store

        public async Task<StoredFile> SaveAsync(Stream content, string fileName, string? mimeType, string category, CancellationToken ct = default, long? maxBytes = null)
        {
            Directory.CreateDirectory(Root);
            var safeName = SafeFileName(fileName);
            var now = DateTime.UtcNow;
            var relativePath = $"{SafeSegment(category)}/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}-{safeName}";
            var absolutePath = ResolveAbsolute(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            long size = 0;
            string sha;
            try
            {
                await using (var target = new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    if (content.CanSeek) content.Position = 0;
                    if (maxBytes == null) await content.CopyToAsync(target, ct);
                    else
                    {
                        size = await BoundedStreamCopy.CopyAsync(content,target,maxBytes.Value,ct);
                    }
                    size = target.Length;
                }
                await using var readBack = File.OpenRead(absolutePath);
                sha = Convert.ToHexString(await SHA256.HashDataAsync(readBack, ct)).ToLowerInvariant();
            }
            catch
            {
                // Only this newly-created, incomplete, unreferenced file is removed.
                // Existing immutable document versions are never touched.
                if (File.Exists(absolutePath)) File.Delete(absolutePath);
                throw;
            }

            _logger.LogInformation("Stored {Path} ({Size} bytes, SHA-256 {Sha})", relativePath, size, sha);
            return new StoredFile
            {
                FileName = safeName,
                RelativePath = relativePath,
                MimeType = string.IsNullOrWhiteSpace(mimeType) || mimeType == "application/octet-stream" ? MimeFromExtension(safeName) : mimeType,
                SizeBytes = size,
                Sha256 = sha
            };
        }

        /// <summary>A new, independent copy (e.g. a template file for a new document).</summary>
        public async Task<StoredFile> CopyAsync(string sourceRelativePath, string category, string targetFileName, CancellationToken ct = default)
        {
            await using var source = OpenRead(sourceRelativePath);
            return await SaveAsync(source, targetFileName, MimeFromExtension(targetFileName), category, ct);
        }

        public Stream OpenRead(string relativePath)
        {
            var absolutePath = ResolveAbsolute(relativePath);
            if (!File.Exists(absolutePath))
                throw new FileStoreException($"The stored file '{relativePath}' is not in the file store ({Root}). " +
                                             "Check Dms:Storage:Root - it must point to the folder that holds the documents.");
            return new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        }

        // Call only after the endpoint's live authorization check. Paths are immutable;
        // including database, storage root and file metadata isolates caches across installations.
        public async Task<Stream> OpenCachedReadAsync(string relativePath, CancellationToken ct = default)
        {
            var path = ResolveAbsolute(relativePath);
            var info = new FileInfo(path);
            if (!info.Exists || _cache == null || _cacheLimit <= 0)
                return OpenRead(relativePath);
            var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                relativePath + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks)));
            var key = "source:" + _cacheScope + ":" + fingerprint;
            if (info.Length > _cacheLimit)
                return info.Length <= _largeCacheLimit
                    ? new CachedFileReadStream(OpenRead(relativePath), _cache, key, _logger)
                    : OpenRead(relativePath);
            try
            {
                var bytes = await _cache.GetAsync(key, ct);
                if (bytes != null) return new MemoryStream(bytes, writable: false);
                await using var source = OpenRead(relativePath);
                using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
                await _cache.SetAsync(key, bytes, new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
                { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) }, ct);
                return new MemoryStream(bytes, writable: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not FileStoreException)
            {
                _logger.LogWarning("Document cache unavailable ({ErrorType}); reading durable storage.", ex.GetType().Name);
                return OpenRead(relativePath);
            }
        }

        public bool Exists(string? relativePath)
            => !string.IsNullOrWhiteSpace(relativePath) && File.Exists(ResolveAbsolute(relativePath));

        /// <summary>SHA-256 of the stored file, to prove it is the file that was recorded.</summary>
        public async Task<string> ComputeSha256Async(string relativePath, CancellationToken ct = default)
        {
            await using var stream = OpenRead(relativePath);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        }

        public void Archive(string relativePath)
        {
            var absolutePath = ResolveAbsolute(relativePath);
            if (!File.Exists(absolutePath)) return;
            var archive = ResolveAbsolute($"_archive/{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}-{Path.GetFileName(absolutePath)}");
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            File.Move(absolutePath, archive, false);
        }

        public string ResolveAbsolute(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new FileStoreException("A storage path is required.");
            if (Path.IsPathRooted(relativePath)) throw new FileStoreException($"Absolute paths are not permitted in the file store: '{relativePath}'.");
            var normalisedRoot = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(normalisedRoot, relativePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!candidate.StartsWith(normalisedRoot + Path.DirectorySeparatorChar, comparison))
                throw new FileStoreException($"The path '{relativePath}' resolves outside the file store.");
            return candidate;
        }

        // ------------------------------------------------------------------ helpers

        public static string SafeFileName(string? name)
        {
            var file = Path.GetFileName(name ?? "file");
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
            var cleaned = new string(file.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
            if (cleaned.Length == 0) cleaned = "file";
            if (cleaned.Length > 150)
            {
                var ext = Path.GetExtension(cleaned);
                cleaned = cleaned[..(150 - ext.Length)] + ext;
            }
            return cleaned;
        }

        private static string SafeSegment(string value)
        {
            var cleaned = new string((value ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()).ToLowerInvariant();
            return cleaned.Length == 0 ? "misc" : cleaned;
        }

        public static string MimeFromExtension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".doc" => "application/msword",
            ".xls" => "application/vnd.ms-excel",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };

        private async Task<Dictionary<string, string>> GetConfigAsync()
            => (await _unitOfWork.SystemConfig.GetListAsync())
               .ToDictionary(c => c.config_key ?? "", c => c.config_value ?? "", StringComparer.OrdinalIgnoreCase);

        private static string Get(Dictionary<string, string> config, string key, string fallback)
            => config.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

        private async Task<(string Status, string Detail)> ScanAsync(string relativePath, Dictionary<string, string> config, CancellationToken ct)
        {
            if (!string.Equals(Get(config, "storage.virusScan.enabled", "false"), "true", StringComparison.OrdinalIgnoreCase))
                return ("SKIPPED", "Virus scanning is disabled in configuration.");
            var command = Get(config, "storage.virusScan.command", "");
            if (command.Length == 0) return ("ERROR", "No scanner command is configured.");
            try
            {
                var psi = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(ResolveAbsolute(relativePath));
                using var process = Process.Start(psi)!;
                var stdout = await process.StandardOutput.ReadToEndAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                string Trim(string s) => s.Trim().Length > 500 ? s.Trim()[..500] : s.Trim();
                return process.ExitCode switch
                {
                    0 => ("CLEAN", Trim(stdout)),
                    1 => ("INFECTED", Trim(stdout)),
                    _ => ("ERROR", $"exit {process.ExitCode}: {Trim(stderr)}")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Virus scan failed for {Path}", relativePath);
                return ("ERROR", ex.Message);
            }
        }
    }
}
