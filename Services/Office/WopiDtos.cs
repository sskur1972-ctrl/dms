//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Text.Json.Serialization;

using DMS.Web.Services;

namespace DMS.Web.Services.Office;

/// <summary>
/// Editing a controlled document in a web-based Office editor, with this
/// application as the WOPI host.
///
/// <para><b>What WOPI is, in one paragraph</b></para>
/// WOPI (Web Application Open Platform Interface) is the protocol Office
/// editors use to talk to whoever holds the file. The editor is the *client*;
/// this application is the *host*. The editor asks the host three things - what
/// is this file, give me its bytes, here are its new bytes - plus locking. The
/// host decides who may open it, what they may do, and what happens to the
/// bytes that come back. Collabora Online and Microsoft
/// Office for the web all speak it.
///
/// <para><b>Why the host stays in charge</b></para>
/// The editor is a rendering and editing engine, not a document management
/// system. It has no idea that a revision must not be edited once it is
/// approved, that a save creates a new revision with a SHA-256 and an audit
/// entry, or that a PDF master has to be re-rendered afterwards. All of that
/// stays here, which is what makes it safe to let a second application touch a
/// controlled document at all.
/// </summary>
public sealed class WopiCheckFileInfoDto
{
    // -------------------------------------------------------------------------
    // Required by the protocol
    // -------------------------------------------------------------------------

    /// <summary>The file name WITH its extension. The editor picks its mode from the extension.</summary>
    [JsonPropertyName("BaseFileName")]
    public string BaseFileName { get; set; } = string.Empty;

    [JsonPropertyName("Size")]
    public long Size { get; set; }

