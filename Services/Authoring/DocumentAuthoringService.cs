//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* URS-7.1 in-application authoring - ported from Bcl.Dms DocumentAuthoringService.
 *
 * The body is written in the browser editor (wwwroot/js/dms-editor.js), sanitised on the server by a
 * whitelist (EditorHtmlSanitizer), turned into a real .docx (HtmlDocxWriter) that becomes the revision's
 * source file, and recorded with a paragraph-level before/after in the audit trail - all in one database
 * transaction (sp_content_commit). Autosave keeps an uncommitted draft only; it is not the record.
 */
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Model.Master;

namespace DMS.Web.Services.Authoring;

public sealed class AuthoringSession
{
    public long DocumentId { get; set; }
    public long DocumentVersionId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DisplayRevision { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;
    public string? StateBadge { get; set; }
    public string Html { get; set; } = string.Empty;
    public bool RestoredFromDraft { get; set; }
    public DateTime? DraftSavedAt { get; set; }
    public long EditCount { get; set; }
    public long RowVersion { get; set; }
    public int AutosaveSeconds { get; set; }
    public string? TemplateName { get; set; }
    public DocumentPageSetupDto PageSetup { get; set; } = DocumentPageSetupDto.Default;
    public bool HeaderFooterEnabled { get; set; } = true;
    public bool DerivedFromUploadedFile { get; set; }
    public string? DerivationNotice { get; set; }
    public int MaxImageBytes { get; set; }
}

public sealed class AuthoringSaveRequest
{
    public long DocumentId { get; set; }
    public long DocumentVersionId { get; set; }
    public string? Html { get; set; }
    public long ExpectedRowVersion { get; set; }
    public string? Reason { get; set; }
    public DocumentPageSetupDto? PageSetup { get; set; }
}

public sealed class AuthoringSaveResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? FileName { get; set; }
    public long RowVersion { get; set; }
    public string? Notice { get; set; }
    public DocumentPageSetupDto? PageSetup { get; set; }
}

public sealed class DocumentAuthoringService
{
    private const string DocxMimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IConfigService _config;
    private readonly FileStore _fileStore;
    private readonly IEditorHtmlSanitizer _sanitizer;
    private readonly IHtmlDocxWriter _docx;
    private readonly IDocumentPreviewRenderer _preview;
    private readonly ILogger<DocumentAuthoringService> _logger;

    public DocumentAuthoringService(IUnitOfWork unitOfWork, IConfigService config, FileStore fileStore,
        IEditorHtmlSanitizer sanitizer, IHtmlDocxWriter docx, IDocumentPreviewRenderer preview,
        ILogger<DocumentAuthoringService> logger)
    {
        _unitOfWork = unitOfWork;
        _config = config;
        _fileStore = fileStore;
        _sanitizer = sanitizer;
        _docx = docx;
        _preview = preview;
        _logger = logger;
    }

    public Task<bool> IsEnabledAsync() => _config.GetBoolAsync(ConfigKeys.AuthoringEnabled, true);

    // ------------------------------------------------------------------ open

    public async Task<(AuthoringSession? Session, string? Error)> OpenAsync(long versionId, int userId)
    {
        if (!await IsEnabledAsync())
            return (null, "The in-application editor is switched off (authoring.enabled). Upload the authored file instead.");

        var (document, version, error) = await LoadForAuthoringAsync(versionId, userId);
        if (error != null) return (null, error);

        var seeded = await SeedContentAsync(document!, version!, userId);
        var content = await _unitOfWork.Authoring.GetContentAsync(versionId);

        var session = new AuthoringSession
        {
            DocumentId = document!.id,
            DocumentVersionId = version!.id,
            DocumentNumber = document.document_number ?? "",
            Title = document.title ?? "",
            DisplayRevision = version.display_revision ?? "",
            StateName = document.state_name ?? "",
            StateBadge = document.badge_css_class,
            AutosaveSeconds = await _config.GetIntAsync(ConfigKeys.AuthoringAutosaveSeconds, 45),
            RowVersion = content?.row_version ?? 1,
            EditCount = content?.edit_count ?? 0,
            DerivedFromUploadedFile = seeded.DerivedFromUpload,
            DerivationNotice = seeded.Notice,
            TemplateName = content?.source_template_name,
            PageSetup = await ResolvePageSetupAsync(content),
            HeaderFooterEnabled = await _config.GetBoolAsync(ConfigKeys.AuthoringHeaderFooterEnabled, true),
            MaxImageBytes = await _config.GetIntAsync(ConfigKeys.AuthoringMaxImageBytes, 2 * 1024 * 1024)
        };

        if (content?.HasUncommittedDraft == true)
        {
            session.Html = content.draft_html ?? string.Empty;
            session.RestoredFromDraft = true;
            session.DraftSavedAt = content.draft_saved_at;
        }
        else
        {
            session.Html = content?.content_html ?? string.Empty;
        }
        return (session, null);
    }

