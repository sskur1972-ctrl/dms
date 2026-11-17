using DMS.DataAccess.Repository.IRepository;
using DMS.Web.Services.Pdf;

namespace DMS.Web.Services.Rendition
{
       public sealed class RenditionProcessor
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly FileStore _fileStore;
        private readonly IConfigService _config;
        private readonly LibreOfficeConverter _converter;
        private readonly EuroOfficePdfConverter _euroOffice;
        private readonly ILogger<RenditionProcessor> _logger;

        public RenditionProcessor(IUnitOfWork unitOfWork, FileStore fileStore, IConfigService config, LibreOfficeConverter converter,
                                  ILogger<RenditionProcessor> logger, EuroOfficePdfConverter euroOffice)
        {
            _unitOfWork = unitOfWork;
            _fileStore = fileStore;
            _config = config;
            _converter = converter;
            _logger = logger;
            _euroOffice = euroOffice;
        }

       
       
        public Task<ConverterStatus> ConverterStatusAsync(CancellationToken ct = default)
            => ProbeAsync(ct);

        private async Task<ConverterStatus> ProbeAsync(CancellationToken ct)
        {
            var provider = (await _config.GetStringAsync("rendition.provider", "EUROOFFICE", ct))?.ToUpperInvariant();
            return provider switch
            {
                "EUROOFFICE" => await _euroOffice.ProbeAsync(ct),
                "LIBREOFFICE" => await _converter.ProbeAsync(await _config.GetStringAsync(ConfigKeys.RenditionLibreOfficePath, null, ct), ct),
                _ => new(false, null, provider, "Choose LIBREOFFICE or EUROOFFICE in rendition.provider.")
            };

        }

        /// <summary>Claims and processes up to <paramref name="max"/> jobs. Returns how many were handled.</summary>
        public async Task<int> RunBatchAsync(int max, CancellationToken ct)
        {
            await _unitOfWork.Rendition.RecoverUnavailableAsync();
            var handled = 0;
            for (var i = 0; i < max && !ct.IsCancellationRequested; i++)
            {
                var claim = await _unitOfWork.Rendition.ClaimAsync();
                if (claim == null) break;
                var available = await ProcessAsync(claim.id, ct);
                handled++;
                if (!available) break; // Avoid a tight loop while Docker/the converter is offline.
            }
            return handled;
        }

