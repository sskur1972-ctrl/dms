//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Office editor host (WOPI) - ported from Bcl.Dms WopiService.
 *
 * The editor (Collabora Online on your own network) is sent the revision's file through this application's
 * /wopi endpoints and sends it back. Every call carries a short-lived, signed access token bound to one revision
 * and one user; every save re-checks that the revision is still editable by that user, records the file's
 * fingerprint and a paragraph-level before/after in the audit trail, and resets the in-application body.
 * Download, export and print from inside the editor are switched off unless the access matrix allows them.
 */
using System.Text.Json;
using DMS.Web.Services.DocumentEditor;
using Microsoft.Extensions.Options;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Web.Services.Authoring;

namespace DMS.Web.Services.Office;

public sealed partial class WopiService
{
    public const string EditInOfficePermission = "Edit in the Office editor";   // document.editInOffice
    private const int MaxLockLength = 1024;

    private static readonly HashSet<string> EditableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp" };

    private readonly EditorSessionStore _sessions;
    private readonly EditorFileCommitService _commits;
    private readonly EditorOptions _options;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IConfigService _config;
    private readonly FileStore _fileStore;
    private readonly IWopiTokenProtector _tokens;
    private readonly IWopiDiscoveryClient _discovery;
    private readonly IDocumentTextExtractor _textExtractor;
    private readonly ILogger<WopiService> _logger;

    public WopiService(IUnitOfWork unitOfWork, IConfigService config, FileStore fileStore, IWopiTokenProtector tokens,
        IWopiDiscoveryClient discovery, IDocumentTextExtractor textExtractor, ILogger<WopiService> logger, EditorSessionStore sessions, EditorFileCommitService commits, IOptions<EditorOptions> options)
    {
        _sessions=sessions; _commits=commits; _options=options.Value;
        _unitOfWork = unitOfWork;
        _config = config;
        _fileStore = fileStore;
        _tokens = tokens;
        _discovery = discovery;
        _textExtractor = textExtractor;
        _logger = logger;
    }

    public static bool IsEditableFile(string? fileName) => EditableExtensions.Contains(Path.GetExtension(fileName ?? ""));

    public Task<bool> IsEnabledAsync() => _config.GetBoolAsync(ConfigKeys.WopiEnabled, false);

    // ------------------------------------------------------------------ opening the editor page

    public async Task<(WopiSessionDto? Session, string? Error)> OpenAsync(long versionId, int userId, bool holdsPermission, string? ip, bool forceReadOnly = false, string? sessionId = null, string? browserOrigin = null)
    {
        if (!await IsEnabledAsync())
            return (null, "Editing in the Office editor is switched off. An administrator turns it on in Administration → " +
                          "System Configuration (office.wopi.enabled) once an editor has been installed and qualified.");
        if (!holdsPermission && !forceReadOnly)
            return (null, "Your roles do not permit editing documents in the Office editor.");

        var (document, version) = await LoadAsync(versionId);
        if (document == null || version == null) return (null, "That revision does not exist.");
        if (string.IsNullOrWhiteSpace(version.source_storage_path) || string.IsNullOrWhiteSpace(version.source_file_name))
            return (null, "This revision has no file to edit yet. Write it in the application editor first, or upload one.");

        var extension = Path.GetExtension(version.source_file_name);
        if (!EditableExtensions.Contains(extension))
            return (null, $"{extension} files cannot be opened in the Office editor. Word, Excel, PowerPoint and OpenDocument formats can.");

        var access = await _unitOfWork.Document.GetAccessAsync(document.id, userId);
        if (!access.can_view || !access.can_view_content) return (null, "You may not read this document.");
        var canWrite = access.can_author && holdsPermission && !forceReadOnly;

        var url = await _discovery.GetActionUrlAsync(extension, canWrite ? "edit" : "view");
        if (string.IsNullOrWhiteSpace(url))
            return (null, "The Office editor did not answer, or does not offer that action for this file type. " +
                          "Administration → Office Editor shows the editor's address and whether it can be reached.");

        var publicBase = (await _config.GetStringAsync(ConfigKeys.WopiPublicBaseUrl, null))?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(publicBase))
            return (null, "The editor needs the address of this application as it can reach it (office.wopi.publicBaseUrl). " +
                          "It calls back to us, so it cannot be guessed from your browser.");

