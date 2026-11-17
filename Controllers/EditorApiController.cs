using DMS.DataAccess.Repository.IRepository;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.DocumentEditor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
namespace DMS.Web.Controllers;
[Route("api/editor")]
public sealed class EditorApiController(IUnitOfWork uow,EditorSessionStore sessions,EditorFileCommitService commits,
 EditorDownloadService downloads,FileStore files,IOptions<EditorOptions> options,ILogger<EditorApiController> logger,TemplateEditorService templates):DmsBaseController(uow){
 [AllowAnonymous,HttpGet("ping")]
 public IActionResult Ping()=>Json(new{application="DMS",editorIntegration=2});
 [AllowAnonymous,HttpGet("file/{id}")]
 public async Task<IActionResult> Source(string id,string token,CancellationToken ct){
  var s=await sessions.GetAsync(id,ct);if(s==null||!sessions.ValidToken(s,token))return Unauthorized();
  if(s.TemplateId!=null){if(!await templates.ValidAsync(s,ct))return Forbid();}
  else{var access=await uow.Document.GetAccessAsync(s.DocumentId,s.UserId);if(!access.can_view||!access.can_view_content)return Forbid();}
  Response.Headers.CacheControl="no-store";Response.Headers["X-Content-Type-Options"]="nosniff";
  return File(await files.OpenCachedReadAsync(s.SourcePath),FileStore.MimeFromExtension(s.FileName),enableRangeProcessing:true);
 }
 [HttpPost("heartbeat/{id}"),ValidateAntiForgeryToken]
 public async Task<IActionResult> Heartbeat(string id,CancellationToken ct){
  var s=await sessions.GetAsync(id,ct);if(s==null||s.UserId!=CurrentUserId)return Unauthorized();
  if(s.TemplateId!=null){if(!HasPermission(AdminPermissions.Template)||!await templates.ValidAsync(s,ct))return Forbid();}
  else{if(!HasPermission(DocumentPermissions.View))return Forbid();var access=await uow.Document.GetAccessAsync(s.DocumentId,CurrentUserId);if(!access.can_view_content)return Forbid();}
  await sessions.TouchAsync(id,ct);HttpContext.Session.SetString("EditorActivity",DateTime.UtcNow.ToString("O"));return Json(new{ok=true,expiresAt=s.ExpiresAt});
 }
 [AllowAnonymous,HttpPost("onlyoffice/callback/{id}"),IgnoreAntiforgeryToken,RequestSizeLimit(131072)]
 public async Task<IActionResult> OnlyOffice(string id,[FromBody]JsonElement body,CancellationToken ct){
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(2));ct=deadline.Token;
  try{
   var p=options.Value.OnlyOffice;var raw=Request.Headers[p.JwtHeader].FirstOrDefault();
   if(raw?.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)==true)raw=raw[7..];
   if(string.IsNullOrEmpty(raw)&&body.TryGetProperty("token",out var t))raw=t.GetString();
   var signed=EditorJwt.Verify(raw??"",p.JwtSecret); // Use ONLY signed fields, never unsigned body URL/status.
   var s=await sessions.GetAsync(id,ct,true);if(s==null||s.Provider!="ONLYOFFICE"||signed.GetProperty("key").GetString()!=s.DocumentKey)return Unauthorized();
   var status=signed.GetProperty("status").GetInt32();
   if(status==1)return Json(new{error=0});
   if(status==4){await sessions.CloseGroupAsync(s,ct);return Json(new{error=0});}
   if(status is 3 or 7){logger.LogWarning("ONLYOFFICE reported save error {Status} for session {Session}",status,id);return Json(new{error=0});}
   if(status is not (2 or 6)||!s.CanWrite)return BadRequest(new{error=1});
   var url=signed.GetProperty("url").GetString()??"";
   var eventId=EditorFileCommitService.EventId(s,status+"|"+url);
   if(await sessions.CommittedAsync(s.Provider,eventId,ct))return Json(new{error=0});
   if(!signed.TryGetProperty("users",out var users)||users.ValueKind!=JsonValueKind.Array||users.GetArrayLength()!=1||!int.TryParse(users[0].GetString(),out var actor))return Json(new{error=1});
   var actorSession=await sessions.GroupActorAsync(s,actor,ct);
   if(actorSession==null)return Json(new{error=1});
   s=actorSession; // Audit the last editor identified by the signed callback, not a stale callback owner.
   // Refuse silent format conversions under a different source extension.
   if(signed.TryGetProperty("filetype",out var ft)&&!string.Equals(ft.GetString(),Path.GetExtension(s.FileName).TrimStart('.'),StringComparison.OrdinalIgnoreCase))return Json(new{error=1});
   using var response=await downloads.OpenAsync(s.Provider,url,ct);await using var stream=await response.Content.ReadAsStreamAsync(ct);
   var result=await commits.CommitAsync(s,stream,eventId,ClientIp,ct,status==2);
   if(!result.Saved)logger.LogWarning("Editor save refused for {Session}: {Reason}",id,result.Message);
   return Json(new{error=result.Saved?0:1});
  }catch(UnauthorizedAccessException){return Unauthorized(new{error=1});}
  catch(Exception ex){logger.LogError(ex,"ONLYOFFICE callback failed for {Session}",id);return StatusCode(500,new{error=1});}
 }
 public sealed record SpireSave(string SessionId,string Url,string SaveId);
 [HttpPost("spire/save"),ValidateAntiForgeryToken,RequestSizeLimit(16384)]
 public async Task<IActionResult> Spire([FromBody]SpireSave request,CancellationToken ct){
  if(!Guid.TryParse(request.SaveId,out _))return BadRequest(new{ok=false,message="Invalid save identifier."});
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(2));ct=deadline.Token;
  var s=await sessions.GetAsync(request.SessionId,ct);if(s==null||s.Provider!="SPIRE"||s.UserId!=CurrentUserId||!s.CanWrite||!HasPermission(s.TemplateId!=null?AdminPermissions.Template:DocumentPermissions.View))return Unauthorized();
  try{var eventId=EditorFileCommitService.EventId(s,request.SaveId);
   if(await sessions.CommittedAsync(s.Provider,eventId,ct))return Json(new{ok=true,message="Already saved."});
   using var response=await downloads.OpenAsync(s.Provider,request.Url,ct);await using var stream=await response.Content.ReadAsStreamAsync(ct);
   var result=await commits.CommitAsync(s,stream,eventId,ClientIp,ct);return Json(new{ok=result.Saved,message=result.Message});
  }catch(Exception ex){logger.LogError(ex,"Spire save failed for {Session}",s.Id);return BadRequest(new{ok=false,message="Save failed. Check the editor download URL, size and server logs."});}
 }
}
