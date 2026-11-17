//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 4 - Word / Excel / PowerPoint -> PDF with LibreOffice in headless mode (URS-7.18).
 * Replaces LibreOfficePdfConverter of the old DMS. One conversion at a time, each with its own throw-away
 * LibreOffice profile, stopped after rendition.timeoutSeconds.
 */
using System.Diagnostics;
using System.IO.Compression;
using System.Xml;

namespace DMS.Web.Services.Rendition
{
    public sealed record ConverterStatus(bool IsAvailable, string? Path, string? Version, string Message);

    public sealed class LibreOfficeConverter
    {
        private readonly SemaphoreSlim _probeGate = new(1, 1);
        private ConverterStatus? _lastProbe;
        private string? _lastProbePath;
        private DateTime _probedAt;
        private static readonly SemaphoreSlim OneAtATime = new(1, 1);
        private static readonly string[] Convertible =
        {
            ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt", ".txt",
            ".xls", ".xlsx", ".xlsm", ".ods", ".csv",
            ".ppt", ".pptx", ".pptm", ".odp"
        };

        private readonly ILogger<LibreOfficeConverter> _logger;
        public LibreOfficeConverter(ILogger<LibreOfficeConverter> logger) => _logger = logger;

        public static bool CanConvert(string? fileName)
            => Convertible.Contains(Path.GetExtension(fileName ?? "").ToLowerInvariant());

