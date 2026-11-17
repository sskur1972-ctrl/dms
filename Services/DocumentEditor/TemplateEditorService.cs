using Dapper;
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.CommonUtils;
using DMS.Web.Services.Authoring;
using DMS.Web.Services.Office;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;

namespace DMS.Web.Services.DocumentEditor;

// Templates have their own target and lease. They never masquerade as document revisions.
public sealed class TemplateEditorService(IConfiguration settings, IUnitOfWork uow, FileStore files,
 IHtmlDocxWriter docx, IEditorHtmlSanitizer sanitizer, IDocumentTextExtractor extractor,
 IOptions<EditorOptions> options, IConfigService config, IWopiDiscoveryClient discovery)
{
 public NpgsqlConnection Connection() => new(settings.GetConnectionString("DMSConnection"));
 public async Task<bool> AllowedAsync(int actor, CancellationToken ct)
 {
  await using var cn=Connection();
  return await cn.ExecuteScalarAsync<bool>(new CommandDefinition(
   "SELECT EXISTS(SELECT 1 FROM common.dms_users WHERE pk_user=@actor AND isactive AND NOT coalesce(isarchived,false)) AND EXISTS(SELECT 1 FROM common.sp_dms_getcurrentuser_permissionforroles(@actor::bigint) WHERE permission=@permission)",
   new{actor,permission=AdminPermissions.Template},cancellationToken:ct));
 }
 public async Task<EditorSessionRecord> CreateAsync(EditorOpenContext context,string provider,CancellationToken ct)
 {
  if(context.TemplateId is not >0 || !context.HoldsEditPermission || context.Mode!=EditorMode.Edit || !await AllowedAsync(context.UserId,ct))
   throw new InvalidOperationException("Template management permission is required.");
  await using var cn=Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  var template=await cn.QuerySingleOrDefaultAsync<TemplateSource>(new CommandDefinition(
   "SELECT id,name,template_version AS Revision,file_name AS FileName,storage_path AS Path,sha256 AS Sha,is_current AS Current,content_html AS Html,settings_json::text AS Settings FROM dms.document_templates WHERE id=@id FOR UPDATE",
   new{id=context.TemplateId},tx,cancellationToken:ct));
  if(template==null||!template.Current)throw new InvalidOperationException("The template is missing or withdrawn.");
  if(string.IsNullOrWhiteSpace(template.Path))
  {
   var clean=sanitizer.Sanitize(template.Html??"<p>&nbsp;</p>",EditorSanitizeLimits.Default);
   if(clean.Rejected)throw new InvalidOperationException("The template body could not be accepted.");
   DocumentPageSetupDto? page=null;
   if(!string.IsNullOrWhiteSpace(template.Settings))page=JsonSerializer.Deserialize<DocumentPageSetupDto>(template.Settings,new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
   using var stream=docx.Write(clean.Html,new DocxWriteMetadata{Title=template.Name,Revision=template.Revision,PageSetup=page});
   var stored=await files.SaveAsync(stream,FileStore.SafeFileName(template.Name)+".docx",FileStore.MimeFromExtension("template.docx"),"templates",ct);
   template.Path=stored.RelativePath;template.FileName=stored.FileName;template.Sha=stored.Sha256;
   await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.document_templates SET file_name=@FileName,storage_path=@RelativePath,mime_type=@MimeType,file_size_bytes=@SizeBytes,sha256=@Sha256,updated_by=@actor,updated_at=now() WHERE id=@id",
    new{stored.FileName,stored.RelativePath,stored.MimeType,stored.SizeBytes,stored.Sha256,actor=context.UserId,id=template.Id},tx,cancellationToken:ct));
   await AuditAsync(cn,tx,template.Id,context.UserId,"OFFICE_SOURCE_CREATED",null,stored.Sha256,context.ClientIp,ct);
  }
  if(!WopiService.IsEditableFile(template.FileName))throw new InvalidOperationException("This template format cannot be edited by the Office provider. Upload DOCX, XLSX, PPTX or OpenDocument content.");
  if(!files.Exists(template.Path))throw new InvalidOperationException("The template's stored Office file is missing. Restore the file-store backup or upload the template again.");
  if(!string.Equals(await files.ComputeSha256Async(template.Path,ct),template.Sha.Trim(),StringComparison.OrdinalIgnoreCase))
   throw new InvalidOperationException("The template file does not match its recorded fingerprint. Restore the verified file-store backup or upload a new template revision.");
  // One provider at a time; an existing provider session shares its document key.
  var lease=await cn.QuerySingleOrDefaultAsync<EditorLease>(new CommandDefinition("SELECT provider,document_key AS DocumentKey,expected_sha AS ExpectedSha FROM dms.template_editor_leases WHERE template_id=@id AND expires_at>now()",new{id=template.Id},tx,cancellationToken:ct));
  if(lease!=null&&!await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM dms.editor_sessions WHERE template_id=@id AND provider=@Provider AND document_key=@DocumentKey AND NOT closed AND can_write AND expires_at>now()) OR EXISTS(SELECT 1 FROM dms.template_editor_leases WHERE template_id=@id AND lock_expires_at>now())",new{id=template.Id,lease.Provider,lease.DocumentKey},tx,cancellationToken:ct)))lease=null;
  if(lease!=null&&(lease.Provider!=provider||lease.ExpectedSha.Trim()!=template.Sha.Trim()))throw new InvalidOperationException("Another editor or template update conflicts with this session. Close the existing editor and try again.");
  var session=new EditorSessionRecord{Id=Guid.NewGuid().ToString("N"),TemplateId=template.Id,UserId=context.UserId,Provider=provider,CanWrite=true,BrowserOrigin=context.BrowserOrigin,
   DocumentKey=lease?.DocumentKey??Guid.NewGuid().ToString("N"),SourcePath=template.Path,FileName=template.FileName,
   ExpiresAt=DateTime.UtcNow.AddMinutes(Math.Clamp(options.Value.SessionMinutes,15,1440))};
  await cn.ExecuteAsync(new CommandDefinition("INSERT INTO dms.template_editor_leases(template_id,provider,document_key,expected_sha,expires_at) VALUES(@TemplateId,@Provider,@DocumentKey,@sha,@ExpiresAt) ON CONFLICT(template_id) DO UPDATE SET provider=EXCLUDED.provider,document_key=EXCLUDED.document_key,expected_sha=EXCLUDED.expected_sha,expires_at=EXCLUDED.expires_at; INSERT INTO dms.editor_sessions(id,provider,template_id,browser_origin,user_id,can_write,document_key,source_path,file_name,expires_at) VALUES(@Id,@Provider,@TemplateId,@BrowserOrigin,@UserId,@CanWrite,@DocumentKey,@SourcePath,@FileName,@ExpiresAt)",new{session.Id,session.Provider,session.TemplateId,session.BrowserOrigin,session.UserId,session.CanWrite,session.DocumentKey,session.SourcePath,session.FileName,session.ExpiresAt,sha=template.Sha.Trim()},tx,cancellationToken:ct));
  await tx.CommitAsync(ct);return session;
 }
 public async Task<bool> ValidAsync(EditorSessionRecord s,CancellationToken ct)
 {
  if(s.TemplateId==null||s.Closed||s.ExpiresAt<=DateTime.UtcNow||!await AllowedAsync(s.UserId,ct))return false;
  await using var cn=Connection();
  if(!await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM dms.editor_sessions WHERE id=@Id AND template_id=@TemplateId AND user_id=@UserId AND NOT closed AND expires_at>now())",s,cancellationToken:ct)))return false;
  var template=await uow.Template.GetByIdAsync(s.TemplateId.Value);
  return template?.is_current==true;
 }
 public async Task<EditorSession> WopiLaunchAsync(EditorSessionRecord s,string? browserOrigin,string token,CancellationToken ct)
 {
  var url=await discovery.GetActionUrlAsync(Path.GetExtension(s.FileName),"edit");
  if(string.IsNullOrWhiteSpace(url))throw new InvalidOperationException("Collabora discovery did not offer editing for this template format.");
  if(!string.IsNullOrWhiteSpace(options.Value.Collabora.BaseUrl))url=EditorUrls.Origin(EditorUrls.Require(options.Value.Collabora.BaseUrl,options.Value.AllowHttpForDevelopment))+new Uri(url).PathAndQuery;
  var host=EditorUrls.Require((await config.GetStringAsync(ConfigKeys.WopiPublicBaseUrl,null,ct))??options.Value.PublicBaseUrl,options.Value.AllowHttpForDevelopment).ToString().TrimEnd('/');
  var source=host+"/wopi/templates/"+s.Id;
  url+=(url.Contains('?')?"&":"?")+"WOPISrc="+Uri.EscapeDataString(source);
  return new EditorSession{SessionId=s.Id,ExpiresAt=s.ExpiresAt,Provider="COLLABORA",Transport="FORM_POST",Title=s.FileName,
   LaunchUrl=url,Mode=EditorMode.Edit,ReadOnly=false,Capabilities=new(true,true,false,false,false),
   PostFields=new(){["access_token"]=token,["access_token_ttl"]=new DateTimeOffset(s.ExpiresAt,TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)}};
 }
 public async Task<EditorCommitResult> CommitAsync(EditorSessionRecord s,Stream content,string eventId,string? ip,CancellationToken ct,bool closeGroup=false,string? wopiLock=null)
 {
  if(!s.CanWrite||!await ValidAsync(s,ct))return new(false,"Template access expired or was withdrawn.");
  await using var cn=Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  var template=await cn.QuerySingleOrDefaultAsync<TemplateSource>(new CommandDefinition("SELECT id,name,sha256 AS Sha,is_current AS Current FROM dms.document_templates WHERE id=@TemplateId FOR UPDATE",s,tx,cancellationToken:ct));
  if(template?.Current!=true)return new(false,"Template withdrawn.");
  if(await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM dms.template_editor_commits WHERE provider=@Provider AND event_id=@eventId)",new{s.Provider,eventId},tx,cancellationToken:ct)))return new(true,"Already saved.");
  var lease=await cn.QuerySingleOrDefaultAsync<TemplateLease>(new CommandDefinition("SELECT provider,document_key AS DocumentKey,expected_sha AS ExpectedSha,lock_value AS LockValue,lock_expires_at AS LockExpiresAt FROM dms.template_editor_leases WHERE template_id=@TemplateId AND expires_at>now() FOR UPDATE",s,tx,cancellationToken:ct));
  if(lease==null||lease.Provider!=s.Provider||lease.DocumentKey!=s.DocumentKey||lease.ExpectedSha.Trim()!=template.Sha.Trim())return new(false,"Template changed outside this session; save refused.");
  if(s.Provider=="COLLABORA"&&(!(lease.LockExpiresAt>DateTime.UtcNow)||string.IsNullOrEmpty(lease.LockValue)||lease.LockValue!=wopiLock))return new(false,"The template editing lock is missing or changed.");
  var stored=await files.SaveAsync(content,s.FileName,FileStore.MimeFromExtension(s.FileName),"templates",ct,(long)Math.Clamp(options.Value.MaxFileMb,1,200)*1024*1024);
  if(stored.SizeBytes==0){files.Archive(stored.RelativePath);return new(false,"Empty template refused.");}
  string? html=null;
  if(Path.GetExtension(stored.FileName).Equals(".docx",StringComparison.OrdinalIgnoreCase))
  {
   var extracted=await extractor.ExtractHtmlAsync(stored.RelativePath,ct);
   if(!string.IsNullOrWhiteSpace(extracted)){var clean=sanitizer.Sanitize(extracted,EditorSanitizeLimits.Default);if(!clean.Rejected)html=clean.Html;}
  }
  // The Office file is authoritative. Its original package is copied to new documents without HTML conversion.
  await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.document_templates SET file_name=@FileName,storage_path=@RelativePath,mime_type=@MimeType,file_size_bytes=@SizeBytes,sha256=@Sha256,content_html=@html,authoring_mode=@mode,updated_by=@actor,updated_at=now() WHERE id=@id; UPDATE dms.template_editor_leases SET expected_sha=@Sha256 WHERE template_id=@id; UPDATE dms.editor_sessions SET source_path=@RelativePath WHERE template_id=@id AND document_key=@DocumentKey; INSERT INTO dms.template_editor_commits(provider,event_id,template_id,source_sha) VALUES(@Provider,@eventId,@id,@Sha256)",new{stored.FileName,stored.RelativePath,stored.MimeType,stored.SizeBytes,stored.Sha256,html,mode=string.IsNullOrWhiteSpace(html)?"FILE":"BOTH",actor=s.UserId,id=s.TemplateId,s.DocumentKey,s.Provider,eventId},tx,cancellationToken:ct));
  await AuditAsync(cn,tx,s.TemplateId!.Value,s.UserId,"OFFICE_TEMPLATE_SAVED",template.Sha.Trim(),stored.Sha256,ip,ct);
  if(closeGroup)await CloseAsync(s,cn,tx,ct);
  await tx.CommitAsync(ct);return new(true,"Template saved.",stored.Sha256,stored.SizeBytes);
 }
 public Task CloseAsync(EditorSessionRecord s,NpgsqlConnection cn,NpgsqlTransaction tx,CancellationToken ct,bool abandon=false) =>
  cn.ExecuteAsync(new CommandDefinition(abandon?
   "UPDATE dms.editor_sessions SET closed=true WHERE id=@Id; DELETE FROM dms.template_editor_leases WHERE template_id=@TemplateId AND document_key=@DocumentKey AND NOT EXISTS(SELECT 1 FROM dms.editor_sessions WHERE template_id=@TemplateId AND document_key=@DocumentKey AND NOT closed AND expires_at>now())":
   "UPDATE dms.editor_sessions SET closed=true WHERE template_id=@TemplateId AND document_key=@DocumentKey; DELETE FROM dms.template_editor_leases WHERE template_id=@TemplateId AND document_key=@DocumentKey",s,tx,cancellationToken:ct));
 static Task AuditAsync(NpgsqlConnection cn,NpgsqlTransaction tx,long id,int actor,string action,string? before,string after,string? ip,CancellationToken ct) =>
  cn.ExecuteAsync(new CommandDefinition("SELECT dms.sp_audit_write('DOCUMENT_TEMPLATE',@id::bigint,@id::text,@action::text,'Office template editor'::text,@actor::integer,@before::json,@after::json,NULL::text,@ip::text)",new{id,actor,action,before=JsonSerializer.Serialize(new{Sha=before}),after=JsonSerializer.Serialize(new{Sha=after}),ip},tx,cancellationToken:ct));
 public sealed class TemplateSource{public long Id{get;set;}public string Name{get;set;}="Template";public string Revision{get;set;}="01";public string FileName{get;set;}="";public string Path{get;set;}="";public string Sha{get;set;}="";public bool Current{get;set;}public string? Html{get;set;}public string? Settings{get;set;}}
 public sealed class TemplateLease{public string Provider{get;set;}="";public string DocumentKey{get;set;}="";public string ExpectedSha{get;set;}="";public string? LockValue{get;set;}public DateTime? LockExpiresAt{get;set;}}
}
