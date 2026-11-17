using DMS.Model.Documents;

namespace DMS.Web.Services.DocumentEditor;

public enum EditorMode { View, Edit, Review, Comment }
public sealed record EditorCapabilities(bool CanView, bool CanEdit,
    bool SupportsNativeComments, bool SupportsTrackChanges, bool CanNavigateToAnchor);
public sealed record EditorOpenContext(long VersionId, int UserId, bool HoldsEditPermission,
    string? ClientIp, EditorMode Mode, string? BrowserOrigin = null, long? TemplateId = null);
// Transport-neutral launch data. WOPI tokens never appear in URLs or cached metadata.
public sealed class EditorSession
{
    public string SessionId {get;init;}="";
    public DateTime ExpiresAt {get;init;}
    public string ScriptUrl {get;init;}="";
    public object? ClientConfig {get;init;}
    public string Provider { get; init; } = "";
    public string Transport { get; init; } = "";
    public long DocumentId { get; init; }
    public long VersionId { get; init; }
    public string DocumentNumber { get; init; } = "";
    public string Title { get; init; } = "";
    public string DisplayRevision { get; init; } = "";
    public string LaunchUrl { get; init; } = "";
    public Dictionary<string, string> PostFields { get; init; } = new();
    public bool ReadOnly { get; init; }
    public EditorMode Mode { get; init; }
    public EditorCapabilities Capabilities { get; init; } = new(false, false, false, false, false);
}
public sealed record EditorOpenResult(EditorSession? Session, string? Error);
public sealed class EditorWorkspaceViewModel
{
    public EditorSession Session { get; init; } = new();
    public DocumentDetailsViewModel Review { get; init; } = new();
    public bool CanEditDocument { get; init; }
    public EditorLocationAnchor? InitialLocation { get; init; }
    public string? Watermark { get; init; }
}
public sealed record EditorLocationAnchor(int? page, string? section, string? type, string? value, string? text);
public interface IDocumentEditorProvider
{
    string Name { get; }
    EditorCapabilities Capabilities { get; }
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
    Task<EditorOpenResult> OpenAsync(EditorOpenContext context, CancellationToken ct = default);
}
public interface IEditorProviderResolver
{
    Task<(IDocumentEditorProvider? Provider, string? Error)> ResolveAsync(CancellationToken ct = default);
}
public interface IDocumentEditorService
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<EditorOpenResult> OpenAsync(EditorOpenContext context, CancellationToken ct = default);
}
