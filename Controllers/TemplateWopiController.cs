using Dapper;
using DMS.Web.Services;
using DMS.Web.Services.DocumentEditor;
using DMS.Web.Services.Office;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers;

[AllowAnonymous,IgnoreAntiforgeryToken,ApiController,WopiRawBody]
[Route("wopi/templates")]
public sealed class TemplateWopiController(EditorSessionStore sessions,TemplateEditorService templates,FileStore files):ControllerBase
{
 private async Task<EditorSessionRecord?> Authorized(string id,string? token,CancellationToken ct)
 {
  var s=await sessions.GetAsync(id,ct);
  return s?.TemplateId!=null && s.Provider=="COLLABORA" && !string.IsNullOrWhiteSpace(token) &&
   sessions.ValidToken(s,token) && await templates.ValidAsync(s,ct)?s:null;
 }
 [HttpGet("{id}")]
 public async Task<IActionResult> Info(string id,[FromQuery(Name="access_token")]string? token,CancellationToken ct)
 {
  var s=await Authorized(id,token,ct);if(s==null)return Unauthorized();
  using var content=await files.OpenCachedReadAsync(s.SourcePath);
  var sha=await files.ComputeSha256Async(s.SourcePath,ct);
  Response.Headers.CacheControl="no-store";
  return new JsonResult(new WopiCheckFileInfoDto{BaseFileName=s.FileName,Size=content.Length,Version=sha,
   UserId=s.UserId.ToString(),UserFriendlyName="DMS user "+s.UserId,OwnerId=s.UserId.ToString(),
   UserCanWrite=true,ReadOnly=false,SupportsUpdate=true,SupportsLocks=true,SupportsGetLock=true,
   DisablePrint=true,HidePrintOption=true,DisableExport=true,HideExportOption=true,DisableCopy=true,
   PostMessageOrigin=s.BrowserOrigin,BreadcrumbDocName=s.FileName,WatermarkText=""});
 }
 [HttpGet("{id}/contents")]
 public async Task<IActionResult> Content(string id,[FromQuery(Name="access_token")]string? token,CancellationToken ct)
 {
  var s=await Authorized(id,token,ct);if(s==null)return Unauthorized();
  Response.Headers.CacheControl="no-store";return File(await files.OpenCachedReadAsync(s.SourcePath),"application/octet-stream");
 }
 [HttpPost("{id}/contents"),RequestSizeLimit(209715200)]
 public async Task<IActionResult> Save(string id,[FromQuery(Name="access_token")]string? token,CancellationToken ct)
 {
  var s=await Authorized(id,token,ct);if(s==null)return Unauthorized();
  try{
   var result=await templates.CommitAsync(s,Request.Body,Guid.NewGuid().ToString("N"),HttpContext.Connection.RemoteIpAddress?.ToString(),ct,
    wopiLock:Request.Headers["X-WOPI-Lock"].FirstOrDefault());
   if(result.Saved){Response.Headers["X-WOPI-ItemVersion"]=result.Sha;return Ok();}
   Response.Headers["X-WOPI-Lock"]=await CurrentLock(s,ct);return Conflict();
  }catch(StoredFileTooLargeException){return StatusCode(413);}
 }
 private async Task<string> CurrentLock(EditorSessionRecord s,CancellationToken ct)
 {
  await using var cn=templates.Connection();return await cn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT coalesce(lock_value,'') FROM dms.template_editor_leases WHERE template_id=@TemplateId AND document_key=@DocumentKey AND lock_expires_at>now()",s,cancellationToken:ct))??"";
 }
 [HttpPost("{id}")]
 public async Task<IActionResult> Lock(string id,[FromQuery(Name="access_token")]string? token,CancellationToken ct)
 {
  var s=await Authorized(id,token,ct);if(s==null)return Unauthorized();
  var operation=Request.Headers["X-WOPI-Override"].ToString().ToUpperInvariant();
  if(operation=="GET_LOCK"){Response.Headers["X-WOPI-Lock"]=await CurrentLock(s,ct);return Ok();}
  if(operation is not ("LOCK" or "REFRESH_LOCK" or "UNLOCK"))return StatusCode(501);
  var requested=Request.Headers["X-WOPI-Lock"].ToString();if(string.IsNullOrEmpty(requested)||requested.Length>1024)return BadRequest();
  await using var cn=templates.Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  // Match template open/save lock ordering before releasing the final editor lease.
  await cn.ExecuteAsync(new CommandDefinition("SELECT id FROM dms.document_templates WHERE id=@TemplateId FOR UPDATE",s,tx,cancellationToken:ct));
  var lease=await cn.QuerySingleOrDefaultAsync<TemplateEditorService.TemplateLease>(new CommandDefinition("SELECT lock_value AS LockValue,lock_expires_at AS LockExpiresAt FROM dms.template_editor_leases WHERE template_id=@TemplateId AND document_key=@DocumentKey AND expires_at>now() FOR UPDATE",s,tx,cancellationToken:ct));
  if(lease==null)return Unauthorized();
  var held=lease.LockExpiresAt>DateTime.UtcNow?lease.LockValue??"":"";
  var old=Request.Headers["X-WOPI-OldLock"].ToString();
  if((operation=="LOCK" && held!="" && held!=(old!=""?old:requested)) || (operation!="LOCK" && held!=requested))
  {Response.Headers["X-WOPI-Lock"]=held;return Conflict();}
  await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.template_editor_leases SET lock_value=@value,lock_expires_at=@expiry WHERE template_id=@TemplateId AND document_key=@DocumentKey",new{s.TemplateId,s.DocumentKey,value=operation=="UNLOCK"?null:requested,expiry=operation=="UNLOCK"?(DateTime?)null:DateTime.UtcNow.AddMinutes(30)},tx,cancellationToken:ct));
  if(operation=="UNLOCK")await templates.CloseAsync(s,cn,tx,ct);
  await tx.CommitAsync(ct);return Ok();
 }
}
