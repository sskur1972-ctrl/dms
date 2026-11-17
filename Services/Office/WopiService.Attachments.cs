using DMS.Model.Documents;
using DMS.Web.Services.DocumentEditor;
using System.Globalization;
namespace DMS.Web.Services.Office;
public sealed partial class WopiService
{
 public async Task<EditorSession?> OpenAttachmentAsync(DocumentAttachment a,int userId,string origin){
  if(!await IsEnabledAsync())return null;
  var access=await _unitOfWork.Document.GetAccessAsync(a.document_id,userId);
  if(!access.can_view||!access.can_view_content||a.storage_path==null)return null;
  var ext=Path.GetExtension(a.file_name);
  var url=await _discovery.GetActionUrlAsync(ext,ext.Equals(".pdf",StringComparison.OrdinalIgnoreCase)?"view_comment":"view");
  var host=(await _config.GetStringAsync(ConfigKeys.WopiPublicBaseUrl,null))?.TrimEnd('/');
  if(string.IsNullOrWhiteSpace(url)||string.IsNullOrWhiteSpace(host))return null;
  EditorUrls.Require(host,_options.AllowHttpForDevelopment);
  if(!string.IsNullOrWhiteSpace(_options.Collabora.BaseUrl))url=EditorUrls.Origin(EditorUrls.Require(_options.Collabora.BaseUrl,_options.AllowHttpForDevelopment))+new Uri(url).PathAndQuery;
  var expires=DateTime.UtcNow.AddMinutes(Math.Clamp(_options.SessionMinutes,15,1440));
  var token=new WopiTokenDto{AttachmentId=a.id,AttachmentDocumentId=a.document_id,UserId=userId,ExpiresAtUtc=expires,BrowserOrigin=origin,Permissions=WopiPermissionsDto.ReadOnly};
  return new EditorSession{Provider="COLLABORA",Mode=EditorMode.View,ReadOnly=true,Transport="FORM_POST",DocumentId=a.document_id,Title=a.title??a.file_name??"Attachment",
   LaunchUrl=AppendQuery(url,"WOPISrc="+Uri.EscapeDataString($"{host}/wopi/attachments/{a.id}")),
   PostFields=new(){["access_token"]=_tokens.Protect(token),["access_token_ttl"]=new DateTimeOffset(expires,TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}};
 }
 private async Task<(DocumentAttachment? Attachment,WopiTokenDto? Token)> LoadAttachmentAsync(long id,string? accessToken){
  var token=_tokens.Unprotect(accessToken);
  if(token?.AttachmentId!=id||token.AttachmentDocumentId==null||token.Permissions.CanWrite)return(null,null);
  var access=await _unitOfWork.Document.GetAccessAsync(token.AttachmentDocumentId.Value,(int)token.UserId);
  if(!access.can_view||!access.can_view_content)return(null,null);
  var a=(await _unitOfWork.Document.GetAttachmentsAsync(token.AttachmentDocumentId.Value,true)).FirstOrDefault(a=>a.id==id);
  return(a,token);
 }
 public async Task<WopiCheckFileInfoDto?> CheckAttachmentAsync(long id,string? accessToken){
  var(a,t)=await LoadAttachmentAsync(id,accessToken);if(a?.storage_path==null||t==null)return null;
  return new WopiCheckFileInfoDto{BaseFileName=a.file_name??"attachment",Size=a.file_size_bytes,Version=a.sha256??a.attachment_version.ToString(),UserId=t.UserId.ToString(),UserFriendlyName="DMS reviewer",OwnerId=a.document_id.ToString(),PostMessageOrigin=t.BrowserOrigin,
   ReadOnly=true,UserCanWrite=false,SupportsUpdate=false,SupportsLocks=false,SupportsGetLock=false,DisablePrint=true,HidePrintOption=true,DisableExport=true,HideExportOption=true,HideSaveOption=true,DisableCopy=true,UserCanNotWriteRelative=true};
 }
 public async Task<Stream?> GetAttachmentAsync(long id,string? accessToken){
  var(a,t)=await LoadAttachmentAsync(id,accessToken);return a?.storage_path==null||t==null?null:await _fileStore.OpenCachedReadAsync(a.storage_path);
 }
}
