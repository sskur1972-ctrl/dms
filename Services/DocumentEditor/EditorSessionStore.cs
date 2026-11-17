using Dapper;
using Npgsql;
using DMS.DataAccess.Repository.IRepository;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
namespace DMS.Web.Services.DocumentEditor;
public sealed class EditorSessionRecord {
 public string Id {get;set;}=""; public string Provider {get;set;}="";
 public long DocumentId {get;set;} public long VersionId {get;set;} public long? TemplateId {get;set;} public int UserId {get;set;}
 public bool CanWrite {get;set;} public string DocumentKey {get;set;}="";
 public string SourcePath {get;set;}=""; public string FileName {get;set;}=""; public string? BrowserOrigin {get;set;}
 public DateTime ExpiresAt {get;set;} public bool Closed {get;set;}
}
public sealed class EditorLease {
 public string Provider {get;set;}=""; public string DocumentKey {get;set;}="";
 public string ExpectedSha {get;set;}=""; public string SourcePath {get;set;}="";
}
public sealed class EditorSessionStore(IConfiguration config,IUnitOfWork uow,
 IDataProtectionProvider protection,IOptions<EditorOptions> options,IDistributedCache cache,ILogger<EditorSessionStore> logger,TemplateEditorService templates) {
 readonly IDataProtector protector=protection.CreateProtector("DMS.Editor.File.v2");
 public NpgsqlConnection Connection()=>new(config.GetConnectionString("DMSConnection"));
 const string Columns="id,provider,coalesce(document_id,0) AS DocumentId,coalesce(version_id,0) AS VersionId,template_id AS TemplateId,browser_origin AS BrowserOrigin,user_id AS UserId,can_write AS CanWrite,document_key AS DocumentKey,source_path AS SourcePath,file_name AS FileName,expires_at AS ExpiresAt,closed";
 public async Task<EditorSessionRecord?> GetAsync(string id,CancellationToken ct,bool includeClosed=false) {
  if(!Guid.TryParseExact(id,"N",out _)) return null;
  await using var cn=Connection();
  return await cn.QuerySingleOrDefaultAsync<EditorSessionRecord>(new CommandDefinition($"SELECT {Columns} FROM dms.editor_sessions WHERE id=@id AND (@includeClosed OR (NOT closed AND expires_at>now()))",new{id,includeClosed},cancellationToken:ct));
 }
 public string FileToken(EditorSessionRecord s)=>protector.Protect(s.Id);
 public bool ValidToken(EditorSessionRecord s,string token) {
  try{return !s.Closed&&s.ExpiresAt>DateTime.UtcNow&&protector.Unprotect(token)==s.Id;}catch{return false;}
 }
 public async Task<EditorSessionRecord> CreateAsync(EditorOpenContext c,string provider,CancellationToken ct) {
  if(c.TemplateId!=null){
   var template=await templates.CreateAsync(c,provider,ct);
   try{await cache.SetStringAsync("editor:"+template.Id,System.Text.Json.JsonSerializer.Serialize(new{template.Provider,template.TemplateId,template.UserId,template.ExpiresAt}),new DistributedCacheEntryOptions{AbsoluteExpiration=template.ExpiresAt},ct);}
   catch(Exception ex){logger.LogWarning("Template metadata cache unavailable ({ErrorType}); database remains authoritative.",ex.GetType().Name);}
   return template;
  }
  var did=await uow.Document.GetDocumentIdOfVersionAsync(c.VersionId)??throw new InvalidOperationException("Revision not found.");
  var doc=await uow.Document.GetByIdAsync(did);
  var v=(await uow.Document.GetVersionsAsync(did)).First(x=>x.id==c.VersionId);
  var access=await uow.Document.GetAccessAsync(did,c.UserId);
  if(!access.can_view||!access.can_view_content) throw new InvalidOperationException("Document access denied.");
  if(string.IsNullOrEmpty(v.source_storage_path)||!Office.WopiService.IsEditableFile(v.source_file_name)) throw new InvalidOperationException("Upload an editable Office document first.");
  var write=c.Mode==EditorMode.Edit&&c.HoldsEditPermission&&access.can_author&&doc.current_version_id==v.id;
  if(c.Mode==EditorMode.Edit&&!write) throw new InvalidOperationException("This revision is not editable by your account.");
  var s=new EditorSessionRecord{Id=Guid.NewGuid().ToString("N"),Provider=provider,DocumentId=did,VersionId=v.id,UserId=c.UserId,CanWrite=write,DocumentKey=Guid.NewGuid().ToString("N"),SourcePath=v.source_storage_path,FileName=v.source_file_name!,BrowserOrigin=c.BrowserOrigin,ExpiresAt=DateTime.UtcNow.AddMinutes(Math.Clamp(options.Value.SessionMinutes,15,1440))};
  await using var cn=Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@v)",new{v=s.VersionId},tx,cancellationToken:ct));
  if(write) {
   // A save may have completed while this request was waiting for the document lock.
   v=(await uow.Document.GetVersionsAsync(did)).First(x=>x.id==c.VersionId);
   s.SourcePath=v.source_storage_path!;s.FileName=v.source_file_name!;
   var held=await uow.Authoring.GetLockAsync(v.id);
   if(provider!="COLLABORA"&&held!=null&&!held.HasExpired(DateTime.UtcNow))throw new InvalidOperationException("A Collabora editing lock is active. Close and save that session first.");
   var lease=await cn.QuerySingleOrDefaultAsync<EditorLease>(new CommandDefinition("SELECT provider,document_key AS DocumentKey,expected_sha AS ExpectedSha,source_path AS SourcePath FROM dms.editor_leases WHERE version_id=@v AND expires_at>now()",new{v=v.id},tx,cancellationToken:ct));
   if(lease!=null) {
    var live=await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM dms.editor_sessions WHERE version_id=@version AND provider=@Provider AND document_key=@DocumentKey AND can_write AND NOT closed AND expires_at>now())",new{version=v.id,lease.Provider,lease.DocumentKey},tx,cancellationToken:ct));
    if(live || (held!=null&&!held.HasExpired(DateTime.UtcNow))) {
     if(lease.Provider!=provider||!string.Equals(lease.ExpectedSha.Trim(),(v.source_sha256??"").Trim(),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Another active editor or source change conflicts with this session. Save and close the existing editor first.");
     s.DocumentKey=lease.DocumentKey;
     // Collabora identifies the live document by WOPISrc; callback editors cache by document key.
     s.SourcePath=v.source_storage_path!;
    }
   }
   await cn.ExecuteAsync(new CommandDefinition("INSERT INTO dms.editor_leases VALUES(@VersionId,@Provider,@DocumentKey,@sha,@SourcePath,@ExpiresAt) ON CONFLICT(version_id) DO UPDATE SET provider=EXCLUDED.provider,document_key=EXCLUDED.document_key,expected_sha=EXCLUDED.expected_sha,source_path=EXCLUDED.source_path,expires_at=EXCLUDED.expires_at",new{s.VersionId,s.Provider,s.DocumentKey,sha=(v.source_sha256??"").Trim(),s.SourcePath,s.ExpiresAt},tx,cancellationToken:ct));
  }
  await cn.ExecuteAsync(new CommandDefinition("INSERT INTO dms.editor_sessions(id,provider,document_id,version_id,user_id,can_write,document_key,source_path,file_name,browser_origin,expires_at) VALUES(@Id,@Provider,@DocumentId,@VersionId,@UserId,@CanWrite,@DocumentKey,@SourcePath,@FileName,@BrowserOrigin,@ExpiresAt)",s,tx,cancellationToken:ct));await tx.CommitAsync(ct);
  try{await cache.SetStringAsync("editor:"+s.Id,System.Text.Json.JsonSerializer.Serialize(new{s.Provider,s.DocumentId,s.VersionId,s.UserId,s.ExpiresAt}),new DistributedCacheEntryOptions{AbsoluteExpiration=s.ExpiresAt},ct);}catch(Exception ex){logger.LogWarning(ex,"Editor metadata cache unavailable; database remains authoritative.");}
  return s;
 }
 public async Task<EditorSessionRecord?> GroupActorAsync(EditorSessionRecord s,int actor,CancellationToken ct){
  await using var cn=Connection();return await cn.QueryFirstOrDefaultAsync<EditorSessionRecord>(new CommandDefinition($"SELECT {Columns} FROM dms.editor_sessions WHERE provider=@Provider AND document_key=@DocumentKey AND user_id=@actor AND can_write AND NOT closed AND expires_at>now() ORDER BY expires_at DESC",new{s.Provider,s.DocumentKey,actor},cancellationToken:ct));
 }
 public async Task TouchAsync(string id,CancellationToken ct){await using var cn=Connection();await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.editor_sessions SET last_activity=now() WHERE id=@id AND NOT closed AND expires_at>now()",new{id},cancellationToken:ct));}
 public async Task<bool> CommittedAsync(string provider,string eventId,CancellationToken ct){await using var cn=Connection();return await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM dms.editor_commits WHERE provider=@provider AND event_id=@eventId) OR EXISTS(SELECT 1 FROM dms.template_editor_commits WHERE provider=@provider AND event_id=@eventId)",new{provider,eventId},cancellationToken:ct));}
 public async Task CloseGroupAsync(EditorSessionRecord s,CancellationToken ct){
  await using var cn=Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  if(s.TemplateId!=null){await templates.CloseAsync(s,cn,tx,ct);await tx.CommitAsync(ct);return;}
  await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@VersionId); UPDATE dms.editor_sessions SET closed=true WHERE provider=@Provider AND document_key=@DocumentKey; DELETE FROM dms.editor_leases WHERE version_id=@VersionId AND document_key=@DocumentKey",s,tx,cancellationToken:ct));await tx.CommitAsync(ct);
 }
 public async Task<bool> UnlockWopiGroupAsync(EditorSessionRecord s,string lockValue,CancellationToken ct){
  if(s.Provider!="COLLABORA"||s.TemplateId!=null)return false;
  await using var cn=Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  // Reopening and final unlock share the same lock; a newly opened group cannot
  // accidentally be closed between releasing the WOPI lock and its host lease.
  await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@VersionId)",s,tx,cancellationToken:ct));
  var released=await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT dms.sp_editlock_release(@version::bigint,@lockValue::text)",new{version=s.VersionId,lockValue},tx,cancellationToken:ct));
  if(released)await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.editor_sessions SET closed=true WHERE version_id=@VersionId AND provider=@Provider AND document_key=@DocumentKey; DELETE FROM dms.editor_leases WHERE version_id=@VersionId AND document_key=@DocumentKey",s,tx,cancellationToken:ct));
  await tx.CommitAsync(ct);return released;
 }
 public async Task AbandonAsync(EditorSessionRecord s,CancellationToken ct){
  await using var cn=Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  if(s.TemplateId!=null){await templates.CloseAsync(s,cn,tx,ct,true);await tx.CommitAsync(ct);return;}
  await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@VersionId); UPDATE dms.editor_sessions SET closed=true WHERE id=@Id; DELETE FROM dms.editor_leases WHERE version_id=@VersionId AND document_key=@DocumentKey AND NOT EXISTS(SELECT 1 FROM dms.editor_sessions WHERE document_key=@DocumentKey AND NOT closed AND expires_at>now())",s,tx,cancellationToken:ct));await tx.CommitAsync(ct);
 }
}
