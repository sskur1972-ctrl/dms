using System.Globalization;
using DMS.Web.Services.Office;
namespace DMS.Web.Services.DocumentEditor;

public sealed class CollaboraEditorProvider(WopiService wopi, EditorSessionStore sessions,TemplateEditorService templates) : IDocumentEditorProvider
{
    public string Name => "COLLABORA";
    // CODE supports live page navigation; workflow findings remain in the DMS panel.
    public EditorCapabilities Capabilities => new(true, true, false, false, true);
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => wopi.IsEnabledAsync();
    public async Task<EditorOpenResult> OpenAsync(EditorOpenContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var record = await sessions.CreateAsync(context, Name, ct);
        try {
        if(context.TemplateId!=null)return new(await templates.WopiLaunchAsync(record,context.BrowserOrigin,sessions.FileToken(record),ct),null);
        var (s, error) = await wopi.OpenAsync(context.VersionId, context.UserId,
            context.HoldsEditPermission, context.ClientIp, context.Mode != EditorMode.Edit, record.Id, context.BrowserOrigin);
        if (s == null) { await sessions.AbandonAsync(record, ct); return new(null, error); }
        if (!Uri.TryCreate(s.EditorUrl, UriKind.Absolute, out var launch)
            || (launch.Scheme != Uri.UriSchemeHttps && launch.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(launch.UserInfo))
            return new(null, "The configured editor returned an invalid launch URL.");
        return new(new EditorSession
        {
            SessionId=record.Id, ExpiresAt=record.ExpiresAt, Provider = Name, Transport = "FORM_POST", DocumentId = s.DocumentId,
            VersionId = s.DocumentVersionId, DocumentNumber = s.DocumentNumber,
            Title = s.Title, DisplayRevision = s.DisplayRevision, LaunchUrl = s.EditorUrl,
            PostFields = new() { ["access_token"] = s.AccessToken,
                ["access_token_ttl"] = s.AccessTokenTtl.ToString(CultureInfo.InvariantCulture) },
            ReadOnly = s.ReadOnly, Mode = context.Mode, Capabilities = Capabilities
        }, null);
        } catch { await sessions.AbandonAsync(record, CancellationToken.None); throw; }
    }
}
