//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5a - controlled print (URS-7.14, 7.15). Ported from Bcl.Dms PrintController / PrintControlService.
 *
 *   request (Request controlled print) -> QA decision, e-signed "Print authorized by" (Authorize controlled print)
 *   -> numbered copies allocated -> each copy printed once (the requester or Issue / return controlled copies)
 *   -> returned / destroyed / lost (Issue / return controlled copies).
 *
 * The PDF of a numbered copy is produced on the fly from the approved base PDF of the revision (never the authored
 * file) and streamed inline for the browser's print dialog; it is not stored - its SHA-256 goes into the print log.
 * Every rule (who, which state, which revision, how many) is decided by the sp_print_* functions.
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.Rendition;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class PrintController : DmsBaseController
    {
        private readonly ControlledCopyRenderer _renderer;
        private readonly ILogger<PrintController> _logger;

        public PrintController(IUnitOfWork unitOfWork, ControlledCopyRenderer renderer, ILogger<PrintController> logger) : base(unitOfWork)
        {
            _renderer = renderer; _logger = logger;
        }

        private bool AnyPrintPermission => PrintPermissions.All.Any(HasPermission);
        private string DisplayName => User.Claims.FirstOrDefault(c => c.Type == "displayName")?.Value ?? User.Identity?.Name ?? "";

        // ------------------------------------------------------------------ lists

        [HttpGet]
        public async Task<IActionResult> Index(string? status, bool mine = false)
        {
            if (!AnyPrintPermission) return NoPermission();
            // without QA / issuer rights a user sees only his own requests
            var onlyMine = mine || !(HasPermission(PrintPermissions.Authorize) || HasPermission(PrintPermissions.Issue) || HasPermission(PrintPermissions.Log));
            ViewBag.status = status ?? "OPEN";
            ViewBag.mine = onlyMine;
            ViewBag.canSeeAll = !onlyMine || HasPermission(PrintPermissions.Authorize) || HasPermission(PrintPermissions.Issue);
            return View(await _unitOfWork.Print.GetRequestsAsync(status ?? "OPEN", onlyMine ? CurrentUserId : null, null, 500));
        }

        [HttpGet]
        public async Task<IActionResult> Log(long? documentId, DateTime? from, DateTime? to)
        {
            if (!HasPermission(PrintPermissions.Log)) return NoPermission();
            ViewBag.documentId = documentId; ViewBag.from = from; ViewBag.to = to;
            return View(await _unitOfWork.Print.GetLogAsync(documentId, null, from, to, 2000));
        }

        /// <summary>Issued controlled copies whose revision is no longer in force, or overdue for return.</summary>
        [HttpGet]
        public async Task<IActionResult> Retrieve()
        {
            if (!HasPermission(PrintPermissions.Issue) && !HasPermission(PrintPermissions.Log)) return NoPermission();
            return View(await _unitOfWork.Print.GetCopiesToRetrieveAsync());
        }

        // ------------------------------------------------------------------ request

        [HttpGet]
        public async Task<IActionResult> New(long documentId)
        {
            if (!HasPermission(PrintPermissions.Request)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(documentId);
            if (document == null) return NotFound();
            if (!document.state_is_effective || document.effective_version_id == null || document.is_obsolete)
            {
                NotifyError("Only a document in force can be printed.");
                return RedirectToAction("Details", "Documents", new { id = documentId });
            }
            ViewBag.document = document;
            ViewBag.users = await _unitOfWork.Workflow.GetUserPickListAsync();
            ViewBag.open = await _unitOfWork.Print.GetRequestsAsync("OPEN", null, documentId, 50);
            var config = HttpContext.RequestServices.GetRequiredService<IConfigService>();
            ViewBag.maxCopies = 1;
            var days = await config.GetIntAsync(ConfigKeys.PrintDefaultReturnDays, 30);
            return View(new PrintRequestForm { DocumentId = documentId, ReturnDueOn = days > 0 ? DateTime.Today.AddDays(days) : null });
        }

        [HttpPost, ValidateAntiForgeryToken, ActionName("New")]
        public async Task<IActionResult> NewPost(PrintRequestForm form)
        {
            if (!HasPermission(PrintPermissions.Request)) return NoPermission();
            if(form.Copies!=1){NotifyError("Request exactly one copy at a time.");return RedirectToAction(nameof(New),new{documentId=form.DocumentId});}
            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                return RedirectToAction(nameof(New), new { documentId = form.DocumentId });
            }
            var result = await _unitOfWork.Print.CreateRequestAsync(form, CurrentUserId, ClientIp);
            if (!result.Success)
            {
                NotifyError(result.Message);
                return RedirectToAction(nameof(New), new { documentId = form.DocumentId });
            }
            NotifySuccess(result.Message);
            return RedirectToAction(nameof(Details), new { id = result.Id });
        }

        // ------------------------------------------------------------------ one request

        [HttpGet]
        public async Task<IActionResult> Details(long id)
        {
            if (!AnyPrintPermission) return NoPermission();
            var request = await _unitOfWork.Print.GetRequestAsync(id);
            if (request == null) return NotFound();
            var isRequester = request.requested_by == CurrentUserId;
            if (!isRequester && !HasPermission(PrintPermissions.Authorize) && !HasPermission(PrintPermissions.Issue) && !HasPermission(PrintPermissions.Log))
                return NoPermission();
            var model = new PrintDetailsViewModel
            {
                Request = request,
                Copies = await _unitOfWork.Print.GetCopiesAsync(id),
                Log = await _unitOfWork.Print.GetLogAsync(null, id, null, null, 500),
                CanAuthorize = HasPermission(PrintPermissions.Authorize),
                CanIssue = HasPermission(PrintPermissions.Issue),
                IsRequester = isRequester,
                CurrentUserName = (await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))?.username ?? ""
            };
            if (model.CanIssue || isRequester) model.Printers = await _unitOfWork.Printer.GetListAsync(true);
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Decide(PrintDecisionForm form)
        {
            if (!HasPermission(PrintPermissions.Authorize)) return NoPermission();
            if(form.Approve&&form.CopiesAuthorized!=1){NotifyError("Authorise exactly one copy.");return RedirectToAction(nameof(Details),new{id=form.RequestId});}
            var signed = false;
            if (form.Approve)
            {
                (signed, var signError) = await VerifySignerAsync(form.SignatureUserName, form.SignaturePassword);
                if (signError != null)
                {
                    NotifyError(signError);
                    return RedirectToAction(nameof(Details), new { id = form.RequestId });
                }
            }
            var (row, result) = await _unitOfWork.Print.DecideAsync(form, signed, CurrentUserId, ClientIp);
            Notify(result, row?.status == "AUTHORIZED" ? $"Authorised: {row.allocated} numbered cop{(row.allocated == 1 ? "y" : "ies")} allocated." : result.Message);
            return RedirectToAction(nameof(Details), new { id = form.RequestId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel(long id, string? reason)
        {
            if (!AnyPrintPermission) return NoPermission();
            Notify(await _unitOfWork.Print.CancelAsync(id, reason, CurrentUserId, ClientIp), "The print request has been cancelled.");
            return RedirectToAction(nameof(Details), new { id });
        }

        /// <summary>
        /// Prints one numbered copy: the PDF is produced, the issue is recorded with its SHA-256, and the PDF is sent inline
        /// (opened in a new tab for the browser's print dialog). If the database refuses, nothing is sent.
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> PrintCopy(long copyId, string? printer, string? issuedToName, string? reason)
        {
            if (!AnyPrintPermission) return NoPermission();
            var copy = await _unitOfWork.Print.GetCopyContextAsync(copyId);
            if (copy == null) return NotFound();
            if (copy.requested_by != CurrentUserId && !HasPermission(PrintPermissions.Issue)) return NoPermission();
            if(copy.copy_status!="ALLOCATED"){NotifyError("This numbered copy has already been issued; request a new copy.");return RedirectToAction(nameof(Details),new{id=copy.print_request_id});}
            printer="Browser print dialog"; // The browser does not report the user's selected printer.
            if (!copy.is_version_in_force)
            {
                NotifyError($"Revision {copy.version_label} is no longer in force; it cannot be printed.");
                return RedirectToAction(nameof(Details), new { id = copy.print_request_id });
            }

            if (!string.IsNullOrWhiteSpace(issuedToName)) copy.issued_to_name = issuedToName.Trim();
            ControlledCopyPdf pdf;
            try
            {
                pdf = await _renderer.RenderAsync(copy, DisplayName, HttpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is RenditionException or FileStoreException or DMS.Web.Services.Pdf.PdfException)
            {
                _logger.LogWarning(ex, "Controlled copy {Copy} could not be produced", copy.copy_control_number);
                NotifyError("The controlled copy could not be produced: " + ex.Message);
                return RedirectToAction(nameof(Details), new { id = copy.print_request_id });
            }

            var (evt, result) = await _unitOfWork.Print.IssueCopyAsync(copyId, printer, issuedToName, pdf.Sha256, pdf.Watermark, reason,
                                                                      CurrentUserId, ClientIp, Request.Headers.UserAgent.ToString());
            if (!result.Success)
            {
                NotifyError(result.Message);
                return RedirectToAction(nameof(Details), new { id = copy.print_request_id });
            }
            _logger.LogInformation("Controlled copy {Copy} {Event} by user {User} (sha256 {Sha})", copy.copy_control_number, evt, CurrentUserId, pdf.Sha256);
            Response.Headers["Content-Disposition"] = $"inline; filename=\"{pdf.FileName}\"";
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers.Remove("Content-Disposition");
            return View("PrintDialog",pdf);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseCopy(long copyId, long requestId, string? outcome, string? reference, string? remarks)
        {
            if (!HasPermission(PrintPermissions.Issue)) return NoPermission();
            Notify(await _unitOfWork.Print.CloseCopyAsync(copyId, outcome, reference, remarks, CurrentUserId, ClientIp),
                   $"The copy has been recorded as {(outcome ?? "").ToLowerInvariant()}.");
            return RedirectToAction(nameof(Details), new { id = requestId });
        }

        /// <summary>
        /// An uncontrolled copy of the revision in force, for a reader the access matrix allows to print (Read page).
        /// Watermarked "UNCONTROLLED COPY - FOR INFORMATION ONLY", logged in the print log with its SHA-256.
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Uncontrolled(long documentId)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            // A browser export cannot bypass the numbered-copy authorization flow.
            return RedirectToAction(nameof(New), new { documentId });
        }
    }
}
