using Dapper;
using DMS.DataAccess.Repository.IRepository;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DMS.Web.Services.Authoring;
namespace DMS.Web.Services.DocumentEditor;
public sealed record EditorCommitResult(bool Saved,string Message,string? Sha=null,long Size=0);
public sealed class EditorFileCommitService(EditorSessionStore sessions,IUnitOfWork uow,FileStore files,IOptions<EditorOptions> options,TemplateEditorService templates,IDocumentTextExtractor extractor) {
 public static string EventId(EditorSessionRecord s,string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Provider+"|"+s.DocumentKey+"|"+value)));
 public async Task<EditorCommitResult> CommitAsync(EditorSessionRecord s,Stream content,string eventId,string? ip,CancellationToken ct,bool closeGroup=false){
  if(s.TemplateId!=null)return await templates.CommitAsync(s,content,eventId,ip,ct,closeGroup);
  await using var cn=sessions.Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  await cn.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@VersionId)",s,tx,cancellationToken:ct));
  if(await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM dms.editor_commits WHERE provider=@Provider AND event_id=@eventId)",new{s.Provider,eventId},tx,cancellationToken:ct)))return new(true,"Already committed.");
  var current=await sessions.GetAsync(s.Id,ct);
  if(current==null||!s.CanWrite)return new(false,"Editor session expired or is read-only. Reopen the document.");
  var doc=await uow.Document.GetByIdAsync(s.DocumentId);var access=await uow.Document.GetAccessAsync(s.DocumentId,s.UserId);
  if(doc==null||doc.current_version_id!=s.VersionId||!access.can_author||!access.can_view_content)return new(false,"Workflow or document access no longer allows this save.");
  var v=(await uow.Document.GetVersionsAsync(s.DocumentId)).First(x=>x.id==s.VersionId);
  var lease=await cn.QuerySingleOrDefaultAsync<EditorLease>(new CommandDefinition("SELECT provider,document_key AS DocumentKey,expected_sha AS ExpectedSha FROM dms.editor_leases WHERE version_id=@VersionId AND expires_at>now()",s,tx,cancellationToken:ct));
  if(lease==null||lease.Provider!=s.Provider||lease.DocumentKey!=s.DocumentKey||lease.ExpectedSha.Trim()!=(v.source_sha256??"").Trim())return new(false,"The source changed outside this editing session; save refused to avoid overwriting it.");
  var held=await uow.Authoring.GetLockAsync(s.VersionId);
  if(s.Provider!="COLLABORA"&&held!=null&&!held.HasExpired(DateTime.UtcNow))return new(false,"A WOPI lock is active.");
  var stored=await files.SaveAsync(content,s.FileName,FileStore.MimeFromExtension(s.FileName),"documents",ct,(long)Math.Clamp(options.Value.MaxFileMb,1,200)*1024*1024);
  if(stored.SizeBytes==0){files.Archive(stored.RelativePath);return new(false,"Empty editor file refused.");}
  var audit=await SavedContentAudit.CaptureAsync(extractor,v.source_storage_path,v.source_file_name,stored.RelativePath,stored.FileName,ct);
  audit=SavedContentAudit.WithEvidence(audit,v.source_sha256,v.source_storage_path,stored.Sha256,stored.RelativePath);
  // Existing workflow routine and receipt commit together. Keep staged file on uncertain DB failure.
  var id=await cn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT DMS.sp_document_set_source_from_editor(@v::bigint,@fname::text,@path::text,@mime::text,@size::bigint,@sha::text,@before::json,@after::json,@detail::text,@actor::integer,@ip::text)",new{v=s.VersionId,fname=stored.FileName,path=stored.RelativePath,mime=stored.MimeType,size=stored.SizeBytes,sha=stored.Sha256,before=audit.Before,after=audit.After,detail="Document content saved.",actor=s.UserId,ip},tx,cancellationToken:ct));
  if(id<=0)throw new InvalidOperationException("Database refused the editor save.");
  await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.editor_leases SET expected_sha=@sha,source_path=@path WHERE version_id=@VersionId AND document_key=@DocumentKey; UPDATE dms.editor_sessions SET source_path=@path WHERE version_id=@VersionId AND provider=@Provider AND document_key=@DocumentKey; INSERT INTO dms.editor_commits(provider,event_id,version_id,source_sha,size_bytes) VALUES(@Provider,@eventId,@VersionId,@sha,@size)",new{s.VersionId,s.Provider,s.DocumentKey,eventId,path=stored.RelativePath,sha=stored.Sha256,size=stored.SizeBytes},tx,cancellationToken:ct));
  if(closeGroup)await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.editor_sessions SET closed=true WHERE provider=@Provider AND document_key=@DocumentKey; DELETE FROM dms.editor_leases WHERE version_id=@VersionId AND document_key=@DocumentKey",s,tx,cancellationToken:ct));
  await tx.CommitAsync(ct);
  DMS.Web.Services.Rendition.RenditionWorker.Nudge();
  return new(true,"Saved.",stored.Sha256,stored.SizeBytes);
 }
}
