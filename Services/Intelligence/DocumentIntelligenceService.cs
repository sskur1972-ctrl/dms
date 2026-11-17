using Dapper;
using Npgsql;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.Services.Authoring;
using System.Text.RegularExpressions;
namespace DMS.Web.Services.Intelligence;
public sealed class IntelligenceIndex {
 public long VersionId{get;set;} public string SourceSha256{get;set;}="";
 public string ExtractedText{get;set;}="";public string SuggestedKeywords{get;set;}="";
 public string Engine{get;set;}="";public string Status{get;set;}="";public string? Message{get;set;} public DateTime IndexedAt{get;set;}
 public int FormatVersion{get;set;}=2;
}
public sealed class IntelligenceHit {
 public long Id{get;set;} public string Number{get;set;}="";public string Title{get;set;}="";
 public string Text{get;set;}="";public string State{get;set;}="";public double Score{get;set;} public string Snippet{get;set;}="";
}
public sealed record IntelligenceReview(List<AdvisoryFinding> Findings,string? ModelReview,string Engine) {
 public List<AiFinding> AiFindings{get;init;}=[]; public List<RagChunk> References{get;init;}=[];
 public string ModelStatus{get;init;}="DISABLED"; public int CompletedBatches{get;init;} public int TotalBatches{get;init;}
 public int ReviewedCharacters{get;init;} public int TotalCharacters{get;init;} public List<string> Warnings{get;init;}=[];
 public string SourceSha256{get;init;}=""; public DateTime ReviewedAt{get;init;}=DateTime.UtcNow;
}
public sealed class DocumentIntelligenceService(IConfiguration configuration,IConfigService config,IUnitOfWork uow,FileStore store,IDocumentTextExtractor extractor,IHttpClientFactory clients,RagService rag,AiConfigurationService aiSettings,AiProviderClient ai){
 public string SearchMode=>rag.RetrievalMode;
 public NpgsqlConnection Connection()=>new(configuration.GetConnectionString("DMSConnection"));
 const string Columns="version_id AS VersionId,source_sha256 AS SourceSha256,extracted_text AS ExtractedText,suggested_keywords AS SuggestedKeywords,engine,status,message,indexed_at AS IndexedAt,format_version AS FormatVersion";
 public Task<bool> Enabled(string key,CancellationToken ct=default)=>config.GetBoolAsync(key,false,ct);
 public async Task<IntelligenceIndex?> Get(long version,CancellationToken ct){await using var cn=Connection();return await cn.QuerySingleOrDefaultAsync<IntelligenceIndex>(new CommandDefinition($"SELECT {Columns} FROM dms.document_intelligence_index WHERE version_id=@version",new{version},cancellationToken:ct));}
 public async Task<IntelligenceIndex> Index(long version,int actor,string? ip,CancellationToken ct,bool background=false,bool force=false){
  if(!await Enabled("ai.autoIndex.enabled",ct)&&!await Enabled("ai.ocr.enabled",ct))throw new InvalidOperationException("OCR and auto indexing are disabled in System Configuration.");
  var documentId=await uow.Document.GetDocumentIdOfVersionAsync(version)??throw new InvalidOperationException("Revision not found.");
  if(!background){var access=await uow.Document.GetAccessAsync(documentId,actor);if(!access.can_view_content||!access.can_view)throw new UnauthorizedAccessException();}
  var v=(await uow.Document.GetVersionsAsync(documentId)).FirstOrDefault(x=>x.id==version)??throw new InvalidOperationException("Revision not found.");
  if(v.source_storage_path==null||!store.Exists(v.source_storage_path))throw new InvalidOperationException("The revision's file is not present in the configured file store.");
  var actualSha=await store.ComputeSha256Async(v.source_storage_path,ct);
  if(!string.Equals(actualSha,v.source_sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Source fingerprint mismatch; indexing was refused.");
  var existing=await Get(version,ct);if(!force&&existing?.Status=="READY"&&existing.FormatVersion==2&&existing.SourceSha256==actualSha && (!await Enabled("ai.autoIndex.enabled",ct)||!string.IsNullOrWhiteSpace(existing.SuggestedKeywords))){await rag.EnsureVersion(version,actualSha,existing.ExtractedText,false,ct);return existing;}
  var result=new IntelligenceIndex{VersionId=version,SourceSha256=actualSha,Engine="OPENXML",Status="READY",IndexedAt=DateTime.UtcNow};
  try{
   (result.ExtractedText,result.Engine)=await Extract(v.source_storage_path,v.source_file_name??"",v.source_size_bytes??0,ct);
   if(string.IsNullOrWhiteSpace(result.ExtractedText))throw new InvalidOperationException("No text recognized; check image quality, language and file format.");
   if(result.ExtractedText.Length>2_000_000)throw new InvalidOperationException("Extracted text exceeds the processing limit.");
   result.SuggestedKeywords=await Enabled("ai.autoIndex.enabled",ct)?TextIntelligence.Keywords(result.ExtractedText):"";
  }catch(Exception ex) when(ex is not OperationCanceledException && ex is not UnauthorizedAccessException){result.Status="FAILED";result.Message=ex is InvalidOperationException?ex.Message:"Extraction failed; verify the OCR service and source format.";}
  await using var cn=Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  // Lock the source row and recheck its fingerprint: an editor save cannot leave an old index marked current.
  var currentSha=await cn.QuerySingleAsync<string?>(new CommandDefinition("SELECT source_sha256 FROM dms.document_versions WHERE id=@version FOR UPDATE",new{version},tx,cancellationToken:ct));
  if(currentSha!=actualSha)throw new InvalidOperationException("Revision changed during extraction; run indexing again.");
  await cn.ExecuteAsync(new CommandDefinition("INSERT INTO dms.document_intelligence_index(version_id,source_sha256,extracted_text,suggested_keywords,engine,status,message,format_version) VALUES(@VersionId,@SourceSha256,@ExtractedText,@SuggestedKeywords,@Engine,@Status,@Message,@FormatVersion) ON CONFLICT(version_id) DO UPDATE SET source_sha256=excluded.source_sha256,extracted_text=excluded.extracted_text,suggested_keywords=excluded.suggested_keywords,engine=excluded.engine,status=excluded.status,message=excluded.message,format_version=excluded.format_version,indexed_at=now() AT TIME ZONE 'UTC'",result,tx,cancellationToken:ct));
  await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.document_versions SET ocr_text=@text,ocr_status=@status,ocr_engine=@engine,ocr_message=@message,ocr_attempted_at=now() AT TIME ZONE 'UTC' WHERE id=@version",new{text=result.ExtractedText,status=result.Status,engine=result.Engine,message=result.Message,version},tx,cancellationToken:ct));
  int? auditActor=background?null:actor;
  await cn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT dms.sp_audit_write('DOCUMENT',@documentId::bigint,@version::text,'AI_INDEX',@detail::text,@auditActor::integer,NULL::json,NULL::json,NULL::text,@ip::text,@documentId::bigint,@version::bigint)",new{documentId,version,detail=$"{result.Status}; {result.Engine}; source {actualSha}",auditActor,ip},tx,cancellationToken:ct));
  await tx.CommitAsync(ct);if(result.Status=="READY")await rag.EnsureVersion(version,actualSha,result.ExtractedText,true,ct);return result;
 }
 public async Task<(string text,string engine)> Extract(string path,string fileName,long size,CancellationToken ct,bool requireEnabled=true){
  if(Path.GetExtension(fileName).Equals(".docx",StringComparison.OrdinalIgnoreCase)){
   var html=await extractor.ExtractHtmlAsync(path,ct)??throw new InvalidOperationException("No extractable Word text.");
   html=Regex.Replace(html,@"</p>|</tr>|<br\s*/?>","\n",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
   return (System.Net.WebUtility.HtmlDecode(Regex.Replace(html,"<[^>]+>"," ",RegexOptions.None,TimeSpan.FromSeconds(1))),"OPENXML");
  }
  var extension=Path.GetExtension(fileName).ToLowerInvariant();
  if(requireEnabled && extension is not (".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp") && !await Enabled("ai.ocr.enabled",ct))throw new InvalidOperationException("Enable ai.ocr.enabled for PDF and image digitization.");
  if(size>25*1024*1024)throw new InvalidOperationException("OCR input exceeds the 25 MB processing limit.");
  var endpoint=await config.GetStringAsync("ai.ocr.endpoint",null,ct);
  if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||(!uri.IsLoopback&&uri.Scheme!="https"))throw new InvalidOperationException("Configure a private OCR endpoint.");
  await using var stream=await store.OpenCachedReadAsync(path,ct);using var body=new StreamContent(stream);
  using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(uri,"/ocr")){Content=body};
  request.Headers.Add("X-OCR-Key",await config.GetStringAsync("ai.ocr.apiKey",null,ct));
  request.Headers.Add("X-File-Type",extension.TrimStart('.'));
  request.Headers.Add("X-OCR-Languages",await config.GetStringAsync("ai.ocr.languages","eng",ct));
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(await config.GetIntAsync("ai.ocr.timeoutSeconds",120,ct),10,600)));
  try{using var response=await clients.CreateClient("document-intelligence").SendAsync(request,timeout.Token);
   if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"OCR returned HTTP {(int)response.StatusCode}; check format, page limit, language and service credentials.");
   var payload=await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:timeout.Token);
   return(payload.GetProperty("text").GetString()??"",payload.GetProperty("engine").GetString()??"TESSERACT");
  }catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new InvalidOperationException("OCR timed out. Check the document size and OCR service.");}
 }
 public async Task<List<IntelligenceHit>> Search(string query,int actor,CancellationToken ct){
  if(!await Enabled("ai.search.semanticEnabled",ct))throw new InvalidOperationException("Smart Search is disabled in System Configuration.");
  var minRelevance=Math.Clamp(await config.GetDoubleAsync("ai.search.minRelevance",0,ct),0,1);
  var snippetLength=Math.Clamp(await config.GetIntAsync("ai.search.snippetLength",240,ct),80,1000);
  var rankByRelevance=await config.GetBoolAsync("ai.search.rankByRelevance",true,ct);
  await using var cn=Connection();
  var rows=(await cn.QueryAsync<IntelligenceHit>(new CommandDefinition("SELECT d.id AS Id,d.document_number AS Number,d.title AS Title,s.name AS State,coalesce(d.title,'')||' '||coalesce(d.document_number,'')||' '||coalesce(d.keywords,'')||' '||coalesce(d.summary,'')||' '||CASE WHEN a.can_view_content THEN coalesce(i.extracted_text,'')||' '||coalesce(i.suggested_keywords,'') ELSE '' END AS Text FROM dms.documents d JOIN dms.document_states s ON s.id=d.current_state_id CROSS JOIN LATERAL dms.sp_document_access(d.id,@actor::integer) a LEFT JOIN dms.document_versions v ON v.id=d.current_version_id LEFT JOIN dms.document_intelligence_index i ON i.version_id=v.id AND i.source_sha256=v.source_sha256 AND i.status='READY' WHERE a.can_view ORDER BY d.updated_at DESC NULLS LAST,d.id DESC LIMIT 1000",new{actor},cancellationToken:ct))).ToList();
  var semantic=await rag.Retrieve(query,actor,null,true,ct);
  var scores=TextIntelligence.Rank(query,rows.Select(r=>r.Text).ToList());
  for(int i=0;i<rows.Count;i++){rows[i].Score=scores[i];rows[i].Snippet=rows[i].Text[..Math.Min(rows[i].Text.Length,snippetLength)];rows[i].Text="";}
  foreach(var group in semantic.Where(c=>c.DocumentId!=null).GroupBy(c=>c.DocumentId)){
   var row=rows.FirstOrDefault(r=>r.Id==group.Key);if(row==null)continue;var best=group.MaxBy(c=>c.Score)!;
   row.Score=Math.Max(row.Score,best.Score);row.Snippet=best.Text[..Math.Min(best.Text.Length,snippetLength)];
  }
  return rows.Where(r=>r.Score>0&&r.Score>=minRelevance).OrderByDescending(r=>rankByRelevance?r.Score:r.Id).Take(50).ToList();
 }
 public async Task<IntelligenceReview> Review(long version,int actor,string? ip,CancellationToken ct){
  if(!await Enabled("ai.complianceAssistant.enabled",ct))throw new InvalidOperationException("Compliance assistant is disabled in System Configuration.");
  var id=await uow.Document.GetDocumentIdOfVersionAsync(version)??throw new InvalidOperationException("Revision not found.");
  var access=await uow.Document.GetAccessAsync(id,actor);if(!access.can_view_content||!access.can_comment)throw new UnauthorizedAccessException();
  var indexed=await Index(version,actor,ip,ct);if(indexed.Status!="READY")throw new InvalidOperationException(indexed.Message);
  var d=await uow.Document.GetByIdAsync(id);var rules=(await uow.ComplianceRule.GetListAsync(false)).Where(r=>r.is_active&&(string.IsNullOrWhiteSpace(r.applies_to_type_code)||d!.document_type_code?.StartsWith(r.applies_to_type_code,StringComparison.OrdinalIgnoreCase)==true));
  var versionDetails=(await uow.Document.GetVersionsAsync(id)).First(v=>v.id==version);
  var links=await uow.Document.GetLinksAsync(id);
  var findings=TextIntelligence.Review(indexed.ExtractedText,rules.Select(r=>new AdvisoryRule(r.rule_code??r.id.ToString(),r.rule_kind??"PATTERN",r.pattern??"",r.severity,r.title??"Review finding",r.detail??"",r.suggestion)),versionDetails.change_control_number,versionDetails.change_summary,links.Select(l=>l.other_document_number??""));
  var engine="Configured QA rules";var modelStatus="DISABLED";var aiFindings=new List<AiFinding>();var references=new List<RagChunk>();var warnings=new List<string>();var completed=0;var reviewed=0;var totalBatches=0;
  if(await Enabled("ai.review.modelEnabled",ct)){
   var options=await aiSettings.Review(ct);engine+=$" + {options.Provider}/{options.Model}";
   var maximum=await config.GetIntAsync("ai.review.maxCharacters",18000,ct);
   if(options.Provider=="OLLAMA")maximum=Math.Min(maximum,Math.Max(1000,(options.ContextTokens-options.MaxTokens-3000)/8));
   var batches=RagService.Batches(indexed.ExtractedText,maximum);totalBatches=batches.Count;
   var limit=Math.Clamp(await config.GetIntAsync("ai.review.maxBatches",150,ct),1,200);
   foreach(var batch in batches.Take(limit)){
    var liveAccess=await uow.Document.GetAccessAsync(id,actor);if(!liveAccess.can_view||!liveAccess.can_view_content||!liveAccess.can_comment)throw new UnauthorizedAccessException();
    try{
     var sources=await rag.Retrieve(TextIntelligence.Keywords(batch.Text)+"\n"+batch.Text,actor,version,false,ct);
     if(await Enabled("ai.rag.enabled",ct)&&sources.Count==0&&warnings.Count==0)warnings.Add("No matching approved references were retrieved. Findings are advisory and have no regulatory reference grounding.");
     var retrievedCount=sources.Count;
     string Prompt()=>JsonSerializer.Serialize(new{document=new{version,sha=indexed.SourceSha256,title=d!.title,batch=batch.Number,totalBatches,versionDetails.change_control_number,versionDetails.change_summary,text=batch.Text},references=sources.Select(c=>new{id=c.Citation,c.Title,c.Page,c.Section,text=c.Text})});
     var prompt=Prompt();while(options.Provider=="OLLAMA"&&!AiProviderClient.FitsLocalContext(options,prompt)&&sources.Count>0){sources.RemoveAt(sources.Count-1);prompt=Prompt();}
     if(retrievedCount>0&&sources.Count==0&&!warnings.Any(w=>w.Contains("context")))warnings.Add("Retrieved references exceeded the local model context budget for this batch. Its findings have no reference grounding.");
     var response=await ai.Review(options,prompt,ct);
     aiFindings.AddRange(AiProviderClient.ValidateGrounding(response,batch.Text,sources));references.AddRange(sources);completed++;reviewed+=batch.Text.Length;
    }catch(Exception ex)when(ex is InvalidOperationException or HttpRequestException or KeyNotFoundException or JsonException){warnings.Add(ex is InvalidOperationException?ex.Message:"AI service returned an unsupported response.");break;}
   }
   modelStatus=completed==totalBatches?"COMPLETE":completed>0?"PARTIAL":"FAILED";
   if(batches.Count>limit)warnings.Add("Review batch limit reached. Increase ai.review.maxBatches or review smaller documents; coverage is partial.");
  }
  var result=new IntelligenceReview(findings,null,engine){AiFindings=aiFindings.DistinctBy(f=>(f.Finding,f.Section,f.Evidence)).ToList(),References=references.DistinctBy(c=>c.Id).Select(c=>new RagChunk{Id=c.Id,VersionId=c.VersionId,KnowledgeId=c.KnowledgeId,DocumentId=c.DocumentId,Title=c.Title,Sha=c.Sha,Page=c.Page,Section=c.Section,Text=c.Text,Uri=c.Uri}).ToList(),ModelStatus=modelStatus,CompletedBatches=completed,TotalBatches=totalBatches,ReviewedCharacters=reviewed,TotalCharacters=indexed.ExtractedText.Length,Warnings=warnings,SourceSha256=indexed.SourceSha256};await using var cn=Connection();await cn.OpenAsync(ct);await using var tx=await cn.BeginTransactionAsync(ct);
  var finalSha=await cn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT source_sha256 FROM dms.document_versions WHERE id=@version FOR UPDATE",new{version},tx,cancellationToken:ct));
  if(finalSha!=indexed.SourceSha256)throw new InvalidOperationException("Document changed during review. Run review on the updated revision.");
  await cn.ExecuteAsync(new CommandDefinition("INSERT INTO dms.document_intelligence_reviews(version_id,source_sha256,actor_user_id,engine,findings_json) VALUES(@version,@sha,@actor,@engine,@json::jsonb)",new{version,sha=indexed.SourceSha256,actor,engine,json=JsonSerializer.Serialize(result)},tx,cancellationToken:ct));
  await cn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT dms.sp_audit_write('DOCUMENT',@id::bigint,@version::text,'AI_REVIEW',@engine::text,@actor::integer,NULL::json,NULL::json,'Advisory only'::text,@ip::text,@id::bigint,@version::bigint)",new{id,version,engine,actor,ip},tx,cancellationToken:ct));
  await tx.CommitAsync(ct);return result;
 }
 public async Task<IntelligenceReview?> LastReview(long version,int actor,CancellationToken ct){await using var cn=Connection();var json=await cn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT r.findings_json::text FROM dms.document_intelligence_reviews r JOIN dms.document_versions v ON v.id=r.version_id AND v.source_sha256=r.source_sha256 WHERE r.version_id=@version ORDER BY r.id DESC LIMIT 1",new{version},cancellationToken:ct));
  var result=json==null?null:JsonSerializer.Deserialize<IntelligenceReview>(json);if(result==null)return null;
  var allowed=new List<RagChunk>();foreach(var source in result.References){if(source.DocumentId is long id){var access=await uow.Document.GetAccessAsync(id,actor);if(!access.can_view||!access.can_view_content)continue;}
   if(source.KnowledgeId is long knowledge&&!await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM dms.ai_knowledge_sources WHERE id=@knowledge AND is_active AND source_sha256=@sha)",new{knowledge,sha=source.Sha},cancellationToken:ct)))continue;
   allowed.Add(source);}
  if(allowed.Count!=result.References.Count)return result with{ModelReview=null,References=[],AiFindings=[],ModelStatus="WITHHELD",Warnings=["Access to a reference changed after this review. AI findings are withheld; run a new review using currently accessible references."]};
  return result;
 }
}