    /// <summary>
    /// Changes whenever the file's content changes. The editor compares it to
    /// detect that the file was modified underneath it - which for us happens
    /// when the in-application editor or an upload writes a new source file.
    /// </summary>
    [JsonPropertyName("Version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// A stable, opaque id for the user. Used for presence and for "who has it
    /// open"; never the user's password or session, and never an e-mail address
    /// we have no reason to disclose to the editor.
    /// </summary>
    [JsonPropertyName("UserId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Shown in the editor's UI as the person editing.</summary>
    [JsonPropertyName("UserFriendlyName")]
    public string UserFriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// The file's owner, as an opaque id. <b>Required by the protocol</b>, and
    /// omitting it is one of the ways a real client refuses a document that
    /// otherwise looks fine - the field is not optional however little the
    /// editor appears to do with it. Here it is the document's owner (its
    /// author), not the person who happens to have it open.
    /// </summary>
    [JsonPropertyName("OwnerId")]
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>
    /// The origin the editor may postMessage to. A WOPI client uses
    /// postMessage to tell the host page that the document has loaded, that
    /// the user pressed Close, and that a save happened; without this field
    /// those messages are never sent, so the Close button appears to do nothing
    /// and the host page cannot tell a slow load from a failed one.
    /// </summary>
    [JsonPropertyName("PostMessageOrigin")]
    public string? PostMessageOrigin { get; set; }

    /// <summary>
    /// When the file last changed, ISO 8601. Used by the editor's own
    /// "somebody else changed this" detection alongside <see cref="Version"/>.
    /// </summary>
    [JsonPropertyName("LastModifiedTime")]
    public string? LastModifiedTime { get; set; }

    // -------------------------------------------------------------------------
    // Capabilities - this is where the access matrix (URS-7.16) reaches the editor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Whether this user may save. False makes the editor read-only, which is
    /// how a reviewer opens a document in it without being able to change it.
    /// </summary>
    [JsonPropertyName("UserCanWrite")]
    public bool UserCanWrite { get; set; }

    [JsonPropertyName("ReadOnly")]
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Advertises that the host implements PutFile. Without it the editor opens
    /// read-only however permissive the other flags are.
    /// </summary>
    [JsonPropertyName("SupportsUpdate")]
    public bool SupportsUpdate { get; set; }

    [JsonPropertyName("SupportsLocks")]
    public bool SupportsLocks { get; set; } = true;

    [JsonPropertyName("SupportsGetLock")]
    public bool SupportsGetLock { get; set; } = true;

    /// <summary>
    /// Deliberately FALSE, always. PutRelativeFile is "save a copy under a name
    /// of the editor's choosing", which would create a file in the store that
    /// no revision points at - an unexplained artefact in a GxP file store.
    /// </summary>
    [JsonPropertyName("SupportsRename")]
    public bool SupportsRename { get; set; }

    [JsonPropertyName("UserCanRename")]
    public bool UserCanRename { get; set; }

    [JsonPropertyName("UserCanNotWriteRelative")]
    public bool UserCanNotWriteRelative { get; set; } = true;

    // -------------------------------------------------------------------------
    // The controls that carry URS-7.16 into the editor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Hides the editor's print command when the role may not print. Not a
    /// security boundary - it is the editor's own UI - but the controlled print
    /// path in this system is the one that produces a numbered copy and a print
    /// log entry, and the editor's print button bypasses it.
    /// </summary>
    [JsonPropertyName("DisablePrint")]
    public bool DisablePrint { get; set; }

    [JsonPropertyName("HidePrintOption")]
    public bool HidePrintOption { get; set; }

    /// <summary>Hides "download a copy" when the role may not take the source file.</summary>
    [JsonPropertyName("DisableExport")]
    public bool DisableExport { get; set; }

    [JsonPropertyName("HideExportOption")]
    public bool HideExportOption { get; set; }

    [JsonPropertyName("HideSaveOption")]
    public bool HideSaveOption { get; set; }

    /// <summary>Discourages copy to the clipboard where the role may not copy.</summary>
    [JsonPropertyName("DisableCopy")]
    public bool DisableCopy { get; set; }

    /// <summary>
    /// The reader-identifying watermark, in the editor's own rendering.
    /// </summary>
    /// <remarks>
    /// The in-application viewer draws this itself; inside a third-party editor
    /// we can only ask, and only some engines honour it. Where it is not
    /// honoured the deterrent is weaker than the one this system provides
    /// natively - which is a real regression against URS-7.16 and is recorded as
    /// one in docs/LIMITATIONS.md, not papered over.
    /// </remarks>
    [JsonPropertyName("WatermarkText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatermarkText { get; set; }

    /// <summary>
    /// Where the editor's Close button sends the user: back to the document in
    /// this application, not to a blank tab.
    /// </summary>
    [JsonPropertyName("CloseUrl")]
    public string? CloseUrl { get; set; }

    [JsonPropertyName("HostEditUrl")]
    public string? HostEditUrl { get; set; }

    /// <summary>Shown in the editor's title bar, so the author can see which revision they are in.</summary>
    [JsonPropertyName("BreadcrumbDocName")]
    public string? BreadcrumbDocName { get; set; }

    [JsonPropertyName("BreadcrumbFolderName")]
    public string? BreadcrumbFolderName { get; set; }

    [JsonPropertyName("BreadcrumbFolderUrl")]
    public string? BreadcrumbFolderUrl { get; set; }

    /// <summary>
    /// Turns off the editor's own file history UI. Revision history in this
    /// system is a signed, audited record; a second history inside the editor
    /// would be a parallel account of what changed, outside the audit trail.
    /// </summary>
    [JsonPropertyName("SupportsRestoreVersion")]
    public bool SupportsRestoreVersion { get; set; }

    [JsonPropertyName("SupportsUserInfo")]
    public bool SupportsUserInfo { get; set; }
}

/// <summary>What the editor is allowed to do in this session, decided before the token is issued.</summary>
public sealed class WopiPermissionsDto
{
    public bool CanWrite { get; set; }
    public bool CanPrint { get; set; }
    public bool CanExport { get; set; }
    public bool ApplyWatermark { get; set; } = true;

    public static WopiPermissionsDto ReadOnly => new()
    {
        CanWrite = false, CanPrint = false, CanExport = false, ApplyWatermark = true
    };
}

/// <summary>
/// A WOPI access token, signed by this application.
///
/// <para>
/// It is not a session. It is scoped to ONE revision, ONE user and ONE set of
/// permissions, it expires, and it is signed with the application's Data
/// Protection key ring so a token cannot be forged or edited - a token that
/// could be widened by changing a query string would be an authorisation
/// bypass, because the editor's calls arrive with no cookie.
/// </para>
/// </summary>
public sealed class WopiTokenDto
{
    public long? AttachmentId { get; set; }
    public long? AttachmentDocumentId { get; set; }
    public string? BrowserOrigin {get;set;}
    public long DocumentVersionId { get; set; }
    public long UserId { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public WopiPermissionsDto Permissions { get; set; } = WopiPermissionsDto.ReadOnly;

    /// <summary>
    /// The correlation id of the request that created it, so every WOPI call the
    /// editor makes can be tied back to the click that opened the editor.
    /// </summary>
    public string? Origin { get; set; }
}

/// <summary>Everything the host page needs to hand the document to the editor.</summary>
public sealed class WopiSessionDto
{
    public long DocumentId { get; set; }
    public long DocumentVersionId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DisplayRevision { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>The editor's URL for this file type, from its discovery document, with WOPISrc appended.</summary>
    public string EditorUrl { get; set; } = string.Empty;

    /// <summary>The signed token, POSTed to the editor in a form rather than put in the URL.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Unix milliseconds, which is the unit WOPI specifies for this one field.</summary>
    public long AccessTokenTtl { get; set; }

    public bool ReadOnly { get; set; }
    public string EditorName { get; set; } = "COLLABORA";
}

/// <summary>The outcome of a lock request, in the shape the protocol needs to answer with.</summary>
public sealed class WopiLockResultDto
{
    public bool Granted { get; set; }

    /// <summary>
    /// The lock currently held, when the request was refused. WOPI requires the
    /// host to return it: it is how the editor distinguishes "somebody else has
    /// this open" from "my own lock expired and I should retake it".
    /// </summary>
    public string? CurrentLock { get; set; }

    public string? HeldByUserName { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Why it was refused, returned to the editor in X-WOPI-LockFailureReason
    /// so a rejection the user can do something about does not look like a
    /// generic conflict.
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>A lock row as stored.</summary>
public sealed class DocumentEditLockDto
{
    public long Id { get; set; }
    public long DocumentVersionId { get; set; }
    public string LockValue { get; set; } = string.Empty;
    public long LockedByUserId { get; set; }
    public string? LockedByUserName { get; set; }
    public DateTime LockedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? EditorName { get; set; }

    public bool HasExpired(DateTime nowUtc) => ExpiresAtUtc <= nowUtc;
}

/// <summary>Why a WOPI request was refused, mapped to the status code the protocol expects.</summary>
public enum WopiFailure
{
    None = 0,

    /// <summary>401 - the token is missing, expired, forged, or for a different file.</summary>
    Unauthorized,

    /// <summary>404 - no such revision, or no source file on it.</summary>
    NotFound,

    /// <summary>409 - lock mismatch, or the revision is in a state that cannot be edited.</summary>
    Conflict,

    /// <summary>413 - the file the editor posted back is larger than the store accepts.</summary>
    TooLarge,

    /// <summary>501 - a WOPI operation this host deliberately does not implement.</summary>
    NotImplemented,

    /// <summary>500 - something failed that should not have.</summary>
    Failed
}

/// <summary>The result of a PutFile: a new revision of the source file, or a refusal.</summary>
public sealed class WopiSaveResultDto
{
    public bool Saved { get; set; }
    public WopiFailure Failure { get; set; }
    public string? Message { get; set; }
    public string? CurrentLock { get; set; }

    /// <summary>The new content version, which the editor keeps to detect later external changes.</summary>
    public string? Version { get; set; }

    public string? Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public bool RenditionQueued { get; set; }
}