        public static string? FindSoffice(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured.Trim())) return ConsoleLauncher(configured.Trim());
            var candidates = new List<string>();
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                                         Environment.GetEnvironmentVariable("ProgramW6432") })
                if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "LibreOffice", "program", "soffice.exe"));
            candidates.AddRange(new[]
            {
                "/usr/bin/soffice", "/usr/local/bin/soffice", "/usr/lib/libreoffice/program/soffice", "/snap/bin/libreoffice",
                "/Applications/LibreOffice.app/Contents/MacOS/soffice"
            });
            if (Directory.Exists("/opt"))
                candidates.AddRange(Directory.GetDirectories("/opt", "libreoffice*").Select(d => Path.Combine(d, "program", "soffice")));
            var candidate=candidates.FirstOrDefault(File.Exists);
            return candidate==null?null:ConsoleLauncher(candidate);
        }

        private static string ConsoleLauncher(string path)
        {
            // LibreOffice documents soffice.com as its Windows CLI launcher.
            if(OperatingSystem.IsWindows() && Path.GetFileName(path).Equals("soffice.exe",StringComparison.OrdinalIgnoreCase)){
                var console=Path.ChangeExtension(path,"com");if(File.Exists(console))return console;
            }
            return path;
        }

        public async Task<ConverterStatus> ProbeAsync(string? configured, CancellationToken ct = default)
        {
            await _probeGate.WaitAsync(ct);
            try {
                if (_lastProbe != null && _lastProbePath == configured && DateTime.UtcNow - _probedAt < TimeSpan.FromMinutes(2)) return _lastProbe;
                var result = await ProbeCoreAsync(configured, ct);
                _lastProbe = result; _lastProbePath = configured; _probedAt = DateTime.UtcNow;
                return result;
            } finally { _probeGate.Release(); }
        }

        private async Task<ConverterStatus> ProbeCoreAsync(string? configured, CancellationToken ct)
        {
            var path = FindSoffice(configured);
            if (path == null)
                return new ConverterStatus(false, null, null,
                    string.IsNullOrWhiteSpace(configured)
                        ? "LibreOffice was not found on this server. Install it, or set rendition.libreOfficePath to the full path of soffice(.exe)."
                        : $"rendition.libreOfficePath '{configured}' does not exist on this server.");
            try
            {
                var (exit, output) = await RunAsync(path, new[] { "--headless", "--nologo", "--version" }, null, TimeSpan.FromSeconds(10), ct);
                var version = output.Split('\n').FirstOrDefault(l => l.Contains("LibreOffice", StringComparison.OrdinalIgnoreCase))?.Trim();
                return exit == 0 || version != null
                    ? new ConverterStatus(true, path, version, "LibreOffice is available.")
                    : new ConverterStatus(false, path, null, $"LibreOffice at {path} did not start (exit code {exit}).");
            }
            catch (Exception ex)
            {
                return new ConverterStatus(false, path, null, $"LibreOffice at {path} could not be started: {ex.Message}");
            }
        }

        /// <summary>The PDF of the file, or an exception saying why not.</summary>
        public async Task<byte[]> ConvertAsync(string sofficePath, Stream source, string fileName, int timeoutSeconds, CancellationToken ct)
        {
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!CanConvert(fileName)) throw new InvalidOperationException($"{extension} files cannot be converted to PDF.");
            await OneAtATime.WaitAsync(ct);
            var work = Path.Combine(Path.GetTempPath(), "dms-rendition-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(work);
                var input = Path.Combine(work, "source" + extension);
                await using (var file = File.Create(input)) await source.CopyToAsync(file, ct);
                var profile = new Uri(Path.Combine(work, "profile") + Path.DirectorySeparatorChar).AbsoluteUri;
                var outDir = Path.Combine(work, "out");
                Directory.CreateDirectory(outDir);
                var (exit, output) = await RunAsync(sofficePath, new[]
                {
                    "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck", "--nofirststartwizard",
                    "-env:UserInstallation=" + profile, "--convert-to", "pdf", "--outdir", outDir, input
                }, work, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 15, 1800)), ct);
                var pdf = Path.Combine(outDir, "source.pdf");
                if (!File.Exists(pdf))
                    throw new InvalidOperationException($"LibreOffice produced no PDF (exit code {exit}). {Trim(output)}");
                return await File.ReadAllBytesAsync(pdf, ct);
            }
            finally
            {
                OneAtATime.Release();
                try { Directory.Delete(work, true); } catch (Exception ex) { _logger.LogDebug(ex, "Could not remove {Dir}", work); }
            }
        }

        /// <summary>Imports a PDF as Writer text, then exports it as DOCX. Scanned pages need OCR.</summary>
        public async Task<byte[]> ConvertPdfToWordAsync(string? configuredPath, Stream source, int timeoutSeconds, CancellationToken ct)
        {
            var sofficePath = FindSoffice(configuredPath);
            if (sofficePath == null)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(configuredPath)
                    ? "LibreOffice is not installed on the application server. Install it or set rendition.libreOfficePath."
                    : $"LibreOffice was not found at rendition.libreOfficePath '{configuredPath}'.");

            await OneAtATime.WaitAsync(ct);
            var work = Path.Combine(Path.GetTempPath(), "dms-pdf-to-word-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(work);
                var input = Path.Combine(work, "source.pdf");
                await using (var file = File.Create(input)) await source.CopyToAsync(file, ct);
                await using (var check = File.OpenRead(input))
                {
                    var signature = new byte[5];
                    if (await check.ReadAsync(signature, ct) != signature.Length || !signature.AsSpan().SequenceEqual("%PDF-"u8))
                        throw new InvalidOperationException("The uploaded file is not a valid PDF.");
                }
                var profile = new Uri(Path.Combine(work, "profile") + Path.DirectorySeparatorChar).AbsoluteUri;
                var outDir = Path.Combine(work, "out");
                Directory.CreateDirectory(outDir);
                var (exit, output) = await RunAsync(sofficePath, new[]
                {
                    "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck", "--nofirststartwizard",
                    "-env:UserInstallation=" + profile, "--infilter=writer_pdf_import", "--convert-to", "docx", "--outdir", outDir, input
                }, work, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 15, 1800)), ct);

                var docx = Path.Combine(outDir, "source.docx");
                if (!File.Exists(docx))
                    throw new InvalidOperationException($"LibreOffice could not import this PDF into Word (exit code {exit}). {Trim(output)}");
                var result = await File.ReadAllBytesAsync(docx, ct);
                if (!IsDocx(result)) throw new InvalidOperationException("LibreOffice did not produce a valid DOCX file.");
                return result;
            }
            finally
            {
                OneAtATime.Release();
                try { Directory.Delete(work, true); } catch (Exception ex) { _logger.LogDebug(ex, "Could not remove {Dir}", work); }
            }
        }

        /// <summary>Returns false for image-only Writer imports so callers can OCR the original PDF.</summary>
        public static bool HasExtractableWordText(byte[] docx)
        {
            try
            {
                using var buffer = new MemoryStream(docx, writable: false);
                using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
                var entry = archive.GetEntry("word/document.xml");
                if (entry == null || entry.Length > 16 * 1024 * 1024) return false;
                using var stream = entry.Open();
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
                var characterCount = 0;
                while (reader.Read())
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                        characterCount += reader.ReadElementContentAsString().Count(char.IsLetterOrDigit);
                return characterCount >= 20;
            }
            catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or ArgumentException)
            {
                return false;
            }
        }

        private static bool IsDocx(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4b && bytes[2] == 0x03 && bytes[3] == 0x04;

        private static string Trim(string text) => text.Length > 400 ? text[..400] : text;

        private static async Task<(int Exit, string Output)> RunAsync(string exe, IEnumerable<string> args, string? workingDirectory,
                                                                      TimeSpan timeout, CancellationToken ct)
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true,
                WorkingDirectory = workingDirectory ?? Path.GetTempPath()
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The converter process did not start.");
            // Windows soffice launchers may wait for Enter after --version. EOF
            // prevents a headless service from hanging on an inherited console.
            process.StandardInput.Close();
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timer.Token);
            var stderr = process.StandardError.ReadToEndAsync(timer.Token);
            try
            {
                await process.WaitForExitAsync(timer.Token);
                return (process.ExitCode, (await stdout) + (await stderr));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"The conversion took longer than {timeout.TotalSeconds:0} seconds and was stopped.");
            }
        }
    }
}
