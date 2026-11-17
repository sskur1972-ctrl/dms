using Microsoft.Extensions.Options;
namespace DMS.Web.Services.DocumentEditor;
public sealed class EditorDownloadService(IHttpClientFactory clients,IOptions<EditorOptions> options){
 public static Uri Validate(string value,ProviderOptions provider,bool allowHttp){
  var uri=EditorUrls.Require(value,allowHttp);
  var origins=provider.AllowedDownloadOrigins.Append(provider.BaseUrl).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>EditorUrls.Origin(EditorUrls.Require(x,allowHttp)));
  if(uri.Fragment.Length>0||!origins.Contains(EditorUrls.Origin(uri),StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Editor download origin is not allowed.");return uri;
 }
 public async Task<HttpResponseMessage> OpenAsync(string provider,string value,CancellationToken ct){
  var uri=Validate(value,options.Value.For(provider),options.Value.AllowHttpForDevelopment);
  var response=await clients.CreateClient("editor-download").GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);
  try{response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>(long)Math.Clamp(options.Value.MaxFileMb,1,200)*1024*1024)throw new StoredFileTooLargeException((long)options.Value.MaxFileMb*1024*1024);return response;}catch{response.Dispose();throw;}
 }
}
