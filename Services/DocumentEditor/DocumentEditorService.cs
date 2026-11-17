namespace DMS.Web.Services.DocumentEditor;

public sealed class EditorProviderResolver(IConfigService config, IConfiguration settings,
    IEnumerable<IDocumentEditorProvider> providers) : IEditorProviderResolver
{
    public async Task<(IDocumentEditorProvider? Provider, string? Error)> ResolveAsync(CancellationToken ct = default)
    {
        var enabled = await config.GetBoolAsync(ConfigKeys.EditorEnabled,
            settings.GetValue("DocumentEditor:Enabled", true), ct);
        var name = (await config.GetStringAsync(ConfigKeys.EditorProvider,
            settings["DocumentEditor:Provider"] ?? "COLLABORA", ct))?.ToUpperInvariant();
        if (!enabled || name == "DISABLED") return (null, "The document editor is disabled.");
        var provider = providers.SingleOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
            return (null, $"Editor provider '{name}' is not registered. Configure COLLABORA, ONLYOFFICE or SPIRE.");
        if (!await provider.IsEnabledAsync(ct)) return (null, $"{name} is disabled. For Collabora, enable office.wopi.enabled after configuring the existing WOPI settings.");
        return (provider, null);
    }
}
public sealed class DocumentEditorService(IEditorProviderResolver resolver, ILogger<DocumentEditorService> logger) : IDocumentEditorService
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => (await resolver.ResolveAsync(ct)).Provider != null;
    public async Task<EditorOpenResult> OpenAsync(EditorOpenContext context, CancellationToken ct = default)
    {
        var (provider, error) = await resolver.ResolveAsync(ct);
        try { return provider == null ? new(null, error) : await provider.OpenAsync(context, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogError(ex,"Editor launch failed for version {Version}",context.VersionId);
            return new(null, ex is InvalidOperationException ? ex.Message : "Editor launch failed. Check Administration / Office Editor diagnostics, migration 20 and server logs.");
        }
    }
}
