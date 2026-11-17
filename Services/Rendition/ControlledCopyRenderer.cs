//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5a - one numbered printed copy (URS-7.14): the approved BASE PDF of the revision, stamped on the fly with
 *   - the watermark CONTROLLED COPY (or UNCONTROLLED COPY - FOR INFORMATION ONLY),
 *   - the copy number on top of every page ("SOP-QA-26-004-R00-CC-0001 · copy 1 of 2 · issued to ... · return by ..."),
 *   - the footer with document, revision, print time and page x of y,
 *   - the signature page.
 * The PDF is not kept: its SHA-256 goes into print_log, so a copy found on the floor can be matched to its print.
 */
using System.Security.Cryptography;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Web.Services.Pdf;

namespace DMS.Web.Services.Rendition
{
    public sealed record ControlledCopyPdf(byte[] Bytes, string Sha256, string Watermark, string FileName);

    public sealed class ControlledCopyRenderer
    {
        public const string ControlledWatermark = "CONTROLLED COPY";
        public const string UncontrolledWatermark = "UNCONTROLLED COPY - FOR INFORMATION ONLY";

        private readonly IUnitOfWork _unitOfWork;
        private readonly FileStore _fileStore;
        private readonly IConfigService _config;

        public ControlledCopyRenderer(IUnitOfWork unitOfWork, FileStore fileStore, IConfigService config)
        {
            _unitOfWork = unitOfWork; _fileStore = fileStore; _config = config;
        }

        public async Task<ControlledCopyPdf> RenderAsync(ControlledCopyContext copy, string printedBy, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(copy.rendition_base_path))
                throw new RenditionException("The PDF rendition of this revision is not available.", permanent: true);
            byte[] basePdf;
            await using (var s = await _fileStore.OpenCachedReadAsync(copy.rendition_base_path, ct))
            using (var m = new MemoryStream())
            {
                await s.CopyToAsync(m, ct);
                basePdf = m.ToArray();
            }
            // the bytes must still be the approved ones
            if (!string.IsNullOrEmpty(copy.rendition_base_sha256)
                && !string.Equals(Convert.ToHexString(SHA256.HashData(basePdf)), copy.rendition_base_sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new RenditionException("The stored PDF of this revision does not match its recorded SHA-256. Printing is refused; tell QA.", permanent: true);

            var controlled = !string.Equals(copy.copy_type, "UNCONTROLLED", StringComparison.OrdinalIgnoreCase);
            var watermark = controlled ? ControlledWatermark : UncontrolledWatermark;
            var tz = await _config.GetStringAsync(ConfigKeys.TimeZoneId, "Asia/Kolkata", ct);
            var local = RenditionProcessor.ToLocal(DateTime.UtcNow, tz);
            var browserCopy = copy.copy_id == 0;   // printed by a reader from the Read page: no request, no number
            var header = browserCopy
                ? $"Uncontrolled copy · printed by {printedBy} on {local:dd-MMM-yyyy HH:mm} · valid on the day of printing only"
                : $"{(controlled ? "Controlled copy" : "Uncontrolled copy")} {copy.copy_control_number} · copy {copy.copy_index} of {copy.copies_authorized ?? copy.copy_index}"
                         + (string.IsNullOrWhiteSpace(copy.issued_to_name) ? "" : $" · issued to {copy.issued_to_name}")
                         + (controlled && copy.return_due_on != null ? $" · return by {copy.return_due_on:dd-MMM-yyyy}" : "");
            var options = new PdfStampOptions
            {
                Watermark = watermark,
                Header = header,
                Footer = $"{copy.document_number} rev {copy.version_label} · {copy.title} · printed {local:dd-MMM-yyyy HH:mm} by {printedBy}"
                         + (browserCopy ? "" : $" · {copy.request_number}") + " · page {page} of {pages}",
                SignatureTitle = "Electronic signatures",
                SignatureHeader =
                {
                    $"{copy.document_number} - {copy.title}",
                    $"Revision {copy.version_label}" + (copy.effective_from == null ? "" : $" · effective from {copy.effective_from:dd-MMM-yyyy}"),
                    browserCopy ? $"Uncontrolled copy printed by {printedBy} on {local:dd-MMM-yyyy HH:mm}"
                                : $"{(controlled ? "Controlled" : "Uncontrolled")} copy {copy.copy_control_number} ({copy.request_number})"
                },
                SignatureNote = controlled
                    ? "This is a numbered controlled copy issued under a QA-authorised print request. Check before use that it is the revision in force; " +
                      "return it to QA when it is superseded."
                    : "Uncontrolled copy for information only. It is not kept up to date - check the DMS for the revision in force before use."
            };
            if (await _config.GetBoolAsync(ConfigKeys.RenditionSignaturePage, true, ct))
            {
                var signatures = await _unitOfWork.Workflow.GetSignaturesAsync(copy.document_id);
                options.Signatures.AddRange(signatures.Where(s => s.document_version_id == copy.version_id && !string.IsNullOrWhiteSpace(s.manifestation_text)
                                                                  && s.action_code != "PRINT_AUTHORIZE")
                                                      .Select(s => s.manifestation_text!));
            }
            var pdf = PdfStamper.Stamp(basePdf, options);
            var sha = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant();
            return new ControlledCopyPdf(pdf, sha, watermark, browserCopy ? $"{copy.document_number}-{copy.version_label}-uncontrolled.pdf" : $"{copy.copy_control_number}.pdf");
        }
    }
}