    // ------------------------------------------------------------------ save (the record)

    public async Task<AuthoringSaveResult> CommitAsync(AuthoringSaveRequest request, int actor, string? ip)
    {
        if (!await IsEnabledAsync())
            return Fail("The in-application editor is switched off (authoring.enabled).");

        var (document, version, error) = await LoadForAuthoringAsync(request.DocumentVersionId, actor);
        if (error != null) return Fail(error);

        var limits = await ReadLimitsAsync();
        var sanitized = _sanitizer.Sanitize(request.Html, limits);
        if (sanitized.Rejected) return Fail(sanitized.RejectionReason ?? "The document body could not be accepted.");
        if (string.IsNullOrWhiteSpace(sanitized.Html)) return Fail("The document is empty. Write some content before saving.");

        var existing = await _unitOfWork.Authoring.GetContentAsync(version!.id);
        var pageSetup = request.PageSetup is null
            ? await ResolvePageSetupAsync(existing)
            : await SanitisePageSetupAsync(request.PageSetup, limits);
        var settingsJson = request.PageSetup is null ? null : JsonSerializer.Serialize(pageSetup, JsonOptions);

        var author = (await _unitOfWork.UserService.GetUsersById(actor)).FirstOrDefault();

        StoredFile stored;
        try
        {
            await using var package = _docx.Write(sanitized.Html, new DocxWriteMetadata
            {
                Title = document!.title ?? "",
                DocumentNumber = document.document_number ?? "",
                Revision = version.display_revision ?? "",
                Author = author?.DisplayName ?? author?.username ?? "DMS",
                CreatedUtc = DateTime.UtcNow,
                PageSetup = pageSetup
            });
            var fileName = $"{FileStore.SafeFileName(document.document_number)}-{FileStore.SafeFileName(version.display_revision)}.docx";
            stored = await _fileStore.SaveAsync(package, fileName, DocxMimeType, "documents");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Writing the authored .docx failed for version {VersionId}", version.id);
            return Fail("The document could not be saved to the file store. Your text has not been lost - " +
                        "leave this page open and try again, or contact IT support.");
        }

        var contentSha = Sha256Hex(sanitized.Html);
        var changes = ContentChangeRules.Compare(existing?.content_html, sanitized.Html);

        var before = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ContentSha256"] = existing?.content_sha256?.Trim() };
        var after = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ContentSha256"] = contentSha,
            ["FileName"] = stored.FileName,
            ["FileSha256"] = stored.Sha256,
            ["SizeBytes"] = stored.SizeBytes,
            ["Authored"] = true
        };
        foreach (var change in changes.Changes)
        {
            before[change.Key] = change.OldText;
            after[change.Key] = change.NewText;
        }
        // The persisted values contain only modified spans, without the unchanged document body.
        var delta=SavedContentAudit.WithEvidence(SavedContentAudit.Changes(existing?.content_html,version.source_file_name,sanitized.Html,stored.FileName),
            version.source_sha256,version.source_storage_path,stored.Sha256,stored.RelativePath);
        before=JsonSerializer.Deserialize<Dictionary<string,object?>>(delta.Before)!;
        after=JsonSerializer.Deserialize<Dictionary<string,object?>>(delta.After)!;

        var result = await _unitOfWork.Authoring.CommitContentAsync(version.id, sanitized.Html, contentSha, settingsJson,
            request.ExpectedRowVersion, stored, JsonSerializer.Serialize(before), JsonSerializer.Serialize(after),
            $"Document body edited in the application ({stored.FileName}). {ContentChangeRules.Describe(changes)}",
            request.Reason, actor, ip);

        if (!result.Success)
        {
            _fileStore.Archive(stored.RelativePath);   // never referenced by the record
            return Fail(result.Message);
        }

