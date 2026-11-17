using Dapper;
namespace DMS.Web.Services.Intelligence;
public sealed class IntelligenceWorker(IServiceScopeFactory scopes,ILogger<IntelligenceWorker> logger):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken ct){
  using var timer=new PeriodicTimer(TimeSpan.FromSeconds(45));
  while(await timer.WaitForNextTickAsync(ct)){
   try{using var scope=scopes.CreateScope();var service=scope.ServiceProvider.GetRequiredService<DocumentIntelligenceService>();
    var rag=scope.ServiceProvider.GetRequiredService<RagService>();
    var config=scope.ServiceProvider.GetRequiredService<IConfigService>();var batch=Math.Clamp(await config.GetIntAsync("ai.autoIndex.batchSize",2,ct),1,10);
    await using var cn=service.Connection();var revisions=await service.Enabled("ai.autoIndex.enabled",ct)?await cn.QueryAsync<long>(new CommandDefinition("SELECT v.id FROM dms.document_versions v JOIN dms.documents d ON d.current_version_id=v.id LEFT JOIN dms.document_intelligence_index i ON i.version_id=v.id WHERE v.source_storage_path IS NOT NULL AND v.source_sha256 IS NOT NULL AND (i.version_id IS NULL OR i.source_sha256<>v.source_sha256 OR i.format_version<2 OR i.status='FAILED' AND i.indexed_at<now() AT TIME ZONE 'UTC'-interval '15 minutes' OR i.status='READY' AND NOT EXISTS(SELECT 1 FROM dms.ai_document_chunks c WHERE c.version_id=v.id AND c.source_sha256=v.source_sha256)) ORDER BY v.updated_at DESC NULLS LAST LIMIT @batch",new{batch},cancellationToken:ct)):[];
    foreach(var version in revisions){try{await service.Index(version,0,null,ct,true);}catch(Exception ex) when(ex is not OperationCanceledException){logger.LogWarning(ex,"Auto indexing revision {Version} failed: {Type}",version,ex.GetType().Name);}}
    await rag.BackfillEmbeddings(ct);
   }catch(Exception ex) when(ex is not OperationCanceledException){logger.LogWarning(ex,"Intelligence worker unavailable: {Type}",ex.GetType().Name);}
  }
 }
}
