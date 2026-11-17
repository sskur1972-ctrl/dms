using Microsoft.AspNetCore.Mvc;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.Authoring;
namespace DMS.Web.Controllers;
public partial class DocumentsController {
 public sealed class EditorSectionRequest { public string? Selection { get; set; } public string? Section { get; set; } public int? Page{get;set;} }
 [HttpPost,ValidateAntiForgeryToken,RequestSizeLimit(16384)]
 public async Task<IActionResult> EditorSection(long versionId,[FromBody] EditorSectionRequest request){
  if(!HasPermission(DocumentPermissions.View))return Forbid();
  if(request==null || request.Selection?.Length>4000 || request.Section?.Length>128
      || request.Page is <1 or >10000 || (string.IsNullOrWhiteSpace(request.Selection)&&string.IsNullOrWhiteSpace(request.Section)&&request.Page==null))return BadRequest();
  var id=await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId);if(id==null)return NotFound();
  var access=await _unitOfWork.Document.GetAccessAsync(id.Value,CurrentUserId);
  if(!access.can_view||!access.can_view_content)return Forbid();
  var version=(await _unitOfWork.Document.GetVersionsAsync(id.Value)).FirstOrDefault(v=>v.id==versionId);
  if(version?.source_storage_path==null)return NotFound();
  if(!string.Equals(Path.GetExtension(version.source_file_name),".docx",StringComparison.OrdinalIgnoreCase)
      || version.source_size_bytes>25*1024*1024)return Json(new { section=(string?)null });
  using var source=await _fileStore.OpenCachedReadAsync(version.source_storage_path);
  try {
   if(!string.IsNullOrWhiteSpace(request.Section))return Json(new { text=Services.DocumentEditor.DocumentSectionLocator.Find(source,"",request.Section) });
   var section=!string.IsNullOrWhiteSpace(request.Selection)?Services.DocumentEditor.DocumentSectionLocator.Find(source,request.Selection):null;
   if(section==null&&request.Page!=null)section=await HttpContext.RequestServices.GetRequiredService<Services.DocumentEditor.PageSectionService>().Find(versionId,request.Page.Value,HttpContext.RequestAborted);
   return Json(new { section });
  }
  catch(Exception ex) when(ex is InvalidDataException or System.Xml.XmlException or InvalidOperationException or HttpRequestException){return Json(new { section=(string?)null });}
 }
 [HttpPost,ValidateAntiForgeryToken,RequestSizeLimit(16384)]
 public async Task<IActionResult> EditorSections(long versionId,[FromBody] EditorSectionRequest request){
  if(!HasPermission(DocumentPermissions.View))return Forbid();
  if(request?.Page is not int page||page<1||page>10000)return BadRequest();
  var id=await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId);if(id==null)return NotFound();
  var access=await _unitOfWork.Document.GetAccessAsync(id.Value,CurrentUserId);
  if(!access.can_view||!access.can_view_content)return Forbid();
  var version=(await _unitOfWork.Document.GetVersionsAsync(id.Value)).FirstOrDefault(v=>v.id==versionId);
  if(version?.source_storage_path==null)return NotFound();
  if(!string.Equals(Path.GetExtension(version.source_file_name),".docx",StringComparison.OrdinalIgnoreCase)
      ||version.source_size_bytes>25*1024*1024)return Json(new{sections=Array.Empty<string>(),selected=(string?)null});
  var sections=await HttpContext.RequestServices.GetRequiredService<Services.DocumentEditor.PageSectionService>().FindOptions(versionId,page,HttpContext.RequestAborted);
  return Json(new {sections,selected=sections.FirstOrDefault()});
 }
 [HttpGet]
 public async Task<IActionResult> EditorLocation(long versionId,CancellationToken ct){
  if(!HasPermission(DocumentPermissions.View))return NoPermission();
  var id=await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId);if(id==null)return NotFound();
  var access=await _unitOfWork.Document.GetAccessAsync(id.Value,CurrentUserId);if(!access.can_view||!access.can_view_content)return Forbid();
  var v=(await _unitOfWork.Document.GetVersionsAsync(id.Value)).FirstOrDefault(x=>x.id==versionId);
  if(v?.source_storage_path==null)return NotFound();
  if(v.source_size_bytes>25*1024*1024)return Content("This saved source exceeds the text locator limit. Use its saved PDF page location.");
  var renderer=HttpContext.RequestServices.GetRequiredService<IDocumentPreviewRenderer>();
  var html=await renderer.RenderAsync(v.source_storage_path,v.source_file_name,v.source_mime_type,ct);
  var sanitizer=HttpContext.RequestServices.GetRequiredService<IEditorHtmlSanitizer>();
  ViewData["SavedHtml"]=sanitizer.Sanitize(html.Html ?? "Saved text is unavailable for this format.",new EditorSanitizeLimits()).Html;
  await _unitOfWork.Authoring.RecordVersionEventAsync(versionId,"EDITOR_LOCATION_VIEWED","Opened saved text locator.",null,CurrentUserId,ClientIp);
  var document=await _unitOfWork.Document.GetByIdAsync(id.Value);
  var rendition=await _unitOfWork.Rendition.GetStatusAsync(versionId);
  ViewData["LocatorWatermark"]=access.apply_screen_watermark ? Services.DocumentEditor.ScreenCopyLabel.Resolve(
      rendition?.expected_watermark,document?.state_is_effective==true,document?.is_obsolete==true) : null;
  Response.Headers.CacheControl="no-store";
  return View("EditorLocation");
 }
}
