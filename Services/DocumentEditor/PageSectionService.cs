using Dapper;
using Npgsql;
using Microsoft.Extensions.Caching.Distributed;
using DMS.Web.Services.Intelligence;
namespace DMS.Web.Services.DocumentEditor;
public sealed class PageSectionService(IConfiguration settings,FileStore files,DocumentIntelligenceService text,IDistributedCache cache)
{
 public async Task<string?> Find(long version,int page,CancellationToken ct){
  var options=await FindOptions(version,page,ct);return options.FirstOrDefault();
 }
 public async Task<IReadOnlyList<string>> FindOptions(long version,int page,CancellationToken ct){
  await using var cn=new NpgsqlConnection(settings.GetConnectionString("DMSConnection"));
  var v=await cn.QuerySingleOrDefaultAsync<PageSource>(new CommandDefinition("SELECT source_storage_path AS SourcePath,source_sha256 AS SourceSha,rendition_base_path AS PdfPath,rendition_base_sha256 AS PdfSha,rendition_source_sha256 AS RenditionSourceSha FROM dms.document_versions WHERE id=@version",new{version},cancellationToken:ct));
  if(v?.SourcePath==null||v.PdfPath==null||!string.Equals(v.SourceSha?.Trim(),v.RenditionSourceSha?.Trim(),StringComparison.OrdinalIgnoreCase))return Array.Empty<string>();
  var key="page-sections:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cn.Database+"|"+files.Root+"|"+v.PdfSha)));
  string? extracted=null;try{extracted=await cache.GetStringAsync(key,ct);}catch{}
  if(extracted==null){
   using var pdf=await files.OpenCachedReadAsync(v.PdfPath,ct);if(pdf.Length>25*1024*1024)return Array.Empty<string>();
   // Native PDF extraction uses the existing private OCR service; no LLM call is made.
   extracted=(await text.Extract(v.PdfPath,"source.pdf",pdf.Length,ct,requireEnabled:false)).text;
   try{await cache.SetStringAsync(key,extracted,new DistributedCacheEntryOptions{AbsoluteExpirationRelativeToNow=TimeSpan.FromHours(1)},ct);}catch{}
  }
  using var source=await files.OpenCachedReadAsync(v.SourcePath,ct);return DocumentSectionLocator.FindPageSections(source,extracted,page);
 }
 private sealed class PageSource {public string? SourcePath{get;set;}public string? SourceSha{get;set;}public string? PdfPath{get;set;}public string? PdfSha{get;set;}public string? RenditionSourceSha{get;set;}}
}
