//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 3b - workflow actions with electronic signatures (URS-7.3 - 7.10, 21 CFR 11), review comments and
 * QA reassignment. Ported from Bcl.Dms DocumentsController (Action, AddComment, DisposeComment, Reassign,
 * ChangeOwner) and ESignatureService.VerifyCredentialsAsync.
 *
 * Every rule of the workflow (legal moves, gates, stage routing, signature meaning) is decided by
 * sp_workflow_execute in ONE transaction. The only thing done here is re-checking the signer's password,
 * because the PTPL AES password is verified by the application, as at login.
 */
using DMS.Model.Documents;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public partial class DocumentsController
    {
        public const string ReassignPermission = "Reassign participants";   // document.reassign

        /// <summary>The single entry point for every workflow action; the database resolves the transition.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Action(WorkflowActionForm form)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            if (!ModelState.IsValid)
            {
                NotifyError("The action could not be submitted - " + FirstModelError());
                return RedirectToAction(nameof(Details), new { id = form.DocumentId });
            }
            var permittedActions=await _unitOfWork.Workflow.GetAvailableActionsAsync(form.DocumentId,CurrentUserId);
            if(!permittedActions.Any(a=>string.Equals(a.action_code,form.ActionCode,StringComparison.OrdinalIgnoreCase)))
                return Forbid();

            // No password entered = no signature offered. The database then refuses any transition that
            // needs one (SIGNATURE_REQUIRED), so an unsigned post can never pass a signed step.
            var (signed, signError) = await VerifySignatureAsync(form);
            form.SignaturePassword = null; // Never retain credentials while waiting for conversion.
            if (signError != null)
            {
                NotifyError(signError);
                return RedirectToAction(nameof(Details), new { id = form.DocumentId });
            }

            // URS-7.18: a state that needs the PDF rendition waits for it (the job is queued by the check itself)
            var wait = await _unitOfWork.Rendition.GateAsync(form.DocumentId, form.ActionCode, CurrentUserId);
            if (!string.IsNullOrWhiteSpace(wait))
            {
                if(wait.Contains("no file yet",StringComparison.OrdinalIgnoreCase))
                {
                    NotifyError("This revision has no file to convert. Upload or write the document before submitting it for review.");
                    return RedirectToAction(nameof(Details),new { id=form.DocumentId });
                }
                var processor=HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.Rendition.RenditionProcessor>();
                var converter=await processor.ConverterStatusAsync(HttpContext.RequestAborted);
                if(!converter.IsAvailable)
                {
                    NotifyError("The PDF conversion service is unavailable. Start Docker and the configured PDF converter. The queued document will resume automatically; then submit for review again. Your document remains saved.");
                    return RedirectToAction(nameof(Details),new { id=form.DocumentId });
                }
                await _unitOfWork.Rendition.RecoverUnavailableAsync();
                DMS.Web.Services.Rendition.RenditionWorker.Nudge();
                // The worker owns conversion. Wait briefly for its committed result
                // instead of requiring a second approval and another signature.
                var deadline = DateTime.UtcNow.AddSeconds(30);
                do {
                    await Task.Delay(500, HttpContext.RequestAborted);
                    var current=await _unitOfWork.Document.GetByIdAsync(form.DocumentId);
                    var status=current?.current_version_id is long version ? await _unitOfWork.Rendition.GetStatusAsync(version) : null;
                    if(status?.base_ready==true)
                        wait=await _unitOfWork.Rendition.GateAsync(form.DocumentId,form.ActionCode,CurrentUserId);
                    else if(status?.job_status=="FAILED")
                    {
                        wait="The PDF could not be generated. Check Administration > PDF Rendition for the failed conversion and retry it after correcting the cause.";
                        break;
                    }
                } while (!string.IsNullOrWhiteSpace(wait) && DateTime.UtcNow < deadline);
                if (!string.IsNullOrWhiteSpace(wait)) {
                    NotifyError(wait.Contains("still being prepared",StringComparison.OrdinalIgnoreCase)
                        ? "The PDF is queued or being generated. Your document is saved. Wait for the PDF to be ready, then submit for review again. If generation fails, check Administration > PDF Rendition."
                        : wait);
                    return RedirectToAction(nameof(Details), new { id = form.DocumentId });
                }
            }

            var (outcome, result) = await _unitOfWork.Workflow.ExecuteAsync(form, CurrentUserId, signed, ClientIp);
            if (!result.Success || outcome == null)
            {
                NotifyError(string.IsNullOrWhiteSpace(result.Message) ? "The action was not carried out." : result.Message);
                return RedirectToAction(nameof(Details), new { id = form.DocumentId });
            }

            _logger.LogInformation("Workflow {Action} on document {DocumentId} by user {UserId}: {From} -> {To}",
                form.ActionCode, form.DocumentId, CurrentUserId, outcome.from_state_name, outcome.to_state_name);
            DMS.Web.Services.Rendition.RenditionWorker.Nudge();   // the controlled copy for the new state
            NotifySuccess(outcome.Describe());
            return RedirectToAction(nameof(Details), new { id = form.DocumentId });
        }

        /// <summary>
        /// ESignatureService.VerifyCredentialsAsync: the signer re-enters his OWN user ID and password.
        /// Being signed in is not the same as signing. A wrong password counts towards the login lockout.
        /// </summary>
        private Task<(bool Signed, string? Error)> VerifySignatureAsync(WorkflowActionForm form)
            => VerifySignerAsync(form.SignatureUserName, form.SignaturePassword);

        // ------------------------------------------------------------------ review comments

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AddComment(long documentId, long versionId, string? commentType, string? sectionReference,
                                                    int? pageNo, string? commentText, long? parentCommentId, long? editorVersionId = null, string? editorMode = null, string? anchorType = null, string? anchorValue = null, string? anchorText = null)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            sectionReference=sectionReference?.Trim();
            if(sectionReference?.Length>128 || sectionReference?.Any(c=>char.IsControl(c)||c is '<' or '>')==true
                || (!string.IsNullOrEmpty(sectionReference)&&!char.IsLetterOrDigit(sectionReference[0])))
            {
                const string message="Enter a valid section reference using up to 128 printable characters.";
                if(editorVersionId==versionId&&Request.Headers["X-Requested-With"]=="XMLHttpRequest")return Json(new{ok=false,message});
                NotifyError(message);
                if(editorVersionId==versionId&&Enum.TryParse<DMS.Web.Services.DocumentEditor.EditorMode>(editorMode,true,out var invalidMode)&&Enum.IsDefined(invalidMode))
                    return RedirectToAction(nameof(Editor),new{versionId,mode=invalidMode});
                return Redirect(Url.Action(nameof(Details),new{id=documentId})+"#dms-comments");
            }
            var result = !string.IsNullOrWhiteSpace(anchorType)
                ? await HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.DocumentEditor.EditorAnchorService>().AddAsync(documentId,versionId,parentCommentId,commentType,sectionReference,pageNo,commentText,CurrentUserId,ClientIp,anchorType,anchorValue,anchorText)
                : await _unitOfWork.Workflow.AddCommentAsync(documentId, versionId, parentCommentId, commentType,
                sectionReference, pageNo, commentText, CurrentUserId, ClientIp);
            Notify(result, string.Equals(commentType, "NO_FINDING", StringComparison.OrdinalIgnoreCase)
                ? "Recorded: \"Reviewed. No comments on this revision.\""
                : "The comment has been recorded.");
            if (editorVersionId == versionId && Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Json(new { ok = result.Success, message = result.Message });
            if (editorVersionId == versionId && Enum.TryParse<DMS.Web.Services.DocumentEditor.EditorMode>(editorMode, true, out var mode) && Enum.IsDefined(mode))
                return RedirectToAction(nameof(Editor), new { versionId, mode });
            return Redirect(Url.Action(nameof(Details), new { id = documentId }) + "#dms-comments");
        }

        /// <summary>The author answers a finding (accepted / rejected / deferred) while the document is back with him.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DisposeComment(long commentId, long documentId, string? disposition, string? remarks, long? editorVersionId = null, string? editorMode = null)
        {
            if (!HasPermission(DocumentPermissions.Edit)) return NoPermission();
            var result = await _unitOfWork.Workflow.DisposeCommentAsync(commentId, disposition, remarks, CurrentUserId, ClientIp);
            Notify(result, "The answer to the finding has been recorded.");
            if (editorVersionId != null && Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Json(new { ok = result.Success, message = result.Message });
            if (editorVersionId != null && await _unitOfWork.Document.GetDocumentIdOfVersionAsync(editorVersionId.Value) == documentId
                && Enum.TryParse<DMS.Web.Services.DocumentEditor.EditorMode>(editorMode, true, out var mode) && Enum.IsDefined(mode))
                return RedirectToAction(nameof(Editor), new { versionId = editorVersionId.Value, mode });
            return Redirect(Url.Action(nameof(Details), new { id = documentId }) + "#dms-comment-" + commentId);
        }

        // ------------------------------------------------------------------ QA authority (URS-7.10)

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reassign(long documentId, long workflowInstanceId, int newAssigneeUserId, string? reason,
                                                  long? expectedRowVersion)
        {
            if (!HasPermission(ReassignPermission)) return NoPermission();
            var result = await _unitOfWork.Workflow.ReassignAsync(workflowInstanceId, newAssigneeUserId, reason, expectedRowVersion,
                CurrentUserId, ClientIp);
            Notify(result, "The stage has been reassigned and the new assignee notified.");
            return RedirectToAction(nameof(Details), new { id = documentId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeOwner(long documentId, int newOwnerUserId, string? reason)
        {
            if (!HasPermission(ReassignPermission)) return NoPermission();
            var result = await _unitOfWork.Workflow.ChangeOwnerAsync(documentId, newOwnerUserId, reason, CurrentUserId, ClientIp);
            Notify(result, "The document owner has been changed.");
            return RedirectToAction(nameof(Details), new { id = documentId });
        }

        /// <summary>Fills the Phase 3b parts of the details page.</summary>
        private async Task LoadWorkflowAsync(DocumentDetailsViewModel model)
        {
            var d = model.Document;
            model.CurrentUserId = CurrentUserId;
            model.CurrentUserName = (await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))?.username ?? "";
            model.Actions = await _unitOfWork.Workflow.GetAvailableActionsAsync(d.id, CurrentUserId);
            model.Comments = await _unitOfWork.Workflow.GetCommentsAsync(d.id);
            model.Signatures = await _unitOfWork.Workflow.GetSignaturesAsync(d.id);
            model.CanReassign = HasPermission(ReassignPermission);
            model.CanManageLinks = HasPermission(DocumentPermissions.Links);
            model.CanDisposition = model.Access.can_edit && HasPermission(DocumentPermissions.Edit);
            if (model.Actions.Count > 0 || model.CanReassign)
                model.AssigneePool = await _unitOfWork.Workflow.GetAssigneePoolAsync(d.id);
            if (model.CanReassign)
                model.UserPickList = await _unitOfWork.Workflow.GetUserPickListAsync();
            if (model.CurrentVersion != null)
                model.Rendition = await _unitOfWork.Rendition.GetStatusAsync(model.CurrentVersion.id);
            if (d.effective_version_id != null && d.effective_version_id != model.CurrentVersion?.id)
                model.EffectiveRendition = await _unitOfWork.Rendition.GetStatusAsync(d.effective_version_id.Value);
            model.CanManageRendition = HasPermission(RenditionPermission);
            model.CanShare = HasPermission(SharePermission);
            model.CanRequestPrint = HasPermission(PrintPermissions.Request);
            if (model.CanShare) model.ShareLinks = await _unitOfWork.Rendition.GetShareLinksAsync(d.id);
            if (model.Actions.Count > 0)
            {
                model.ReviewRejectionReasons = await _unitOfWork.Workflow.GetRejectionReasonsAsync("REVIEW");
                model.ApprovalRejectionReasons = await _unitOfWork.Workflow.GetRejectionReasonsAsync("APPROVAL");
                model.ChangeReasons = await _unitOfWork.Workflow.GetChangeReasonsAsync();
            }
        }
    }
}