        var lifetime = Math.Clamp(await _config.GetIntAsync(ConfigKeys.WopiTokenLifetimeMinutes, 600), 5, 24 * 60);
        var token = new WopiTokenDto
        {
            DocumentVersionId = versionId,
            UserId = userId,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(lifetime),
            Origin = sessionId ?? Guid.NewGuid().ToString("N"),
            BrowserOrigin = browserOrigin,
            Permissions = new WopiPermissionsDto
            {
                CanWrite = canWrite,
                CanPrint = canWrite && access.can_print,
                CanExport = canWrite && access.can_export,
                ApplyWatermark = access.apply_screen_watermark
            }
        };

        EditorUrls.Require(publicBase, _options.AllowHttpForDevelopment);
        if (!string.IsNullOrWhiteSpace(_options.Collabora.BaseUrl))
            url = EditorUrls.Origin(EditorUrls.Require(_options.Collabora.BaseUrl, _options.AllowHttpForDevelopment)) + new Uri(url).PathAndQuery;
        var source = $"{publicBase}/wopi/files/{versionId}";
        var editorUrl = url.Contains("WOPISrc=", StringComparison.OrdinalIgnoreCase) ? url : AppendQuery(url, "WOPISrc=" + Uri.EscapeDataString(source));
        var editorName = await _config.GetStringAsync(ConfigKeys.WopiEditor, "COLLABORA") ?? "COLLABORA";

        await _unitOfWork.Authoring.RecordVersionEventAsync(versionId, "OFFICE_EDIT_OPENED",
            canWrite ? $"Opened for editing in the {editorName} Office editor." : $"Opened read-only in the {editorName} Office editor.",
            JsonSerializer.Serialize(new { Editor = editorName, version.source_file_name, CanWrite = canWrite, TokenExpiresUtc = token.ExpiresAtUtc }),
            userId, ip);

        return (new WopiSessionDto
        {
            DocumentId = document.id,
            DocumentVersionId = versionId,
            DocumentNumber = document.document_number ?? "",
            Title = document.title ?? "",
            DisplayRevision = version.display_revision ?? "",
            FileName = version.source_file_name!,
            EditorUrl = editorUrl,
            AccessToken = _tokens.Protect(token),
            AccessTokenTtl = new DateTimeOffset(token.ExpiresAtUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            ReadOnly = !canWrite,
            EditorName = editorName
        }, null);
    }

    // ------------------------------------------------------------------ WOPI protocol (called by the editor)

    public WopiTokenDto? ValidateToken(long versionId, string? accessToken)
    {
        var token = _tokens.Unprotect(accessToken);
        return token != null && token.AttachmentId == null && token.DocumentVersionId == versionId ? token : null;
    }

    public async Task<(WopiCheckFileInfoDto? Info, int Status)> CheckFileInfoAsync(long versionId, string? accessToken)
    {
        var token = ValidateToken(versionId, accessToken);
        if (token == null) return (null, 401);
        var (document, version) = await LoadAsync(versionId);
        if (document == null || version == null || string.IsNullOrWhiteSpace(version.source_storage_path)) return (null, 404);

        var access=await _unitOfWork.Document.GetAccessAsync(document.id,(int)token.UserId);
        if(!access.can_view||!access.can_view_content)return (null,403);
        var user = (await _unitOfWork.UserService.GetUsersById((int)token.UserId)).FirstOrDefault();
        var publicBase = (await _config.GetStringAsync(ConfigKeys.WopiPublicBaseUrl, null))?.TrimEnd('/') ?? string.Empty;
        var writable = token.Permissions.CanWrite && await StillEditableAsync(document.id, (int)token.UserId);
        var userName = user?.DisplayName ?? user?.username ?? "DMS user";

        return (new WopiCheckFileInfoDto
        {
            BaseFileName = version.source_file_name ?? $"document-{versionId}.docx",
            Size = version.source_size_bytes ?? 0,
            Version = string.IsNullOrWhiteSpace(version.source_sha256) ? version.row_version.ToString() : version.source_sha256!.Trim(),
            UserId = token.UserId.ToString(),
            UserFriendlyName = userName,
            OwnerId = document.owner_user_id.ToString(),
            PostMessageOrigin = token.BrowserOrigin ?? (publicBase.Length > 0 ? new Uri(publicBase).GetLeftPart(UriPartial.Authority) : null),
            LastModifiedTime = version.created_at.ToUniversalTime().ToString("O"),
            UserCanWrite = writable,
            ReadOnly = !writable,
            SupportsUpdate = writable,
            SupportsLocks = true,
            SupportsGetLock = true,
            SupportsRename = false,
            UserCanRename = false,
            UserCanNotWriteRelative = true,
            SupportsRestoreVersion = false,
            SupportsUserInfo = false,
            DisablePrint = !writable || !token.Permissions.CanPrint,
            HidePrintOption = !writable || !token.Permissions.CanPrint,
            DisableExport = !writable || !token.Permissions.CanExport,
            HideExportOption = !writable || !token.Permissions.CanExport,
            HideSaveOption = !writable,
            DisableCopy = !token.Permissions.CanExport,
            WatermarkText = null, // DMS draws one centered watermark above the editor.
            BreadcrumbDocName = $"{document.document_number} rev {version.display_revision}",
            BreadcrumbFolderName = document.document_type_name ?? "Documents",
            CloseUrl = token.BrowserOrigin != null ? $"{token.BrowserOrigin}/Documents/Details/{document.id}" : null,
            HostEditUrl = token.BrowserOrigin != null ? $"{token.BrowserOrigin}/Documents/EditInOffice?versionId={versionId}" : null
        }, 200);
    }

