//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 3a (part 2) - writing the document in the application (URS-7.1) and editing it in the
 * Office editor (Collabora Online over WOPI). Ported from Bcl.Dms DocumentsController
 * (Compose, SaveContent, AutosaveContent, DiscardDraft, EditInOffice).
 */
using DMS.Web.CommonUtils;
using DMS.Web.Services.Authoring;
using DMS.Web.Services.Office;
using DMS.Web.Services.DocumentEditor;
using DMS.Model.Documents;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public partial class DocumentsController
    {
        private DocumentAuthoringService Authoring => HttpContext.RequestServices.GetRequiredService<DocumentAuthoringService>();
        private WopiService Wopi => HttpContext.RequestServices.GetRequiredService<WopiService>();

        /// <summary>The in-application editor for the revision in progress.</summary>
        [HttpGet]
        public async Task<IActionResult> Compose(long versionId, string? view = null)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            var documentId = await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId);
            if (documentId == null) return NotFound();

            // Existing authoring links must open the configured Office workspace too.
            // Keep an explicit HTML route for draft recovery and unsupported file formats.
            if (!string.Equals(view, "html", StringComparison.OrdinalIgnoreCase)
                && HasPermission(WopiService.EditInOfficePermission)
                && (await _unitOfWork.Authoring.GetContentAsync(versionId))?.HasUncommittedDraft != true
                && await DocumentEditor.IsAvailableAsync())
            {
                var version = (await _unitOfWork.Document.GetVersionsAsync(documentId.Value))
                    .FirstOrDefault(v => v.id == versionId);
                if (version == null) return NotFound();
                if (!string.IsNullOrWhiteSpace(version.source_storage_path)
                    && WopiService.IsEditableFile(version.source_file_name))
                    return RedirectToAction(nameof(Editor), new { versionId, mode = EditorMode.Edit });

                if (string.IsNullOrWhiteSpace(version.source_storage_path)
                    && HasPermission(WopiService.EditInOfficePermission))
                {
                    var (initial, initialError) = await Authoring.OpenAsync(versionId, CurrentUserId);
                    if (initial == null)
                    {
                        NotifyError(initialError ?? "The document cannot be initialized.");
                        return RedirectToAction(nameof(Details), new { id = documentId });
                    }
                    // An autosaved draft must be recovered and saved by its author first.
                    if (!initial.RestoredFromDraft)
                    {
                        var created = await Authoring.CommitAsync(new AuthoringSaveRequest
                        {
                            DocumentId = documentId.Value, DocumentVersionId = versionId,
                            Html = string.IsNullOrWhiteSpace(initial.Html) ? "<p>&nbsp;</p>" : initial.Html,
                            ExpectedRowVersion = initial.RowVersion, PageSetup = initial.PageSetup,
                            Reason = "Initialize the source document for the Office editor."
                        }, CurrentUserId, ClientIp);
                        if (!created.Ok)
                        {
                            NotifyError(created.Message ?? "The Office source could not be initialized.");
                            return RedirectToAction(nameof(Details), new { id = documentId });
                        }
                        return RedirectToAction(nameof(Editor), new { versionId, mode = EditorMode.Edit });
                    }
                }
            }

            var (session, error) = await Authoring.OpenAsync(versionId, CurrentUserId);
            if (session == null)
            {
                NotifyError(error ?? "The document cannot be opened in the editor.");
                return RedirectToAction(nameof(Details), new { id = documentId });
            }

            Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            Response.Headers.Pragma = "no-cache";
            ViewBag.pageSetupJson = Authoring.PageSetupJson(session.PageSetup);
            return View(session);
        }

        public sealed class ComposeSaveRequest
        {
            public long DocumentId { get; set; }
            public long DocumentVersionId { get; set; }
            public string? Html { get; set; }
            public long ExpectedRowVersion { get; set; }
            public string? Reason { get; set; }
            public DocumentPageSetupDto? PageSetup { get; set; }
            public long? TemplateId { get; set; }
            public string? AuthoringMode { get; set; }
        }

        [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(33_554_432)]
        public async Task<IActionResult> SaveContent([FromBody] ComposeSaveRequest request)
        {
            if (!HasPermission(DocumentPermissions.View)) return Json(new { ok = false, message = "You are not permitted to do this." });
            if (request == null) return Json(new { ok = false, message = "The request could not be read." });

            var saved = await Authoring.CommitAsync(new AuthoringSaveRequest
            {
                DocumentId = request.DocumentId,
                DocumentVersionId = request.DocumentVersionId,
                Html = request.Html,
                ExpectedRowVersion = request.ExpectedRowVersion,
                Reason = request.Reason,
                PageSetup = request.PageSetup
            }, CurrentUserId, ClientIp);

            if (!saved.Ok) return Json(new { ok = false, message = saved.Message });
            return Json(new
            {
                ok = true,
                fileName = saved.FileName,
                rowVersion = saved.RowVersion,
                notice = saved.Notice,
                pageSetup = saved.PageSetup,
                returnUrl = Url.Action(nameof(Details), new { id = request.DocumentId })
            });
        }

        [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(33_554_432)]
        public async Task<IActionResult> AutosaveContent([FromBody] ComposeSaveRequest request)
        {
            if (request == null || !HasPermission(DocumentPermissions.View)) return Json(new { ok = false });
            var result = await Authoring.AutosaveAsync(request.DocumentVersionId, request.Html, CurrentUserId);
            return Json(new { ok = result.Success, message = result.Success ? null : result.Message });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DiscardDraft(long versionId)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            var result = await Authoring.DiscardDraftAsync(versionId, CurrentUserId);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Compose), new { versionId });
        }

        private IDocumentEditorService DocumentEditor => HttpContext.RequestServices.GetRequiredService<IDocumentEditorService>();

        [HttpGet]
        public async Task<IActionResult> EditorComments(long versionId, EditorMode mode = EditorMode.Edit)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            if (!ModelState.IsValid || !Enum.IsDefined(mode)) return BadRequest();
            var documentId = await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId);
            if (documentId == null) return NotFound();
            var document = await _unitOfWork.Document.GetByIdAsync(documentId.Value);
            if (document == null) return NotFound();
            var access = await _unitOfWork.Document.GetAccessAsync(document.id, CurrentUserId);
            if (!access.can_view || !access.can_view_content) return Forbid();
            var versions = await _unitOfWork.Document.GetVersionsAsync(document.id);
            var version = versions.FirstOrDefault(v => v.id == versionId);
            if (version == null) return NotFound();
            var review = new DocumentDetailsViewModel
            {
                Document = document, Access = access, Versions = versions, CurrentVersion = version,
                Comments = (await _unitOfWork.Workflow.GetCommentsAsync(document.id))
                    .Where(c => c.document_version_id == versionId).ToList(),
                CurrentUserId = CurrentUserId,
                CanDisposition = access.can_edit && HasPermission(DocumentPermissions.Edit)
            };
            await HttpContext.RequestServices.GetRequiredService<EditorAnchorService>().EnrichAsync(document.id,review.Comments);
            foreach (var c in review.Comments) c.is_current_version = true;
            ViewData["EditorWorkspace"] = true;
            ViewData["EditorReturnVersionId"] = versionId;
            ViewData["EditorReturnMode"] = mode.ToString();
            Response.Headers.CacheControl = "no-store";
            return PartialView("_ReviewComments", review);
        }

        // Existing links remain valid but now go through the common provider resolver.
        [HttpGet]
        public Task<IActionResult> EditInOffice(long versionId) => Editor(versionId, EditorMode.Edit);

        [HttpGet]
        public async Task<IActionResult> Editor(long versionId, EditorMode mode = EditorMode.Edit, long? commentId = null)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            if (!ModelState.IsValid || !Enum.IsDefined(mode)) return BadRequest("Unknown editor mode.");
            var documentId = await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId);
            if (documentId == null) return NotFound();
            if(mode != EditorMode.Edit) return mode == EditorMode.Review
                ? await ReviewViewer(documentId.Value,versionId,commentId)
                : await Read(versionId,documentId.Value,"pdf");
            var editAccess = await _unitOfWork.Document.GetAccessAsync(documentId.Value,CurrentUserId);
            if(!editAccess.can_author || !HasPermission(WopiService.EditInOfficePermission))
                return await Read(versionId,documentId.Value,"pdf");
            var result = await DocumentEditor.OpenAsync(new EditorOpenContext(versionId, CurrentUserId,
                HasPermission(WopiService.EditInOfficePermission), ClientIp, mode, $"{Request.Scheme}://{Request.Host}"), HttpContext.RequestAborted);
            if (result.Session == null)
            {
                NotifyError(result.Error ?? "The document cannot be opened in the editor.");
                return RedirectToAction(nameof(Details), new { id = documentId });
            }
            var session = result.Session;
            var document = await _unitOfWork.Document.GetByIdAsync(session.DocumentId);
            if (document == null) return NotFound();
            var versions = await _unitOfWork.Document.GetVersionsAsync(session.DocumentId);
            var version = versions.FirstOrDefault(v => v.id == versionId);
            if (version == null) return NotFound();
            var access = await _unitOfWork.Document.GetAccessAsync(session.DocumentId, CurrentUserId);
            if (!access.can_view || !access.can_view_content) return Forbid();
            var review = new DocumentDetailsViewModel
            {
                Document = document, Access = access, Versions = versions, CurrentVersion = version,
                Comments = (await _unitOfWork.Workflow.GetCommentsAsync(session.DocumentId))
                    .Where(c => c.document_version_id == versionId).ToList(),
                CurrentUserId = CurrentUserId,
                CanDisposition = access.can_edit && HasPermission(DocumentPermissions.Edit)
            };
            // All displayed comments belong to the opened revision, including historical views.
            await HttpContext.RequestServices.GetRequiredService<EditorAnchorService>().EnrichAsync(document.id,review.Comments);
            foreach (var c in review.Comments) c.is_current_version = true;
            Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            Response.Headers.Pragma = "no-cache";
            ViewData["EditorWorkspace"] = true;
            ViewData["EditorReturnVersionId"] = versionId;
            ViewData["EditorReturnMode"] = mode.ToString();
            var locationComment = commentId.HasValue ? review.Comments.FirstOrDefault(c => c.id == commentId) : null;
            review.Attachments=(await _unitOfWork.Document.GetAttachmentsAsync(document.id,true)).Where(a=>a.document_version_id==null||a.document_version_id==versionId).ToList();
            var rendition=await _unitOfWork.Rendition.GetStatusAsync(versionId);
            return View("Editor", new EditorWorkspaceViewModel
            {
                Session = session, Review = review,
                Watermark=access.apply_screen_watermark?ScreenCopyLabel.Resolve(rendition?.expected_watermark,
                    document.state_is_effective,document.is_obsolete||(document.current_version_id!=versionId&&document.effective_version_id!=versionId)):null,
                CanEditDocument = access.can_author && document.state_is_editable
                    && document.current_version_id == versionId && HasPermission(WopiService.EditInOfficePermission),
                InitialLocation = locationComment == null ? null : new EditorLocationAnchor(
                    locationComment.page_no, locationComment.section_reference, locationComment.anchor_type,
                    locationComment.anchor_value, locationComment.anchor_text)
            });
        }
    }
}
