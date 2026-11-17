//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 3c - revisions and lifecycle (URS-7.8, 7.11, 7.12, 7.19). Ported from Bcl.Dms DocumentsController
 * (Revise, Obsolete, History, Compare, AddLink, Impact, AssessImpact) and DocumentService.CompareVersionsAsync.
 */
using DMS.Model.Documents;
using DMS.Web.CommonUtils;
using DMS.Web.Services.Authoring;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public partial class DocumentsController
    {
        public const string RevisePermission = "Initiate revision";      // document.revise
        public const string ObsoletePermission = "Obsolete document";    // document.obsolete

        // ------------------------------------------------------------------ start a revision

        [HttpGet]
        public async Task<IActionResult> Revise(long id)
        {
            if (!HasPermission(RevisePermission)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(id);
            if (document == null) return NotFound();
            await LoadReviseChoicesAsync();
            ViewBag.document = document;
            return View(new ReviseForm { DocumentId = id, ExpectedRowVersion = document.row_version });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Revise(ReviseForm form)
        {
            if (!HasPermission(RevisePermission)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(form.DocumentId);
            if (document == null) return NotFound();
            if (ModelState.IsValid)
            {
                var result = await _unitOfWork.Lifecycle.ReviseAsync(form, CurrentUserId, ClientIp);
                if (result.Success)
                {
                    DMS.Web.Services.Rendition.RenditionWorker.Nudge();
                    NotifySuccess(result.Message);
                    return RedirectToAction(nameof(Details), new { id = form.DocumentId });
                }
                ModelState.AddModelError(string.Empty, result.Message);
            }
            await LoadReviseChoicesAsync();
            ViewBag.document = document;
            return View(form);
        }

        private async Task LoadReviseChoicesAsync()
        {
            ViewBag.changeControls = (await _unitOfWork.ChangeControl.SearchAsync(null, null))
                .Where(c => c.status is "VERIFIED" or "CLOSED").ToList();
            ViewBag.changeReasons = await _unitOfWork.Workflow.GetChangeReasonsAsync();
        }

        // ------------------------------------------------------------------ obsolete (signed)

        [HttpGet]
        public async Task<IActionResult> Obsolete(long id)
        {
            if (!HasPermission(ObsoletePermission)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(id);
            if (document == null) return NotFound();
            ViewBag.document = document;
            ViewBag.userName = (await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))?.username;
            return View(new ObsoleteForm { DocumentId = id, ExpectedRowVersion = document.row_version, ObsoleteOn = DateTime.Today });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Obsolete(ObsoleteForm form)
        {
            if (!HasPermission(ObsoletePermission)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(form.DocumentId);
            if (document == null) return NotFound();
            if (ModelState.IsValid)
            {
                var (signed, signError) = await VerifySignatureAsync(new WorkflowActionForm
                {
                    SignatureUserName = form.SignatureUserName,
                    SignaturePassword = form.SignaturePassword
                });
                if (signError != null)
                    ModelState.AddModelError(string.Empty, signError);
                else
                {
                    form.SignatureMeaning = "Obsoleted by";
                    var result = await _unitOfWork.Lifecycle.ObsoleteAsync(form, signed, CurrentUserId, ClientIp);
                    if (result.Success)
                    {
                        DMS.Web.Services.Rendition.RenditionWorker.Nudge();
                        NotifySuccess(result.Message);
                        return RedirectToAction(nameof(Details), new { id = form.DocumentId });
                    }
                    ModelState.AddModelError(string.Empty, result.Message);
                }
            }
            ViewBag.document = document;
            ViewBag.userName = (await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))?.username;
            form.SignaturePassword = null;
            return View(form);
        }

        // ------------------------------------------------------------------ revision history + compare

        [HttpGet]
        public async Task<IActionResult> History(long id)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(id);
            if (document == null) return NotFound();
            var access = await _unitOfWork.Document.GetAccessAsync(id, CurrentUserId);
            if (!access.can_view) return NoPermission();
            return View(new DocumentHistoryViewModel
            {
                Document = document,
                Access = access,
                Versions = await _unitOfWork.Document.GetVersionsAsync(id),
                History = await _unitOfWork.Document.GetStateHistoryAsync(id),
                Signatures = await _unitOfWork.Workflow.GetSignaturesAsync(id),
                TempRevisions = await _unitOfWork.Lifecycle.GetTempRevisionsAsync(id)
            });
        }

        /// <summary>
        /// DocumentService.CompareVersionsAsync: metadata side by side, and the text line by line - the body written in
        /// the application, or (new in DMS_V2) the text read out of an uploaded .docx when there is no such body.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Compare(long id, long? left, long? right)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(id);
            if (document == null) return NotFound();
            var access = await _unitOfWork.Document.GetAccessAsync(id, CurrentUserId);
            if (!access.can_view_content) return NoPermission();

            var versions = await _unitOfWork.Document.GetVersionsAsync(id);
            if (versions.Count < 2)
            {
                NotifyError("There is only one revision - nothing to compare yet.");
                return RedirectToAction(nameof(History), new { id });
            }
            var r = versions.FirstOrDefault(v => v.id == right) ?? versions[0];
            var l = versions.FirstOrDefault(v => v.id == left)
                    ?? versions.Where(v => v.version_seq < r.version_seq).OrderByDescending(v => v.version_seq).FirstOrDefault()
                    ?? versions[1];

            var signatures = await _unitOfWork.Workflow.GetSignaturesAsync(id);
            var model = new CompareViewModel
            {
                Document = document,
                Versions = versions,
                Left = l,
                Right = r,
                LeftSignatures = signatures.Where(s => s.document_version_id == l.id).ToList(),
                RightSignatures = signatures.Where(s => s.document_version_id == r.id).ToList(),
                Metadata = new()
                {
                    ("Revision", l.display_revision, r.display_revision),
                    ("State", l.state_name, r.state_name),
                    ("Supersedes", l.supersedes_version_no, r.supersedes_version_no),
                    ("Change control", l.change_control_number, r.change_control_number),
                    ("Description of change", l.change_summary, r.change_summary),
                    ("Source file", l.source_file_name, r.source_file_name),
                    ("Source SHA-256", l.source_sha256, r.source_sha256),
                    ("Effective from", l.effective_from?.ToString("dd-MMM-yyyy"), r.effective_from?.ToString("dd-MMM-yyyy")),
                    ("Obsolete / superseded on", l.obsolete_on?.ToString("dd-MMM-yyyy"), r.obsolete_on?.ToString("dd-MMM-yyyy")),
                    ("Authorized", l.authorized_at == null ? null : $"{l.authorized_at:dd-MMM-yyyy HH:mm} {l.authorized_by_name}",
                                   r.authorized_at == null ? null : $"{r.authorized_at:dd-MMM-yyyy HH:mm} {r.authorized_by_name}"),
                    ("Published", l.published_at == null ? null : $"{l.published_at:dd-MMM-yyyy HH:mm} {l.published_by_name}",
                                  r.published_at == null ? null : $"{r.published_at:dd-MMM-yyyy HH:mm} {r.published_by_name}"),
                    ("Created", $"{l.created_at:dd-MMM-yyyy} {l.created_by_name}", $"{r.created_at:dd-MMM-yyyy} {r.created_by_name}")
                }
            };

            var (leftHtml, leftSource) = await CompareTextAsync(l);
            var (rightHtml, rightSource) = await CompareTextAsync(r);
            model.LeftTextSource = leftSource;
            model.RightTextSource = rightSource;
            try
            {
                var leftLines = TextDiffRules.ToLines(leftHtml);
                var rightLines = TextDiffRules.ToLines(rightHtml);
                if (leftLines.Count == 0 && rightLines.Count == 0)
                    model.ContentUnavailableReason = "Neither revision has text that can be read here (only a .docx written in the application or uploaded can be compared line by line; for other files compare the SHA-256 above).";
                else
                {
                    var rows = TextDiffRules.Compare(leftLines, rightLines);
                    (model.Added, model.Removed, model.Unchanged) = TextDiffRules.Summarise(rows);
                    // inside each block of changes, the n-th removed line is paired with the n-th added line - that is
                    // a changed line, and only the words that differ are marked
                    var partner = new Dictionary<int, string?>();
                    for (var i = 0; i < rows.Count;)
                    {
                        if (rows[i].Kind == DiffKind.Unchanged) { i++; continue; }
                        var start = i;
                        while (i < rows.Count && rows[i].Kind != DiffKind.Unchanged) i++;
                        var removed = Enumerable.Range(start, i - start).Where(k => rows[k].Kind == DiffKind.Removed).ToList();
                        var added = Enumerable.Range(start, i - start).Where(k => rows[k].Kind == DiffKind.Added).ToList();
                        for (var n = 0; n < Math.Min(removed.Count, added.Count); n++)
                        {
                            partner[removed[n]] = rows[added[n]].Right;
                            partner[added[n]] = rows[removed[n]].Left;
                        }
                    }
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        partner.TryGetValue(i, out var other);
                        model.Lines.Add(new CompareLine
                        {
                            Kind = row.Kind.ToString(),
                            LeftLineNo = row.LeftLineNo,
                            RightLineNo = row.RightLineNo,
                            LeftHtml = row.Kind == DiffKind.Removed ? TextDiffRules.HighlightWords(row.Left, other) : System.Net.WebUtility.HtmlEncode(row.Left ?? ""),
                            RightHtml = row.Kind == DiffKind.Added ? TextDiffRules.HighlightWords(row.Right, other) : System.Net.WebUtility.HtmlEncode(row.Right ?? "")
                        });
                    }
                }
            }
            catch (ArgumentOutOfRangeException ex)
            {
                model.ContentUnavailableReason = ex.Message;
            }
            await _unitOfWork.Document.RecordReadAsync(id, r.id, $"COMPARE {l.display_revision} / {r.display_revision}", CurrentUserId, ClientIp);
            return View(model);
        }

        private async Task<(string? Html, string? Source)> CompareTextAsync(DocumentVersion version)
        {
            var content = await _unitOfWork.Authoring.GetContentAsync(version.id);
            if (!string.IsNullOrWhiteSpace(content?.content_html)) return (content.content_html, "written in the application");
            var extractor = HttpContext.RequestServices.GetRequiredService<IDocumentTextExtractor>();
            if (version.source_storage_path != null && extractor.CanExtract(version.source_file_name, version.source_mime_type))
            {
                try
                {
                    var html = await extractor.ExtractHtmlAsync(version.source_storage_path);
                    if (!string.IsNullOrWhiteSpace(html)) return (html, "read from " + version.source_file_name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Text of revision {VersionId} could not be read for the comparison", version.id);
                }
            }
            return (null, version.source_file_name == null ? "no content" : "not readable (" + version.source_file_name + ")");
        }

        // ------------------------------------------------------------------ links and impact (URS-7.19)

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AddLink(long documentId, long? targetDocumentId, string? linkType, string? sectionReference)
        {
            if (!HasPermission(DocumentPermissions.Links)) return NoPermission();
            if (targetDocumentId == null) NotifyError("Pick the document that is referenced from the list.");
            else Notify(await _unitOfWork.Lifecycle.AddLinkAsync(documentId, targetDocumentId.Value, linkType, sectionReference,
                                                                  CurrentUserId, ClientIp), "The document reference has been recorded.");
            return Redirect(Url.Action(nameof(Details), new { id = documentId }) + "#dms-links");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveLink(long documentId, long linkId, string? reason)
        {
            if (!HasPermission(DocumentPermissions.Links)) return NoPermission();
            Notify(await _unitOfWork.Lifecycle.RemoveLinkAsync(linkId, reason, CurrentUserId, ClientIp), "The reference has been removed.");
            return Redirect(Url.Action(nameof(Details), new { id = documentId }) + "#dms-links");
        }

        [HttpGet]
        public async Task<IActionResult> Impact(long? departmentId)
        {
            if (!HasPermission(DocumentPermissions.Links)) return NoPermission();
            ViewBag.departments = await _unitOfWork.Department.GetListAsync();
            ViewBag.departmentId = departmentId;
            return View(await _unitOfWork.Lifecycle.GetImpactPendingAsync(departmentId));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AssessImpact(long linkId, string? impactFlag, string? remarks, long? documentId)
        {
            if (!HasPermission(DocumentPermissions.Links)) return NoPermission();
            Notify(await _unitOfWork.Lifecycle.AssessLinkAsync(linkId, impactFlag, remarks, CurrentUserId, ClientIp),
                   "The impact assessment has been recorded.");
            return documentId.HasValue
                ? Redirect(Url.Action(nameof(Details), new { id = documentId.Value }) + "#dms-links")
                : RedirectToAction(nameof(Impact));
        }
    }
}