        public async Task<bool> ProcessAsync(long jobId, CancellationToken ct)
        {
            var job = await _unitOfWork.Rendition.GetJobContextAsync(jobId);
            if (job == null) { await _unitOfWork.Rendition.FailAsync(jobId, "The job's revision no longer exists.", true); return true; }
            try
            {
                // ---- 1. the base PDF
                string basePath;
                if (job.base_ready && job.rendition_base_path != null)
                {
                    basePath = job.rendition_base_path;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(job.source_storage_path))
                        throw new RenditionException("The revision has no file to convert.", permanent: true);
                    var isPdf = string.Equals(Path.GetExtension(job.source_file_name), ".pdf", StringComparison.OrdinalIgnoreCase)
                                || (job.source_mime_type ?? "").Contains("pdf", StringComparison.OrdinalIgnoreCase);
                    if (isPdf)
                    {
                        var bytes = await ReadAsync(job.source_storage_path, ct);
                        PdfStamper.CountPages(bytes);
                        basePath = job.source_storage_path;
                        await _unitOfWork.Rendition.SetBaseAsync(job.version_id, basePath, job.source_sha256 ?? "", job.source_sha256, "PASSTHROUGH");
                    }
                    else
                    {
                        if (!LibreOfficeConverter.CanConvert(job.source_file_name))
                            throw new RenditionException($"{Path.GetExtension(job.source_file_name)} files cannot be converted to PDF. Attach a PDF rendition by hand (Details > PDF rendition).", permanent: true);
                        var status = await ProbeAsync(ct);
                        if (!status.IsAvailable || status.Path == null)
                            throw new RenditionDependencyException(status.Message);
                        var timeout = await _config.GetIntAsync(ConfigKeys.RenditionTimeoutSeconds, 180, ct);
                        byte[] pdf;
                        await using (var source = await _fileStore.OpenCachedReadAsync(job.source_storage_path, ct))
                        {
                              pdf = status.Version switch
                            {
                                "EUROOFFICE" => await _euroOffice.ConvertAsync(source, job.source_file_name ?? "source.docx", timeout, ct),
                                _ => await _converter.ConvertAsync(status.Path, source, job.source_file_name ?? "source.docx", timeout, ct)
                            };

                        }
                        PdfStamper.CountPages(pdf);
                        var stored = await _fileStore.SaveAsync(new MemoryStream(pdf), $"{job.document_number}-{job.version_label}-base.pdf",
                                                                "application/pdf", "renditions", ct);
                        basePath = stored.RelativePath;
                        await _unitOfWork.Rendition.SetBaseAsync(job.version_id, stored.RelativePath, stored.Sha256, job.source_sha256, "CONVERTED");
                    }
                }

                // ---- 2. the controlled copy for the state
                var baseBytes = await ReadAsync(basePath, ct);
                var watermark = job.watermark_text ?? "";
                var tz = await _config.GetStringAsync(ConfigKeys.TimeZoneId, "Asia/Kolkata", ct);
                var local = ToLocal(DateTime.UtcNow, tz);
                var options = new PdfStampOptions
                {
                    Watermark = watermark,
                    Footer = $"{job.document_number} rev {job.version_label} · {job.title} · {(string.IsNullOrEmpty(watermark) ? job.state_name : watermark)}"
                             + $" · produced {local:dd-MMM-yyyy HH:mm} · page {{page}} of {{pages}}",
                    SignatureTitle = "Electronic signatures",
                    SignatureHeader =
                    {
                        $"{job.document_number} - {job.title}",
                        $"Revision {job.version_label} · {job.state_name}" + (job.effective_from == null ? "" : $" · effective from {job.effective_from:dd-MMM-yyyy}")
                    },
                    SignatureNote = "This page is generated by the DMS. Each signature was applied by re-entering the signer's user ID and password; " +
                                    "the signature records are held in the system with its audit trail (21 CFR Part 11)."
                };
                if (job.state_code is "PUBLISHED" or "EFFECTIVE" || watermark.Contains("MASTER", StringComparison.OrdinalIgnoreCase)
                    || await _config.GetBoolAsync(ConfigKeys.RenditionSignaturePage, true, ct))
                {
                    var signatures = await _unitOfWork.Workflow.GetSignaturesAsync(job.document_id);
                    options.Signatures.AddRange(signatures.Where(s => s.document_version_id == job.version_id && !string.IsNullOrWhiteSpace(s.manifestation_text)
                                                                      && s.action_code != "PRINT_AUTHORIZE")
                                                         .Select(s => s.manifestation_text!));
                }
                var stamped = PdfStamper.Stamp(baseBytes, options);
                var copy = await _fileStore.SaveAsync(new MemoryStream(stamped), $"{job.document_number}-{job.version_label}-{job.state_code}.pdf",
                                                      "application/pdf", "renditions", ct);
                var done = await _unitOfWork.Rendition.CompleteAsync(jobId, copy.RelativePath, copy.Sha256, watermark);
                if (!done.Success) throw new RenditionException(done.Message, permanent: false);

                // Keep your existing return true + catch blocks and ReadAsync/ToLocal methods here
                               return true;
            }
            catch (RenditionDependencyException ex)
            {
                _logger.LogWarning(ex, "Rendition job {JobId} deferred until its converter recovers", jobId);
                await _unitOfWork.Rendition.DeferUnavailableAsync(jobId, ex.Message);
                return false;
            }
            catch (RenditionException ex)
            {
                _logger.LogWarning(ex, "Rendition job {JobId} failed (permanent={Permanent})", jobId, ex.Permanent);
                await _unitOfWork.Rendition.FailAsync(jobId, ex.Message, ex.Permanent);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Rendition job {JobId} failed unexpectedly", jobId);
                await _unitOfWork.Rendition.FailAsync(jobId, ex.Message, false);
                return true;
            }
        }

        private async Task<byte[]> ReadAsync(string relativePath, CancellationToken ct)
        {
            await using var stream = await _fileStore.OpenCachedReadAsync(relativePath, ct);
            await using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }

        internal static DateTime ToLocal(DateTime utc, string? timeZoneId)
        {
            if (string.IsNullOrWhiteSpace(timeZoneId)) return utc;
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
            }
            catch
            {
                return utc;
            }
        }
    }

    public class RenditionException : Exception
    {
        public bool Permanent { get; }
        public RenditionException(string message, bool permanent = false) : base(message)
            => Permanent = permanent;
    }

    public sealed class RenditionDependencyException : RenditionException
    {
        public RenditionDependencyException(string message) : base(message, permanent: false) { }
    }
}
