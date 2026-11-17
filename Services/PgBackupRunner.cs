//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Backup runner (replaces MySqlBackupService of the old DMS).
 *   DB_FULL  : pg_dump -Fc of the DMS database          -> dms_database.dump
 *   FILES    : zip of the document file store (storage.root) -> filestore.zip
 *   COMBINED : both
 * Every run is written to backup_log (RUNNING -> SUCCESS / FAILED) and to the audit trail,
 * with the SHA-256 of what was written (manifest.sha256 lists every output file).
 * Settings: backup.pgdumpPath, backup.targetPath (system_config); the file store is Dms:Storage:Root (appsettings).
 * The database password is passed to pg_dump through PGPASSWORD, never on the command line.
 */
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using Npgsql;

namespace DMS.Web.Services
{
    public class PgBackupRunner
    {
        private static readonly string[] Types = { "DB_FULL", "FILES", "COMBINED" };
        private static readonly SemaphoreSlim OneAtATime = new(1, 1);

        private readonly IUnitOfWork _unitOfWork;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PgBackupRunner> _logger;
        private readonly FileStore _fileStore;

        public PgBackupRunner(IUnitOfWork unitOfWork, IConfiguration configuration, ILogger<PgBackupRunner> logger, FileStore fileStore)
        {
            _fileStore = fileStore;
            _unitOfWork = unitOfWork;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<DbResult> RunAsync(string backupType, string triggerSource, int? actor, string? ip, CancellationToken ct)
        {
            backupType = (backupType ?? "").Trim().ToUpperInvariant();
            if (!Types.Contains(backupType)) return DbResult.Fail("Backup type must be DB_FULL, FILES or COMBINED.");

            if (!await OneAtATime.WaitAsync(0, ct)) return DbResult.Fail("A backup is already running. Wait for it to finish.");
            try
            {
                var config = (await _unitOfWork.SystemConfig.GetListAsync())
                    .ToDictionary(c => c.config_key ?? "", c => c.config_value ?? "", StringComparer.OrdinalIgnoreCase);
                string Setting(string key, string fallback) =>
                    config.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

                var targetRoot = Setting("backup.targetPath", Path.Combine(AppContext.BaseDirectory, "backups"));
                var pgDump = Setting("backup.pgdumpPath", "pg_dump");
                var storeRoot = _fileStore.Root;

                long id = await _unitOfWork.BackupLog.StartAsync(backupType, triggerSource, actor);
                string folder = Path.Combine(targetRoot, $"DMS_{backupType}_{DateTime.Now:yyyyMMdd_HHmmss}");
                try
                {
                    Directory.CreateDirectory(folder);
                    var outputs = new List<string>();
                    long fileCount = 0;

                    if (backupType is "DB_FULL" or "COMBINED")
                    {
                        var dump = Path.Combine(folder, "dms_database.dump");
                        await RunPgDumpAsync(pgDump, dump, ct);
                        outputs.Add(dump);
                    }

                    if (backupType is "FILES" or "COMBINED")
                    {
                        if (string.IsNullOrWhiteSpace(storeRoot) || !Directory.Exists(storeRoot))
                            throw new InvalidOperationException($"The file store folder (Dms:Storage:Root = '{storeRoot}') does not exist on this server.");
                        var zip = Path.Combine(folder, "filestore.zip");
                        await Task.Run(() => ZipFile.CreateFromDirectory(storeRoot, zip, CompressionLevel.Optimal, false), ct);
                        fileCount = Directory.EnumerateFiles(storeRoot, "*", SearchOption.AllDirectories).LongCount();
                        outputs.Add(zip);
                    }

                    // manifest: sha256 of every output; the log row carries the sha of the manifest
                    var manifest = new StringBuilder();
                    long size = 0;
                    foreach (var file in outputs)
                    {
                        manifest.Append(await Sha256Async(file, ct)).Append("  ").Append(Path.GetFileName(file)).Append('\n');
                        size += new FileInfo(file).Length;
                    }
                    var manifestPath = Path.Combine(folder, "manifest.sha256");
                    await File.WriteAllTextAsync(manifestPath, manifest.ToString(), ct);
                    var sha = outputs.Count == 1 ? manifest.ToString()[..64] : await Sha256Async(manifestPath, ct);

                    await _unitOfWork.BackupLog.FinishAsync(id, "SUCCESS", folder, size, fileCount, sha, null, actor, ip);
                    return DbResult.Ok(id, "The backup has completed.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backup {Id} failed", id);
                    var message = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    await _unitOfWork.BackupLog.FinishAsync(id, "FAILED", folder, null, null, null, message, actor, ip);
                    return DbResult.Fail("The backup failed: " + message);
                }
            }
            finally
            {
                OneAtATime.Release();
            }
        }

        private async Task RunPgDumpAsync(string pgDump, string outputFile, CancellationToken ct)
        {
            var cs = new NpgsqlConnectionStringBuilder(_configuration.GetConnectionString("DMSConnection"));
            var psi = new ProcessStartInfo
            {
                FileName = pgDump,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in new[]
                     {
                         "--format=custom", "--no-owner", "--no-privileges",
                         "--host=" + cs.Host, "--port=" + cs.Port, "--username=" + cs.Username,
                         "--dbname=" + cs.Database, "--file=" + outputFile
                     })
                psi.ArgumentList.Add(arg);
            psi.Environment["PGPASSWORD"] = cs.Password ?? "";

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"pg_dump could not be started ('{pgDump}'). Set backup.pgdumpPath in System Configuration to the full path of pg_dump. {ex.Message}");
            }

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* already gone */ }
                throw new TimeoutException("pg_dump did not finish within 60 minutes.");
            }
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"pg_dump exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        private static async Task<string> Sha256Async(string path, CancellationToken ct)
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
