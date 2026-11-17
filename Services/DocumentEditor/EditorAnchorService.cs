using Dapper;
using Npgsql;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Model.Master;
namespace DMS.Web.Services.DocumentEditor;
public sealed class EditorAnchorService(EditorSessionStore sessions,IUnitOfWork uow){
 public async Task EnrichAsync(long documentId,IEnumerable<ReviewComment> comments){
  await using var cn=sessions.Connection();
  var anchors=await cn.QueryAsync("SELECT * FROM dms.editor_comment_anchors WHERE document_id=@documentId",new{documentId});
  var map=comments.ToDictionary(x=>x.id);
  foreach(var a in anchors)if(map.TryGetValue((long)a.comment_id,out var c)){c.anchor_type=a.anchor_type;c.anchor_value=a.anchor_value;c.anchor_text=a.anchor_text;c.anchor_source_sha=a.source_sha;}
 }
 public async Task<DbResult> AddAsync(long d,long v,long? parent,string? type,string? section,int? page,string? text,int actor,string? ip,string anchorType,string? anchorValue,string? anchorText){
  anchorType=anchorType.ToUpperInvariant();
  if(anchorType is not ("BOOKMARK" or "QUOTE" or "SECTION" or "PAGE")||anchorValue?.Length>512||anchorText?.Length>4000||page is <=0||
    (anchorType=="QUOTE"&&string.IsNullOrWhiteSpace(anchorText))||(anchorType=="BOOKMARK"&&string.IsNullOrWhiteSpace(anchorValue)))return new DbResult{Success=false,Message="Provide a valid bookmark, quote, section or page anchor."};
  var access=await uow.Document.GetAccessAsync(d,actor);
  if(!access.can_view_content||!access.can_comment||await uow.Document.GetDocumentIdOfVersionAsync(v)!=d)return new DbResult{Success=false,Message="Comment access denied."};
  var version=(await uow.Document.GetVersionsAsync(d)).First(x=>x.id==v);
  await using var cn=sessions.Connection();await cn.OpenAsync();await using var tx=await cn.BeginTransactionAsync();
  try{
   var id=await cn.ExecuteScalarAsync<long>("SELECT DMS.sp_comment_add(@d::bigint,@v::bigint,@parent::bigint,@type::text,@section::text,@page::integer,@text::text,@actor::integer,@ip::text)",new{d,v,parent,type,section,page,text,actor,ip},tx);
   await cn.ExecuteAsync("INSERT INTO dms.editor_comment_anchors(comment_id,document_id,version_id,anchor_type,anchor_value,anchor_text,page_no,source_sha,created_by) VALUES(@id,@d,@v,@anchorType,@anchorValue,@anchorText,@page,@sha,@actor)",new{id,d,v,anchorType,anchorValue,anchorText,page,sha=version.source_sha256,actor},tx);
   await tx.CommitAsync();return new DbResult{Success=true,Message="Comment and location recorded."};
  }catch(PostgresException ex) when(ex.SqlState.StartsWith("45")||ex.SqlState=="P0001"){return new DbResult{Success=false,Message=ex.MessageText};}
 }
}
