using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DMS.Web.Services.Pdf;

namespace DMS.Web.Services.Rendition
{
    public sealed class EuroOfficePdfConverter
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _appConfig;
        private static readonly ConcurrentDictionary<string, byte[]> _pendingFiles = new();

        public EuroOfficePdfConverter(IHttpClientFactory httpFactory, IConfiguration appConfig)
        {
            _httpFactory = httpFactory;
            _appConfig = appConfig;
        }

        public static bool TryTakePendingFile(string token, out byte[] bytes)
            => _pendingFiles.TryRemove(token, out bytes!);

        private string BaseUrl => (_appConfig["DocumentEditor:OnlyOffice:BaseUrl"] ?? "http://127.0.0.1:8080").TrimEnd('/');
        private string JwtSecret => _appConfig["DocumentEditor:OnlyOffice:JwtSecret"] ?? "my_long_jwt_secret_at_least_32_chars";
        private string DmsInternalUrl => (_appConfig["DocumentEditor:PublicBaseUrl"] ?? "http://host.docker.internal:5194").TrimEnd('/');

        public async Task<ConverterStatus> ProbeAsync(CancellationToken ct)
        {
            try
            {
                var client = _httpFactory.CreateClient("editor-health");
                using var resp = await client.GetAsync($"{BaseUrl}/healthcheck", ct);
                if (!resp.IsSuccessStatusCode)
                    return new(false, null, "EUROOFFICE", $"Check DocumentEditor:OnlyOffice:BaseUrl ({BaseUrl}/healthcheck returned {(int)resp.StatusCode}).");

                return new(true, BaseUrl, "EUROOFFICE", "Euro-Office Document Server is available.");
            }
            catch (Exception ex)
            {
                return new(false, null, "EUROOFFICE", $"Check DocumentEditor:OnlyOffice:BaseUrl and the Euro-Office service: {ex.Message}");
            }
        }

        public Task<byte[]> ConvertToDocxAsync(Stream source, string fileName, int timeoutSeconds = 60, CancellationToken ct = default)
            => ConvertInternalAsync(source, fileName, "docx", timeoutSeconds, ct);

        public Task<byte[]> ConvertAsync(Stream source, string fileName, int timeoutSeconds, CancellationToken ct)
            => ConvertInternalAsync(source, fileName, "pdf", timeoutSeconds, ct);

        private async Task<byte[]> ConvertInternalAsync(Stream source, string fileName, string outputType, int timeoutSeconds, CancellationToken ct)
        {
            await using var ms = new MemoryStream();
            await source.CopyToAsync(ms, ct);
            var fileToken = Guid.NewGuid().ToString("N");
            _pendingFiles[fileToken] = ms.ToArray();

            try
            {
                var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(ext)) ext = "docx";

                var payload = new Dictionary<string, object>
                {
                    ["async"] = false,
                    ["filetype"] = ext,
                    ["key"] = fileToken,
                    ["outputtype"] = outputType,
                    ["title"] = fileName,
                    ["url"] = $"{DmsInternalUrl}/api/internal/rendition-source/{fileToken}"
                };

                if (!string.IsNullOrWhiteSpace(JwtSecret))
                    payload["token"] = CreateJwt(payload, JwtSecret);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/ConvertService.ashx")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(JwtSecret))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt(new Dictionary<string, object> { ["payload"] = payload }, JwtSecret));

                using var resp = await client.SendAsync(req, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                resp.EnsureSuccessStatusCode();

                string? convertedUrl = null;
                if (body.TrimStart().StartsWith("<"))
                {
                    var doc = XDocument.Parse(body);
                    var err = doc.Root?.Element("Error")?.Value;
                    if (!string.IsNullOrEmpty(err))
                        throw new RenditionException($"Euro-Office conversion error code: {err}", permanent: false);
                    convertedUrl = doc.Root?.Element("FileUrl")?.Value;
                }
                else
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out var errEl))
                        throw new RenditionException($"Euro-Office conversion error code: {errEl}", permanent: false);
                    if (doc.RootElement.TryGetProperty("fileUrl", out var urlEl))
                        convertedUrl = urlEl.GetString();
                }

                if (string.IsNullOrWhiteSpace(convertedUrl))
                    throw new RenditionException("Euro-Office did not return a converted fileUrl.", permanent: false);

                return await client.GetByteArrayAsync(convertedUrl, cts.Token);
            }
            finally
            {
                _pendingFiles.TryRemove(fileToken, out _);
            }
        }

        private static string CreateJwt(object payload, string secret)
        {
            static string Base64Url(byte[] data) =>
                Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            var body = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            var unsigned = $"{header}.{body}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var sig = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(unsigned)));
            return $"{unsigned}.{sig}";
        }
    }
}
