using Dapper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Distributed;
namespace DMS.Web.Services.DocumentEditor;
public sealed record EditorHealthRow(string Check,string Status,string Detail);
public sealed class EditorHealthService(EditorSessionStore sessions,IOptions<EditorOptions> options,IHttpClientFactory clients,IDistributedCache cache){
 public async Task<List<EditorHealthRow>> RunAsync(CancellationToken ct){
  var rows=new List<EditorHealthRow>();var o=options.Value;
  rows.Add(new("Active provider",o.Enabled?"CONFIGURED":"DISABLED",o.Provider));
  try{var uri=EditorUrls.Require(o.PublicBaseUrl,o.AllowHttpForDevelopment);rows.Add(new("Editor → DMS callback URL",uri.IsLoopback?"WARNING":"MANUAL",uri.IsLoopback?"Loopback points to the editor container itself. Set a DMS address reachable from it.":"Verify from the editor container: "+uri+"api/editor/ping"));}catch(Exception ex){rows.Add(new("DMS callback URL","FAIL",ex.Message));}
  try{await using var cn=sessions.Connection();await cn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::integer FROM dms.editor_sessions",cancellationToken:ct));rows.Add(new("Editor database migration","PASS","Editor session table is accessible."));}catch{rows.Add(new("Editor database migration","FAIL","Run web/Database/20_editor_integration.sql in the configured DMS database; check database connectivity."));}
  var key="editor:health:"+Guid.NewGuid().ToString("N");try{await cache.SetStringAsync(key,"ok",new DistributedCacheEntryOptions{AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(10)},ct);if(await cache.GetStringAsync(key,ct)!="ok")throw new Exception();await cache.RemoveAsync(key,ct);rows.Add(new("Redis","PASS","Small metadata round trip succeeded."));}catch{rows.Add(new("Redis","FAIL","Check existing Redis connection and authentication."));}
  foreach(var name in new[]{"COLLABORA","ONLYOFFICE","SPIRE"}){
   var p=o.For(name);if(!p.Enabled){rows.Add(new(name,"DISABLED","Enable this provider only after setting its URL and required credentials."));continue;}
   try{
    var url=name=="COLLABORA"?p.DiscoveryUrl:p.BaseUrl.TrimEnd('/')+(name=="ONLYOFFICE"?"/web-apps/apps/api/documents/api.js":"/web/editors/spireapi/SpireCloudEditor.js");
    EditorUrls.Require(url,o.AllowHttpForDevelopment);using var response=await clients.CreateClient("editor-health").GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
    if(name=="ONLYOFFICE")EditorJwt.Sign(new{},p.JwtSecret);
    rows.Add(new(name,"PASS","Discovery/script endpoint is reachable from DMS. Browser and callback connectivity still require testing."));
   }catch(Exception ex){rows.Add(new(name,"FAIL",ex is InvalidOperationException?ex.Message:"Endpoint unreachable or certificate/HTTP failure. Check server logs and configured URL."));}
  }
  rows.Add(new("Capacity and vendor APIs","MANUAL","Verify the installed build/license, browser document-ready event, save/reopen and the 200-user qualification matrix. A reachable script is not an end-to-end test."));return rows;
 }
}