    public async Task<(Stream? Content, int Status)> GetFileAsync(long versionId, string? accessToken)
    {
        var token = ValidateToken(versionId, accessToken);
        if (token == null) return (null, 401);
        var (document, version) = await LoadAsync(versionId);
        if (document == null) return (null,404);
        var access = await _unitOfWork.Document.GetAccessAsync(document.id,(int)token.UserId);
        if (!access.can_view || !access.can_view_content) return (null,403);
        if (version?.source_storage_path == null) return (null, 404);
        try
        {
            return (await _fileStore.OpenCachedReadAsync(version.source_storage_path), 200);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WOPI GetFile could not read {Path} for version {VersionId}", version.source_storage_path, versionId);
            return (null, 500);
        }
    }

    public async Task<WopiSaveResultDto> PutFileAsync(long versionId, string? accessToken, string? lockValue, Stream content, string? ip, CancellationToken ct = default)
    {
        var token = ValidateToken(versionId, accessToken);
        if (token == null) return Refuse(WopiFailure.Unauthorized, "Invalid token.");
        if (!token.Permissions.CanWrite) return Refuse(WopiFailure.Unauthorized, "This session is read-only.");

        var (document, version) = await LoadAsync(versionId);
        if (document == null || version == null) return Refuse(WopiFailure.NotFound, "No such revision.");
        var userId = (int)token.UserId;
        if (!await StillEditableAsync(document.id, userId))
            return Refuse(WopiFailure.Conflict, $"{document.document_number} is in state {document.state_code} and can no longer be edited. " +
                                                "Your changes have not been saved.");

        var held = await _unitOfWork.Authoring.GetLockAsync(versionId);
        if (held == null || held.HasExpired(DateTime.UtcNow) || !string.Equals(held.lock_value, lockValue, StringComparison.Ordinal))
            return new WopiSaveResultDto { Saved = false, Failure = WopiFailure.Conflict, CurrentLock = held?.lock_value ?? "",
                                           Message = "A current matching WOPI lock is required." };

        var session = await _sessions.GetAsync(token.Origin ?? "", ct);
        if (session == null || session.Provider != "COLLABORA") return Refuse(WopiFailure.Unauthorized,"Editor session expired.");
        try {
            var result = await _commits.CommitAsync(session,content,EditorFileCommitService.EventId(session,Guid.NewGuid().ToString("N")),ip,ct);
            return result.Saved ? new WopiSaveResultDto {Saved=true,Version=result.Sha,Sha256=result.Sha,SizeBytes=result.Size} : Refuse(WopiFailure.Conflict,result.Message);
        } catch(StoredFileTooLargeException) { return Refuse(WopiFailure.TooLarge,"Editor file exceeds configured limit."); }
    }

