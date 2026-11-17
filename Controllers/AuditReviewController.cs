//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5b - the GxP audit trail (21 CFR 11.10(e), EU GMP Annex 11 cl. 9). Ported from Bcl.Dms AuditTrailService:
 * search, one entry with its before / after values, the hash-chain check, the export for an inspector and the
 * recorded periodic review. (AuditTrailController is the login / activity log of the user module and stays as it is.)
 * The trail is read-only here - nothing can change or remove an entry (append-only triggers in the database).
 */
using System.Text.Json;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.Export;
using DMS.Web.Services.Authoring;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class AuditReviewController : DmsBaseController
    {
        private readonly IConfigService _config;

        public AuditReviewController(IUnitOfWork unitOfWork, IConfigService config) : base(unitOfWork) => _config = config;

        private bool CanView => HasPermission(CompliancePermissions.AuditView);

        [HttpGet]
        public async Task<IActionResult> Index([FromQuery] AuditFilter filter)
        {
            if (!CanView) return NoPermission();
            filter.PageSize = Math.Clamp(filter.PageSize, 25, 500);
            filter.Page = Math.Max(filter.Page, 1);
            filter.Scope = string.Equals(filter.Scope,"ALL",StringComparison.OrdinalIgnoreCase)?"ALL":"DOCUMENT";
            if (filter.From == null && filter.To == null && filter.DocumentId == null) { filter.From = DateTime.Today.AddDays(-7); filter.To = DateTime.Today; }
            var rows = await _unitOfWork.Compliance.SearchAuditAsync(filter, filter.PageSize, (filter.Page - 1) * filter.PageSize);
            return View(new AuditIndexViewModel
            {
                Filter = filter,
                Rows = rows,
                Total = rows.FirstOrDefault()?.total_count ?? 0,
                FilterValues = await _unitOfWork.Compliance.GetAuditFilterValuesAsync(filter.Scope),
                Status = await _unitOfWork.Compliance.GetAuditStatusAsync(),
                CanExport = HasPermission(CompliancePermissions.AuditExport),
                CanReview = HasPermission(CompliancePermissions.AuditReview)
            });
        }

        [HttpGet]
        public async Task<IActionResult> Details(long id)
        {
            if (!CanView) return NoPermission();
            var entry = await _unitOfWork.Compliance.GetAuditAsync(id);
            return entry == null || !UserAuditPresentation.Visible(entry.action, entry.entity_type) ? NotFound() : View(entry);
        }

        /// <summary>Recomputes every row hash and the links between them.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Verify()
        {
            if (!CanView) return NoPermission();
            var started = DateTime.UtcNow;
            var r = await _unitOfWork.Compliance.VerifyChainAsync();
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            if (r?.is_valid == true) NotifySuccess($"Hash chain verified: {r.rows_checked:N0} entries, no alteration found ({seconds:0.0} s).");
            else NotifyError($"HASH CHAIN BROKEN at entry #{r?.first_broken_id}: {r?.problem}. Report this to QA immediately - the audit trail may have been altered outside the application.");
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Export([FromQuery] AuditFilter filter, string format = "csv")
        {
            if (!HasPermission(CompliancePermissions.AuditExport)) return NoPermission();
            var max = Math.Clamp(await _config.GetIntAsync("audit.export.maxRows", 100000), 1, 1_000_000);
            var rows = await _unitOfWork.Compliance.SearchAuditAsync(filter, max + 1, 0);
            if (rows.Count > max)
            {
                NotifyError($"The filter matches more than {max:N0} entries. Narrow it (dates, user, type) and export again.");
                return RedirectToAction(nameof(Index), filter);
            }
            ExportFile file;
            try
            {
                var report = AuditReportBuilder.Build(rows, filter,
                    User.Claims.FirstOrDefault(c => c.Type == "displayName")?.Value ?? User.Identity?.Name ?? "User", DateTime.UtcNow);
                file = AuditWorkbookExport.Create(report, format, DateTime.UtcNow);
            }
            catch (InvalidOperationException ex)
            {
                NotifyError(ex.Message);
                return RedirectToAction(nameof(Index), filter);
            }
            var logged = await _unitOfWork.Compliance.RecordAuditExportAsync(JsonSerializer.Serialize(filter.ToJson()), rows.Count,
                                                                             format.Equals("xlsx", StringComparison.OrdinalIgnoreCase) ? "XLSX" : "CSV", file.Sha256, CurrentUserId, ClientIp);
            if (!logged.Success) { NotifyError(logged.Message); return RedirectToAction(nameof(Index), filter); }
            Response.Headers["X-Content-SHA256"] = file.Sha256;
            return File(file.Bytes, file.ContentType, file.FileName);
        }

        [HttpGet]
        public async Task<IActionResult> Reviews()
        {
            if (!CanView) return NoPermission();
            ViewBag.canReview = HasPermission(CompliancePermissions.AuditReview);
            ViewBag.status = await _unitOfWork.Compliance.GetAuditStatusAsync();
            return View(await _unitOfWork.Compliance.GetAuditReviewsAsync(200));
        }

        [HttpGet]
        public async Task<IActionResult> Review([FromQuery] AuditFilter filter)
        {
            if (!HasPermission(CompliancePermissions.AuditReview)) return NoPermission();
            var status = await _unitOfWork.Compliance.GetAuditStatusAsync();
            // the next periodic review starts where the last one ended
            var from = filter.From ?? status?.last_reviewed_to?.Date ?? DateTime.Today.AddMonths(-1);
            var to = filter.To ?? DateTime.Today;
            ViewBag.status = status;
            ViewBag.rowCount = (await _unitOfWork.Compliance.SearchAuditAsync(new AuditFilter
                { From = from, To = to, Actor = filter.Actor, EntityType = filter.EntityType, AuditAction = filter.AuditAction, DocumentId = filter.DocumentId, Q = filter.Q }, 1, 0))
                .FirstOrDefault()?.total_count ?? 0;
            ViewBag.userName = (await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))?.username ?? "";
            return View(new AuditReviewForm { From = from, To = to, Actor = filter.Actor, EntityType = filter.EntityType, AuditAction = filter.AuditAction,
                                              DocumentId = filter.DocumentId, Q = filter.Q, ReviewType = filter.DocumentId != null ? "DOCUMENT" : "PERIODIC" });
        }

        [HttpPost, ValidateAntiForgeryToken, ActionName("Review")]
        public async Task<IActionResult> ReviewPost(AuditReviewForm form)
        {
            if (!HasPermission(CompliancePermissions.AuditReview)) return NoPermission();
            var (signed, error) = await VerifySignerAsync(form.SignatureUserName, form.SignaturePassword);
            if (error != null || !signed)
            {
                NotifyError(error ?? "Re-enter your user ID and password to record the review.");
                return RedirectToAction(nameof(Review), new { From = form.From?.ToString("yyyy-MM-dd"), To = form.To?.ToString("yyyy-MM-dd"), form.Actor, form.EntityType, form.AuditAction, form.DocumentId, form.Q });
            }
            var result = await _unitOfWork.Compliance.RecordAuditReviewAsync(form, signed, CurrentUserId, ClientIp);
            if (!result.Success)
            {
                NotifyError(result.Message);
                return RedirectToAction(nameof(Review), new { From = form.From?.ToString("yyyy-MM-dd"), To = form.To?.ToString("yyyy-MM-dd"), form.Actor, form.EntityType, form.AuditAction, form.DocumentId, form.Q });
            }
            var review = (await _unitOfWork.Compliance.GetAuditReviewsAsync(1)).FirstOrDefault();
            if (review?.chain_verified == false) NotifyError($"Review #{result.Id} recorded - but the hash chain is BROKEN at entry #{review.chain_break_at_id}. Raise a deviation.");
            else NotifySuccess($"Review #{result.Id} recorded: {review?.row_count:N0} entries reviewed, hash chain verified.");
            return RedirectToAction(nameof(Reviews));
        }

        private static string Describe(AuditFilter f)
        {
            var parts = new List<string>();
            if (f.From != null || f.To != null) parts.Add($"{f.From:dd-MMM-yyyy} to {f.To:dd-MMM-yyyy} (local dates)");
            if (!string.IsNullOrWhiteSpace(f.Actor)) parts.Add("user " + f.Actor);
            if (!string.IsNullOrWhiteSpace(f.EntityType)) parts.Add("type " + f.EntityType);
            if (!string.IsNullOrWhiteSpace(f.AuditAction)) parts.Add("action " + f.AuditAction);
            if (f.DocumentId != null) parts.Add("document #" + f.DocumentId);
            if (!string.IsNullOrWhiteSpace(f.Q)) parts.Add($"text \"{f.Q}\"");
            return parts.Count == 0 ? "everything" : string.Join(", ", parts);
        }
    }
}

