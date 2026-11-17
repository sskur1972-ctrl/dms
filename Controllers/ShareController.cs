//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 4 - the anonymous, view-only link to a document in force (URS-7.23, "the SAP link").
 * The token in the URL is the whole of the authorisation: it is hashed and looked up; expiry, withdrawal,
 * the kill switch (share.link.enabled) and "still in force" are checked on EVERY request, and every opening is
 * written to the audit trail. Only the watermarked controlled copy is ever served - never the authored file.
 * Framing is limited to share.link.frameAncestors.
 */
using System.Security.Cryptography;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    [AllowAnonymous]
    [Route("s")]
    public class ShareController : Controller
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly FileStore _fileStore;
        private readonly IConfigService _config;

        public ShareController(IUnitOfWork unitOfWork, FileStore fileStore, IConfigService config)
        {
            _unitOfWork = unitOfWork; _fileStore = fileStore; _config = config;
        }

        private string? ClientIp => HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                                    ?? HttpContext.Connection.RemoteIpAddress?.ToString();

        private static string Hash(string? token)
            => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token ?? ""))).ToLowerInvariant();

        private async Task SecureAsync()
        {
            var ancestors = (await _config.GetStringAsync(ConfigKeys.ShareLinkFrameAncestors, "") ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(o => Uri.TryCreate(o, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
                .Select(o => o.TrimEnd('/'));
            Response.Headers.Remove("X-Frame-Options");
            Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; object-src 'self'; frame-ancestors 'self' " + string.Join(' ', ancestors);
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        }

        [HttpGet("{token}")]
        public async Task<IActionResult> Open(string token)
        {
            await SecureAsync();
            var resolved = await _unitOfWork.Rendition.ResolveShareAsync(Hash(token)) ?? new ShareResolution();
            if (resolved.status == "OK" && resolved.link_id is long id)
                await _unitOfWork.Rendition.RecordShareViewAsync(id, ClientIp, $"{resolved.document_number} rev {resolved.version_label}");
            resolved.Token = token;
            Response.StatusCode = resolved.status is "OK" or "NO_RENDITION" ? 200 : 404;
            return View("~/Views/Share/View.cshtml", resolved);
        }

        [HttpGet("{token}/pdf")]
        public async Task<IActionResult> Pdf(string token)
        {
            await SecureAsync();
            var resolved = await _unitOfWork.Rendition.ResolveShareAsync(Hash(token));
            if (resolved?.status != "OK" || resolved.rendered_pdf_path == null) return NotFound();
            try
            {
                Response.Headers["Content-Disposition"] = "inline";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(await _fileStore.OpenCachedReadAsync(resolved.rendered_pdf_path), "application/pdf");
            }
            catch (FileStoreException)
            {
                return NotFound();
            }
        }
    }
}