        return new AuthoringSaveResult
        {
            Ok = true,
            FileName = stored.FileName,
            RowVersion = result.Id,
            Notice = sanitized.ChangeNotice,
            PageSetup = pageSetup
        };
    }

    public async Task<DbResult> AutosaveAsync(long versionId, string? html, int actor)
    {
        var limits = await ReadLimitsAsync();
        var sanitized = _sanitizer.Sanitize(html, limits);
        if (sanitized.Rejected) return DbResult.Fail(sanitized.RejectionReason ?? "The document body could not be accepted.");
        return await _unitOfWork.Authoring.SaveDraftAsync(versionId, sanitized.Html, actor);
    }

    public Task<DbResult> DiscardDraftAsync(long versionId, int actor) => _unitOfWork.Authoring.DiscardDraftAsync(versionId, actor);

    /// <summary>A new document started "from the template in the editor": its body is copied from the template.</summary>
    public async Task SeedFromTemplateAsync(long documentId, long versionId, long templateId, int actor)
    {
        var template = await _unitOfWork.Authoring.GetTemplateContentAsync(templateId);
        if (template == null) return;
        string? html = null;
        if (!string.IsNullOrWhiteSpace(template.content_html))
        {
            var sanitized = _sanitizer.Sanitize(template.content_html, await ReadLimitsAsync());
            html = sanitized.Rejected ? null : sanitized.Html;
        }
        await _unitOfWork.Authoring.EnsureContentAsync(documentId, versionId, html, template.settings_json, templateId, actor);
    }

    // ------------------------------------------------------------------ template bodies

    public async Task<DbResult> SaveTemplateContentAsync(long templateId, string? html, string? mode, DocumentPageSetupDto? pageSetup,
                                                         int actor, string? ip)
    {
        var limits = await ReadLimitsAsync();
        var sanitized = _sanitizer.Sanitize(html, limits);
        if (sanitized.Rejected) return DbResult.Fail(sanitized.RejectionReason ?? "The template body could not be accepted.");
        var settingsJson = pageSetup is null ? null : JsonSerializer.Serialize(await SanitisePageSetupAsync(pageSetup, limits), JsonOptions);
        return await _unitOfWork.Authoring.SetTemplateContentAsync(templateId, sanitized.Html, mode ?? "EDITOR", settingsJson, actor, ip);
    }

    public async Task<string> TemplatePageSetupJsonAsync(string? settingsJson)
        => JsonSerializer.Serialize(Deserialise(settingsJson)?.Normalised() ?? await DefaultPageSetupAsync(), JsonOptions);

    public string PageSetupJson(DocumentPageSetupDto setup) => JsonSerializer.Serialize(setup, JsonOptions);

    // ------------------------------------------------------------------ helpers

    private async Task<(DocumentDetail? Document, DocumentVersion? Version, string? Error)> LoadForAuthoringAsync(long versionId, int userId)
    {
        var documentId = await FindDocumentIdAsync(versionId);
        if (documentId == null) return (null, null, "The document revision was not found.");
        var document = await _unitOfWork.Document.GetByIdAsync(documentId.Value);
        if (document == null) return (null, null, "The document was not found.");
        var version = (await _unitOfWork.Document.GetVersionsAsync(document.id)).FirstOrDefault(v => v.id == versionId);
        if (version == null) return (null, null, "The document revision was not found.");

        var access = await _unitOfWork.Document.GetAccessAsync(document.id, userId);
        if (!access.can_author)
            return (null, null, $"Your access rights do not permit editing the content of a document in state '{document.state_name}'.");
        if (!document.state_is_editable)
            return (null, null, $"The document cannot be edited while it is in state '{document.state_name}'. Start a revision to change an effective document.");
        if (document.current_version_id is { } current && current != version.id)
            return (null, null, "This is not the current revision. Only the revision in progress can be edited.");
        return (document, version, null);
    }

    private async Task<long?> FindDocumentIdAsync(long versionId)
        => (await _unitOfWork.Authoring.GetContentAsync(versionId))?.document_id
           ?? await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId);

    private async Task<(bool DerivedFromUpload, string? Notice)> SeedContentAsync(DocumentDetail document, DocumentVersion version, int actor)
    {
        var existing = await _unitOfWork.Authoring.GetContentAsync(version.id);
        if (existing != null && !string.IsNullOrWhiteSpace(existing.content_html)) return (false, null);

        // an uploaded Word file is read into the editor so it can be revised here
        if (!string.IsNullOrWhiteSpace(version.source_storage_path) && !string.IsNullOrWhiteSpace(version.source_file_name) &&
            _preview.CanRender(version.source_mime_type ?? string.Empty, version.source_file_name!))
        {
            try
            {
                var rendered = await _preview.RenderAsync(version.source_storage_path!, version.source_file_name!, version.source_mime_type ?? "");
                if (!string.IsNullOrWhiteSpace(rendered.Html))
                {
                    var sanitized = _sanitizer.Sanitize(rendered.Html, await ReadLimitsAsync());
                    if (!sanitized.Rejected && !string.IsNullOrWhiteSpace(sanitized.Html))
                    {
                        await _unitOfWork.Authoring.EnsureContentAsync(document.id, version.id, sanitized.Html, null, null, actor);
                        return (true, $"This document was uploaded as {version.source_file_name}. Its text, tables and figures have " +
                                      "been read into the editor so you can revise them here, but exact page geometry, headers and " +
                                      "footers from the original file are not reproduced. Saving replaces the stored file with the " +
                                      "document as it appears in the editor.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not derive an editable body from {File} for version {VersionId}",
                    version.source_file_name, version.id);
            }
        }

        await _unitOfWork.Authoring.EnsureContentAsync(document.id, version.id, null, null, null, actor);
        return (false, null);
    }

    private async Task<EditorSanitizeLimits> ReadLimitsAsync() => new()
    {
        MaxContentBytes = await _config.GetIntAsync(ConfigKeys.AuthoringMaxContentBytes, 4 * 1024 * 1024),
        MaxImageBytes = await _config.GetIntAsync(ConfigKeys.AuthoringMaxImageBytes, 2 * 1024 * 1024),
        MaxTotalImageBytes = await _config.GetIntAsync(ConfigKeys.AuthoringMaxTotalImageBytes, 12 * 1024 * 1024)
    };

    private async Task<DocumentPageSetupDto> ResolvePageSetupAsync(DocumentContentRow? content)
    {
        var own = Deserialise(content?.settings_json);
        if (own != null) return own.Normalised();
        if (content?.source_template_id is { } templateId)
        {
            var inherited = Deserialise((await _unitOfWork.Authoring.GetTemplateContentAsync(templateId))?.settings_json);
            if (inherited != null) return inherited.Normalised();
        }
        return await DefaultPageSetupAsync();
    }

    private async Task<DocumentPageSetupDto> DefaultPageSetupAsync()
        => (Deserialise(await _config.GetStringAsync(ConfigKeys.AuthoringDefaultPageSetup, null)) ?? DocumentPageSetupDto.Default).Normalised();

    private async Task<DocumentPageSetupDto> SanitisePageSetupAsync(DocumentPageSetupDto setup, EditorSanitizeLimits limits)
    {
        var normalised = setup.Normalised();
        if (!await _config.GetBoolAsync(ConfigKeys.AuthoringHeaderFooterEnabled, true))
        {
            normalised.HeaderHtml = normalised.FooterHtml = normalised.FirstHeaderHtml = null;
            normalised.FirstFooterHtml = normalised.EvenHeaderHtml = normalised.EvenFooterHtml = null;
            return normalised;
        }
        normalised.HeaderHtml = Clean(setup.HeaderHtml, limits);
        normalised.FooterHtml = Clean(setup.FooterHtml, limits);
        normalised.FirstHeaderHtml = Clean(setup.FirstHeaderHtml, limits);
        normalised.FirstFooterHtml = Clean(setup.FirstFooterHtml, limits);
        normalised.EvenHeaderHtml = Clean(setup.EvenHeaderHtml, limits);
        normalised.EvenFooterHtml = Clean(setup.EvenFooterHtml, limits);
        return normalised;
    }

    private string? Clean(string? html, EditorSanitizeLimits limits)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var result = _sanitizer.Sanitize(html, limits);
        return result.Rejected || string.IsNullOrWhiteSpace(result.Html) ? null : result.Html;
    }

    private DocumentPageSetupDto? Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DocumentPageSetupDto>(json, JsonOptions); }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Stored page setup could not be read; falling back to the default.");
            return null;
        }
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AuthoringSaveResult Fail(string message) => new() { Ok = false, Message = message };
}
