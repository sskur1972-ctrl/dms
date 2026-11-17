//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 4 - the PDF rendition of a revision (URS-7.18, 7.20) and external view links (URS-7.23).
 * Ported from Bcl.Dms DocumentsController (Preview, AttachRendition, CreateShareLink, RevokeShareLink).
 *
 * There is deliberately no "download" here, as in the old DMS: the controlled copy is streamed inline for the
 * reader; a copy on paper goes through the QA-authorised print request (Phase 5).
 */
using System.Security.Cryptography;
using DMS.Model.Documents;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.Rendition;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public partial class DocumentsController
    {
        public const string RenditionPermission = "Manage PDF rendition";          // document.rendition.manage
        public const string SharePermission = "Manage external view links";        // document.share.manage

        /// <summary>The controlled copy (watermarked PDF) of a revision, inline for the reader page.</summary>
        //[HttpGet]
        //public async Task<IActionResult> RenditionPdf(long versionId, long id)
        //{
        //    if (!HasPermission(DocumentPermissions.View)) return NoPermission();
        //    var access = await _unitOfWork.Document.GetAccessAsync(id, CurrentUserId);
        //    var version = (await _unitOfWork.Document.GetVersionsAsync(id)).FirstOrDefault(v => v.id == versionId);
        //    if (!access.can_view_content || version == null) return Forbid();
        //    var status = await _unitOfWork.Rendition.GetStatusAsync(versionId);
        //    if (status?.rendered_pdf_path == null) return NotFound();
        //    try
        //    {
        //        Response.Headers["Content-Disposition"] = "inline";
        //        Response.Headers["Cache-Control"] = "no-store";
        //        Response.Headers["X-Content-Type-Options"] = "nosniff";
        //        return File(await _fileStore.OpenCachedReadAsync(status.rendered_pdf_path), "application/pdf");
        //    }
        //    catch (FileStoreException)
        //    {
        //        return NotFound();
        //    }
        //}



        [HttpGet]
        public async Task<IActionResult> RenditionPdf(long versionId, long id)
        {
            if (!HasPermission(DocumentPermissions.View))
                return NoPermission();

            var access = await _unitOfWork.Document.GetAccessAsync(id, CurrentUserId);

            var version = (await _unitOfWork.Document.GetVersionsAsync(id))
                .FirstOrDefault(v => v.id == versionId);

            if (!access.can_view_content || version == null)
                return Forbid();

            var status = await _unitOfWork.Rendition.GetStatusAsync(versionId);

            if (string.IsNullOrWhiteSpace(status?.rendered_pdf_path) || !status.IsCurrent || !status.base_ready)
                return NotFound();

            try
            {
                Response.Headers["Content-Disposition"] = "inline";
                Response.Headers["Cache-Control"] = "no-store";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(await _fileStore.OpenCachedReadAsync(status.rendered_pdf_path), "application/pdf", enableRangeProcessing: true);
            }
            catch (FileStoreException)
            {
                return NotFound();
            }
        }



        /// <summary>Queue the controlled copy of a revision now.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestRendition(long documentId, long versionId)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            var access = await _unitOfWork.Document.GetAccessAsync(documentId, CurrentUserId);
            if (!HasPermission(RenditionPermission) && !access.is_owner && !access.holds_open_stage) return NoPermission();
            var result = await _unitOfWork.Rendition.RequestAsync(versionId, CurrentUserId);
            Notify(result, result.Message);
            if (result.Success) RenditionWorker.Nudge();
            return Redirect(Url.Action(nameof(Details), new { id = documentId }) + "#dms-rendition");
        }

        [HttpGet]
        public async Task<IActionResult> AttachRendition(long id, long versionId)
        {
            if (!HasPermission(RenditionPermission)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(id);
            var version = document == null ? null : (await _unitOfWork.Document.GetVersionsAsync(id)).FirstOrDefault(v => v.id == versionId);
            if (document == null || version == null) return NotFound();
            ViewBag.document = document;
            ViewBag.converter = await HttpContext.RequestServices.GetRequiredService<RenditionProcessor>().ConverterStatusAsync();
            return View(version);
        }

        [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(104_857_600)]
        public async Task<IActionResult> AttachRendition(long id, long versionId, IFormFile? file)
        {
            if (!HasPermission(RenditionPermission)) return NoPermission();
            if (file == null || file.Length == 0 || !string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                NotifyError("Choose the PDF to attach.");
                return RedirectToAction(nameof(AttachRendition), new { id, versionId });
            }
            byte[] bytes;
            await using (var s = file.OpenReadStream()) { using var m = new MemoryStream(); await s.CopyToAsync(m); bytes = m.ToArray(); }
            try
            {
                DMS.Web.Services.Pdf.PdfStamper.CountPages(bytes);   // it must be a PDF the stamper can work with
            }
            catch (DMS.Web.Services.Pdf.PdfException ex)
            {
                NotifyError("This PDF cannot be used: " + ex.Message);
                return RedirectToAction(nameof(AttachRendition), new { id, versionId });
            }
            var stored = await _fileStore.SaveAsync(new MemoryStream(bytes), file.FileName, "application/pdf", "renditions");
            var result = await _unitOfWork.Rendition.AttachManualAsync(versionId, stored, CurrentUserId, ClientIp);
            if (!result.Success) _fileStore.Archive(stored.RelativePath);
            Notify(result, result.Message);
            if (result.Success) RenditionWorker.Nudge();
            return Redirect(Url.Action(nameof(Details), new { id }) + "#dms-rendition");
        }

        // ------------------------------------------------------------------ external view links (URS-7.23)

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateShareLink(long documentId, int? days, string? purpose, string? note, bool pinCurrent)
        {
            if (!HasPermission(SharePermission)) return NoPermission();
            // the token is shown ONCE; only its SHA-256 is stored (same contract as an API key)
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token))).ToLowerInvariant();
            var result = await _unitOfWork.Rendition.CreateShareLinkAsync(documentId, pinCurrent, days, purpose, note, sha, token[..6], CurrentUserId, ClientIp);
            if (result.Success)
            {
                TempData["ShareLinkUrl"] = Url.Action("Open", "Share", new { token }, Request.Scheme, Request.Host.Value);
                NotifySuccess("The external view link has been created. Copy it now - it is shown only once.");
            }
            else NotifyError(result.Message);
            return Redirect(Url.Action(nameof(Details), new { id = documentId }) + "#dms-share");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokeShareLink(long documentId, long linkId, string? reason)
        {
            if (!HasPermission(SharePermission)) return NoPermission();
            Notify(await _unitOfWork.Rendition.RevokeShareLinkAsync(linkId, reason, CurrentUserId, ClientIp),
                   "The link has been withdrawn. It stops working immediately.");
            return Redirect(Url.Action(nameof(Details), new { id = documentId }) + "#dms-share");
        }
    }
}
