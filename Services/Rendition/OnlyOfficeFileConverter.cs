using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DMS.Web.Services.DocumentEditor;
using Microsoft.Extensions.Options;

namespace DMS.Web.Services.Rendition;

/// <summary>Uses ONLYOFFICE Docs' local from-file conversion API for uploaded files.</summary>
public sealed class OnlyOfficeFileConverter(IHttpClientFactory clients, IOptions<EditorOptions> options)
{
    public bool IsConfigured => options.Value.OnlyOffice.Enabled && !string.IsNullOrWhiteSpace(options.Value.OnlyOffice.BaseUrl);

    public async Task<byte[]> PdfToWordAsync(Stream pdf, string fileName, int timeoutSeconds, CancellationToken ct)
    {
        var settings = options.Value;
        var provider = settings.OnlyOffice;
        if (!provider.Enabled) throw new InvalidOperationException("ONLYOFFICE is disabled in the editor configuration.");
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new InvalidOperationException("Configure the ONLYOFFICE document server URL first.");
        var origin = EditorUrls.Require(provider.BaseUrl, settings.AllowHttpForDevelopment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 15, 1800)));
        using var form = new MultipartFormDataContent();
        var parameters = new { filetype = "pdf", outputtype = "docx", title = Path.GetFileNameWithoutExtension(fileName) + ".docx", operation = "converter" };
        if (!string.IsNullOrWhiteSpace(provider.JwtSecret))
            form.Add(new StringContent(EditorJwt.Sign(parameters, provider.JwtSecret)), "token");
        else
            form.Add(new StringContent(JsonSerializer.Serialize(parameters), Encoding.UTF8, "application/json"), "params");
        var content = new StreamContent(pdf);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(content, "file", Path.GetFileName(fileName));
        using var response = await clients.CreateClient("rendition-conversion").PostAsync(
            new Uri(new Uri(EditorUrls.Origin(origin) + "/"), "converter/from-file"), form, deadline.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ONLYOFFICE conversion returned HTTP {(int)response.StatusCode}. Check converter availability and JWT settings.");
        await using var result = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        await DMS.Web.Services.BoundedStreamCopy.CopyAsync(result, output, 100 * 1024 * 1024, deadline.Token);
        return output.ToArray();
    }
}
