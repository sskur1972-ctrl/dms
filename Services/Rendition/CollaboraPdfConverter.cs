using DMS.Web.Services.DocumentEditor;
using DMS.Web.Services.Pdf;
namespace DMS.Web.Services.Rendition;
public sealed class CollaboraPdfConverter(IHttpClientFactory clients,IConfigService config,IConfiguration settings)
{
 private async Task<Uri> Origin(CancellationToken ct){
  var value=await config.GetStringAsync("rendition.collaboraUrl",null,ct)??settings["DocumentEditor:Collabora:BaseUrl"]??"";
  var uri=EditorUrls.Require(value,allowHttp:true);
  if(uri.Scheme=="http"&&!uri.IsLoopback)throw new RenditionException("Use HTTPS for the Collabora converter outside this server.",true);
  if(uri.Query.Length!=0||uri.Fragment.Length!=0)throw new RenditionException("Configure a Collabora conversion origin without query or fragment.",true);
  return new Uri(EditorUrls.Origin(uri));
 }
 public async Task<ConverterStatus> ProbeAsync(CancellationToken ct){
  try{using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(3));var origin=await Origin(deadline.Token);using var response=await clients.CreateClient("editor-health").GetAsync(new Uri(origin,"/hosting/discovery"),deadline.Token);
   return new(response.IsSuccessStatusCode,origin.ToString(),"COLLABORA",response.IsSuccessStatusCode?"Collabora PDF conversion is available.":"Collabora conversion service is unavailable.");}
  catch(Exception)when(!ct.IsCancellationRequested){return new(false,null,"COLLABORA","Check rendition.collaboraUrl and the Collabora service.");}
 }
 public async Task<byte[]> ConvertAsync(Stream source,string fileName,int timeout,CancellationToken ct){
  return await ConvertToAsync(source,fileName,"pdf",timeout,ct);
 }
 public async Task<byte[]> ConvertToAsync(Stream source,string fileName,string format,int timeout,CancellationToken ct){
  if(format is not ("pdf" or "docx"))throw new ArgumentOutOfRangeException(nameof(format));
  var origin=await Origin(ct);using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeout,15,1800)));
  using var body=new MultipartFormDataContent();body.Add(new StreamContent(source),"data",Path.GetFileName(fileName));body.Add(new StringContent(format),"format");
  var client=clients.CreateClient("rendition-conversion");
  using var response=await client.PostAsync(new Uri(origin,$"/cool/convert-to/{format}"),body,deadline.Token);
  if((int)response.StatusCode is 429 or 502 or 503 or 504)throw new RenditionDependencyException("The PDF conversion service is temporarily unavailable.");
  if(!response.IsSuccessStatusCode)throw new RenditionException($"Collabora {format.ToUpperInvariant()} conversion returned HTTP {(int)response.StatusCode}.",false);
  await using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);using var memory=new MemoryStream();
  await BoundedStreamCopy.CopyAsync(stream,memory,100*1024*1024,deadline.Token);
  return memory.ToArray();
 }
}