    public async Task<WopiLockResultDto> LockAsync(long versionId, string? accessToken, string lockValue, string? ip)
    {
        var token = ValidateToken(versionId, accessToken);
        if (token == null || !token.Permissions.CanWrite) return new WopiLockResultDto { Granted = false };
        if (lockValue.Length > MaxLockLength)
            return new WopiLockResultDto { Granted = false, Message = $"The lock value exceeds the {MaxLockLength} characters this host supports." };

        var minutes = Math.Clamp(await _config.GetIntAsync(ConfigKeys.WopiLockMinutes, 30), 1, 24 * 60);
        var editorName = await _config.GetStringAsync(ConfigKeys.WopiEditor, "COLLABORA");
        var expires = DateTime.UtcNow.AddMinutes(minutes);
        if (await _unitOfWork.Authoring.AcquireLockAsync(versionId, lockValue, (int)token.UserId, expires, editorName))
        {
            await _unitOfWork.Authoring.RecordVersionEventAsync(versionId, "OFFICE_EDIT_LOCKED",
                $"Edit lock taken by the {editorName} editor until {expires:u}.", null, (int)token.UserId, ip);
            return new WopiLockResultDto { Granted = true, ExpiresAtUtc = expires };
        }
        var held = await _unitOfWork.Authoring.GetLockAsync(versionId);
        return new WopiLockResultDto { Granted = false, CurrentLock = held?.lock_value, HeldByUserName = held?.locked_by_name, ExpiresAtUtc = held?.expires_at_utc };
    }

    public async Task<WopiLockResultDto> RefreshLockAsync(long versionId, string? accessToken, string lockValue)
    {
        var token = ValidateToken(versionId, accessToken);
        if (token == null) return new WopiLockResultDto { Granted = false };
        var expires = DateTime.UtcNow.AddMinutes(Math.Clamp(await _config.GetIntAsync(ConfigKeys.WopiLockMinutes, 30), 1, 24 * 60));
        if (await _unitOfWork.Authoring.RefreshLockAsync(versionId, lockValue, expires))
            return new WopiLockResultDto { Granted = true, ExpiresAtUtc = expires };
        var held = await _unitOfWork.Authoring.GetLockAsync(versionId);
        return new WopiLockResultDto { Granted = false, CurrentLock = held?.lock_value, HeldByUserName = held?.locked_by_name };
    }

    public async Task<WopiLockResultDto> UnlockAsync(long versionId, string? accessToken, string lockValue)
    {
        var token = ValidateToken(versionId, accessToken);
        if (token == null) return new WopiLockResultDto { Granted = false };
        var session=await _sessions.GetAsync(token.Origin??"",CancellationToken.None);
        var released=session?.Provider=="COLLABORA"
            ? await _sessions.UnlockWopiGroupAsync(session,lockValue,CancellationToken.None)
            : await _unitOfWork.Authoring.ReleaseLockAsync(versionId,lockValue);
        if (released) {
            return new WopiLockResultDto { Granted = true };
        }
        var held = await _unitOfWork.Authoring.GetLockAsync(versionId);
        return new WopiLockResultDto { Granted = false, CurrentLock = held?.lock_value, HeldByUserName = held?.locked_by_name };
    }

    public async Task<WopiLockResultDto> GetLockAsync(long versionId, string? accessToken)
    {
        var token = ValidateToken(versionId, accessToken);
        if (token == null) return new WopiLockResultDto { Granted = false };
        var held = await _unitOfWork.Authoring.GetLockAsync(versionId);
        if (held == null || held.HasExpired(DateTime.UtcNow)) return new WopiLockResultDto { Granted = true, CurrentLock = string.Empty };
        return new WopiLockResultDto { Granted = true, CurrentLock = held.lock_value, HeldByUserName = held.locked_by_name, ExpiresAtUtc = held.expires_at_utc };
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(DocumentDetail? Document, DocumentVersion? Version)> LoadAsync(long versionId)
    {
        var documentId = await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId);
        if (documentId == null) return (null, null);
        var document = await _unitOfWork.Document.GetByIdAsync(documentId.Value);
        var version = document == null ? null : (await _unitOfWork.Document.GetVersionsAsync(document.id)).FirstOrDefault(v => v.id == versionId);
        return (document, version);
    }

    private async Task<bool> StillEditableAsync(long documentId, int userId)
    {
        var user = (await _unitOfWork.UserService.GetUsersById(userId)).FirstOrDefault();
        if (user == null) return false;
        var access = await _unitOfWork.Document.GetAccessAsync(documentId, userId);
        return access.can_author;
    }

    private static WopiSaveResultDto Refuse(WopiFailure failure, string message) => new() { Saved = false, Failure = failure, Message = message };

    private static string AppendQuery(string url, string query) => url.Contains('?') ? $"{url.TrimEnd('&')}&{query}" : $"{url}?{query}";
}
