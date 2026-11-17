using DMS.Model.Documents;
using DMS.Web.CommonUtils;
using DMS.Web.Services.DocumentEditor;
using DMS.Web.Services.Office;
using DMS.Web.Services.Rendition;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers;
public partial class DocumentsController
{
    private async Task<IActionResult> ReviewViewer(long documentId, long versionId, long? commentId)
    {
        var document = await _unitOfWork.Document.GetByIdAsync(documentId);
        var access = await _unitOfWork.Document.GetAccessAsync(documentId, CurrentUserId);
        if (document == null || !access.can_view || !access.can_view_content) return Forbid();
        var versions = await _unitOfWork.Document.GetVersionsAsync(documentId);
        var version = versions.FirstOrDefault(v => v.id == versionId);
        if (version == null) return NotFound();
        var review = new DocumentDetailsViewModel {
            Document=document, Access=access, Versions=versions, CurrentVersion=version,
            Comments=(await _unitOfWork.Workflow.GetCommentsAsync(documentId)).Where(c=>c.document_version_id==versionId).ToList(),
            CurrentUserId=CurrentUserId, CanDisposition=access.can_edit && HasPermission(DocumentPermissions.Edit),
            Attachments=(await _unitOfWork.Document.GetAttachmentsAsync(documentId,true)).Where(a=>a.document_version_id==null||a.document_version_id==versionId).ToList()
        };
        await HttpContext.RequestServices.GetRequiredService<EditorAnchorService>().EnrichAsync(documentId,review.Comments);
        foreach(var c in review.Comments)c.is_current_version=true;
        var status=await _unitOfWork.Rendition.GetStatusAsync(versionId);
        if(status?.IsCurrent!=true && status?.job_status is not ("PENDING" or "RUNNING")) {
            await _unitOfWork.Rendition.RequestAsync(versionId,CurrentUserId); RenditionWorker.Nudge();
        }
        var target=review.Comments.FirstOrDefault(c=>c.id==commentId);
        ViewData["ViewerPage"]=target?.page_no;
        ViewData["ViewerEditUrl"]=access.can_author && HasPermission(WopiService.EditInOfficePermission)
            ? Url.Action(nameof(Editor),new {versionId,mode=EditorMode.Edit}) : null;
        ViewData["EditorWorkspace"]=true;
        ViewData["EditorReturnVersionId"]=versionId;
        ViewData["EditorReturnMode"]="Review";
        Response.Headers.CacheControl="no-store";
        await _unitOfWork.Document.RecordReadAsync(documentId,versionId,"Read-only review viewer opened",CurrentUserId,ClientIp);
        return View("ReviewViewer",review);
    }

    [HttpGet]
    public async Task<IActionResult> ViewerStatus(long id,long versionId)
    {
        if(!HasPermission(DocumentPermissions.View))return Forbid();
        var access=await _unitOfWork.Document.GetAccessAsync(id,CurrentUserId);
        if(!access.can_view || !access.can_view_content)return Forbid();
        if(await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId)!=id)return NotFound();
        Response.Headers.CacheControl="no-store";
        var status=await _unitOfWork.Rendition.GetStatusAsync(versionId);
        return Json(new {ready=status?.IsCurrent==true && status.base_ready,
            failed=status?.job_status=="FAILED", message=status?.job_status=="FAILED"?"The PDF could not be prepared. Ask the document controller to check the rendition queue.":null});
    }
}
