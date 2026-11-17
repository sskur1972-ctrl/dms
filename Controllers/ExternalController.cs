using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;
using Dapper;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Web.Services;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace DMS.Web.Controllers;

[ApiController, Authorize(AuthenticationSchemes = DmsAuthentication.Bearer), Route("api/integration/external")]
public sealed class ExternalController(
    IUnitOfWork unitOfWork,
    IConfiguration configuration,
    ExternalIntegrationSecurity security,
    FileStore fileStore,
    PasswordCipher passwordCipher,
    IWebHostEnvironment environment,
    ILogger<ExternalController> logger) : Controller
{
    private bool TryGetAuthenticatedUserId(out int userId)
        => int.TryParse(User.FindFirst("UserID")?.Value, out userId) && userId > 0;

    private IActionResult UnauthorizedExternal()
    {
        Response.Headers["WWW-Authenticate"] = "Bearer";
        return Unauthorized(new { success = false, message = "Unauthorized." });
    }

    [AllowAnonymous, HttpPost("token")]
    [EnableRateLimiting("external-token")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> CreateToken([FromBody] ExternalTokenRequest request, CancellationToken ct)
    {
        var isLoopbackDevelopment = environment.IsDevelopment() &&
            System.Net.IPAddress.IsLoopback(HttpContext.Connection.RemoteIpAddress ?? System.Net.IPAddress.None);
        if (!Request.IsHttps && !isLoopbackDevelopment)
            return StatusCode(400, new { success = false, message = "Token requests require HTTPS." });

        var connectionString = configuration.GetConnectionString("DMSConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            return StatusCode(503, new { success = false, message = "Authentication is not configured." });

        var username = request.Username.Trim();
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            var account = await connection.QuerySingleOrDefaultAsync<ExternalAuthAccount>(new CommandDefinition("""
                SELECT u.pk_user, u.password, u.isactive, u.isarchived, u.must_change_password,
                       u.is_locked, u.locked_until
                FROM common.sp_dms_getuserforlogin(@username) u
                WHERE lower(trim(u.username)) = lower(trim(@username))
                """, new { username }, cancellationToken: ct));

            if (account is null || account.isactive != true || account.isarchived != false ||
                account.must_change_password || account.is_locked ||
                account.locked_until > DateTime.UtcNow ||
                !passwordCipher.Verify(request.Password, account.password))
                return Unauthorized(new { success = false, message = "Invalid username or password." });

            var issued = security.IssueUserToken(account.pk_user);
            return Ok(new
            {
                success = true,
                access_token = issued.Token,
                token_type = "Bearer",
                expires_in = (int)TimeSpan.FromMinutes(15).TotalSeconds,
                expires_at_utc = issued.ExpiresAt.ToString("O")
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "External token signing is not configured.");
            return StatusCode(503, new { success = false, message = "Token signing is not configured." });
        }
        catch (Exception ex) when (ex is NpgsqlException or PostgresException)
        {
            logger.LogError(ex, "External token authentication database request failed.");
            return StatusCode(503, new { success = false, message = "Authentication is temporarily unavailable." });
        }
    }

    [HttpGet("documents")]
    public async Task<IActionResult> Documents(CancellationToken ct)
    {
        if (!TryGetAuthenticatedUserId(out var readerId))
            return UnauthorizedExternal();
        if (!TryGetPublicBaseUrl(out var publicBase))
            return StatusCode(503, new { success = false, message = "The External public base URL is not configured." });

        try
        {
            var items = new List<DMS.Model.Documents.DocumentListItem>();
            const int pageSize = 200;
            for (var page = 1; page <= 500; page++)
            {
                var batch = await unitOfWork.Document.SearchAsync(new DocumentSearchQuery
                {
                    includeObsolete = false,
                    page = page,
                    pageSize = pageSize,
                    sort = "number",
                    desc = false
                }, readerId);
                items.AddRange(batch.Where(item => !item.is_obsolete));
                if (batch.Count < pageSize || (batch.Count > 0 && batch[0].total_count <= page * pageSize)) break;
                if (page == 500)
                    return StatusCode(500, new { success = false, message = "The document list could not be read." });
            }

            var response = new List<ExternalDocumentDto>(items.Count);
            foreach (var item in items.OrderBy(item => item.document_number, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var document = await unitOfWork.Document.GetByIdAsync(item.id);
                if (document == null || document.is_obsolete) continue;
                var access = await unitOfWork.Document.GetAccessAsync(document.id, readerId);
                if (!access.can_view || !access.can_view_content) continue;

                var versions = await unitOfWork.Document.GetVersionsAsync(document.id);
                var viewable = await FindViewableVersionAsync(document, versions, ct);
                var url = viewable == null || document.effective_version_id != viewable.id
                    ? null
                    : await GetOrCreateExternalShareUrlAsync(document.id, readerId, publicBase, ct);

                response.Add(new ExternalDocumentDto
                {
                    DocumentId = document.id,
                    DocumentNumber = document.document_number,
                    Title = document.title,
                    TypeCode = document.document_type_code,
                    TypeName = document.document_type_name,
                    Department = document.department_name,
                    State = document.state_name,
                    IsObsolete = false,
                    EffectiveVersionId = document.effective_version_id,
                    EffectiveVersionNo = document.effective_version_no,
                    EffectiveFrom = document.effective_from?.ToString("yyyy-MM-dd"),
                    CurrentVersionId = document.current_version_id,
                    CurrentVersionNo = CurrentVersionLabel(document, versions),
                    CurrentState = document.state_name,
                    RevisionInProgress = document.effective_version_id != null && document.current_version_id != document.effective_version_id,
                    ViewableVersionId = viewable?.id,
                    DocumentUrl = url
                });
            }

            var externalUser = ExternalUserHeader();
            logger.LogInformation("External document list returned {Count} items for {ExternalUser}", response.Count, externalUser ?? "(not supplied)");
            return Ok(new { success = true, count = response.Count, data = response });
        }
        catch (ExternalShareLinkException ex)
        {
            logger.LogWarning("External view link creation failed with status {StatusCode}.", ex.StatusCode);
            return StatusCode(ex.StatusCode, new
            {
                success = false,
                message = ex.StatusCode == 403
                    ? "The authenticated user is not allowed to create external view links."
                    : "External view links are disabled or unavailable."
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "External document list failed.");
            return StatusCode(500, new { success = false, message = "The document list could not be read." });
        }
    }

    [HttpPost("documents/{documentId:long}/view-link")]
    public async Task<IActionResult> CreateDocumentViewLink(long documentId, CancellationToken ct)
    {
        if (!TryGetAuthenticatedUserId(out var userId))
            return UnauthorizedExternal();
        if (!TryGetPublicBaseUrl(out var publicBase))
            return StatusCode(503, new { success = false, message = "The External public base URL is not configured." });

        var document = await unitOfWork.Document.GetByIdAsync(documentId);
        if (document == null || document.is_obsolete || document.effective_version_id == null)
            return NotFound(new { success = false, message = "An effective document was not found." });
        var access = await unitOfWork.Document.GetAccessAsync(documentId, userId);
        if (!access.can_view || !access.can_view_content)
            return StatusCode(403, new { success = false, message = "The authenticated user cannot view this document." });
        var status = await unitOfWork.Rendition.GetStatusAsync(document.effective_version_id.Value);
        if (status?.rendered_pdf_path == null || !status.IsCurrent || !status.base_ready)
            return Conflict(new { success = false, message = "The controlled PDF is not ready yet." });

        try
        {
            var url = await GetOrCreateExternalShareUrlAsync(documentId, userId, publicBase, ct, allowRecreate: true);
            return Ok(new { success = true, document_id = documentId, document_url = url });
        }
        catch (ExternalShareLinkException ex)
        {
            logger.LogWarning("External view link creation failed with status {StatusCode}.", ex.StatusCode);
            return StatusCode(ex.StatusCode, new
            {
                success = false,
                message = ex.StatusCode == 403
                    ? "The authenticated user is not allowed to create external view links."
                    : "External view links are disabled or unavailable."
            });
        }
    }

    [AllowAnonymous, HttpGet("documents/{documentId:long}/versions/{versionId:long}/view")]
    public async Task<IActionResult> ViewDocument(long documentId, long versionId, [FromQuery(Name = "user_id")] int userId, [FromQuery(Name = "sig")] string? signature, CancellationToken ct)
    {
        var file = await ResolveSignedDocumentAsync(documentId, versionId, userId, signature, ct);
        if (file == null) return NotFound(new { success = false, message = "The document view is not available." });
        var resolved = file.Value;
        SetViewOnlyHeaders();
        var model = new ExternalDocumentViewModel(resolved.Document.document_number ?? "Document", resolved.Document.title ?? "",
            resolved.Version.display_revision ?? resolved.Version.version_no ?? "", resolved.Document.state_name ?? "", resolved.Version.effective_from);
        model.PdfUrl = Url.Action(nameof(DocumentPdf), new { documentId, versionId, user_id = userId, sig = signature }) ?? "#";
        return View("~/Views/External/DocumentView.cshtml", model);
    }

    [AllowAnonymous, HttpGet("documents/{documentId:long}/versions/{versionId:long}/pdf")]
    public async Task<IActionResult> DocumentPdf(long documentId, long versionId, [FromQuery(Name = "user_id")] int userId, [FromQuery(Name = "sig")] string? signature, CancellationToken ct)
    {
        var file = await ResolveSignedDocumentAsync(documentId, versionId, userId, signature, ct);
        if (file == null || file.Value.Status.rendered_pdf_path == null)
            return NotFound(new { success = false, message = "The document view is not available." });
        var resolved = file.Value;
        SetViewOnlyHeaders();
        Response.Headers["Content-Disposition"] = "inline";
        try
        {
            return File(await fileStore.OpenCachedReadAsync(resolved.Status.rendered_pdf_path, ct), "application/pdf", enableRangeProcessing: true);
        }
        catch (FileStoreException)
        {
            return NotFound();
        }
    }

    [HttpPost("change-controls")]
    [RequestSizeLimit(64 * 1024)]
    public Task<IActionResult> CreateChangeControl([FromBody] ExternalChangeControlRequest request, CancellationToken ct)
        => SyncChangeControlAsync("CREATE", request.cc_number, request, ct);

    [HttpPut("change-controls/{ccNumber}")]
    [RequestSizeLimit(64 * 1024)]
    public Task<IActionResult> UpdateChangeControl(string ccNumber, [FromBody] ExternalChangeControlRequest request, CancellationToken ct)
    {
        if (!string.Equals(ccNumber?.Trim(), request.cc_number?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IActionResult>(BadRequest(new { success = false, message = "The path and body Change Control numbers must match." }));
        return SyncChangeControlAsync("UPDATE", ccNumber, request, ct);
    }

    private async Task<IActionResult> SyncChangeControlAsync(string mode, string? number, ExternalChangeControlRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedUserId(out var actor))
            return UnauthorizedExternal();
        if (!string.IsNullOrWhiteSpace(request.status) && !ExternalChangeControlRequest.AllowedStatuses.Contains(request.status.Trim(), StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { success = false, message = "Status must be UNVERIFIED, PENDING_APPROVAL, VERIFIED, CLOSED, or CANCEL." });
        var scope = request.applicability?.Trim().ToUpperInvariant();
        var requestedStatus = request.status?.Trim().ToUpperInvariant();
        if (scope == "PLANT" && (!request.plant_reference.HasValue || request.plant_reference.Value <= 0))
            return BadRequest(new { success = false, message = "plant_reference must be a positive plant ID when applicability is PLANT." });
        if (requestedStatus == "VERIFIED" && (!request.verified_by.HasValue || request.verified_by.Value <= 0))
            return BadRequest(new { success = false, message = "verified_by must be a positive common user ID when status is VERIFIED." });

        try
        {
            await using var connection = new NpgsqlConnection(configuration.GetConnectionString("DMSConnection"));
            var result = await connection.QuerySingleAsync<ChangeControlSyncResult>(new CommandDefinition(
                "SELECT * FROM dms.sp_changecontrol_external_sync(@mode::text,@number::text,@title::text,@initiatedOn::date,@remarks::text,@scope::text,@plant::text,@changeType::text,@description::text,@reason::text,@impact::text,@actionPlan::boolean,@status::text,@verifiedBy::bigint,@actor::integer,@ip::text,@externalUser::text)",
                new
                {
                    mode,
                    number,
                    title = request.title,
                    initiatedOn = request.initiated_on?.ToDateTime(TimeOnly.MinValue),
                    remarks = request.remarks,
                    scope,
                    plant = scope == "PLANT" ? request.plant_reference?.ToString(CultureInfo.InvariantCulture) : null,
                    changeType = request.change_type,
                    description = request.description,
                    reason = request.reason_for_change,
                    impact = request.impact_assessment,
                    actionPlan = request.requires_action_plan,
                    status = requestedStatus,
                    verifiedBy = request.verified_by,
                    actor,
                    ip = ClientIp(),
                    externalUser = ExternalUserHeader()
                }, cancellationToken: ct));

            var body = new
            {
                success = true,
                change_control_id = result.id,
                cc_number = number,
                operation = result.operation,
                status = result.status,
                verified_by = result.verified_by,
                updated_at_utc = DateTime.SpecifyKind(result.updated_at, DateTimeKind.Utc).ToString("O")
            };
            return mode == "CREATE" ? StatusCode(201, body) : Ok(body);
        }
        catch (PostgresException ex) when (ex.SqlState is "P0002")
        {
            return NotFound(new { success = false, message = "The Change Control number was not found." });
        }
        catch (PostgresException ex) when (ex.SqlState is "45005" or "23505" or "45003" or "45001")
        {
            return Conflict(new { success = false, message = SafeDatabaseMessage(ex.MessageText) });
        }
        catch (PostgresException ex) when (ex.SqlState is "42501" || (ex.SqlState == "45004" && ex.MessageText.StartsWith("FORBIDDEN:", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(403, new { success = false, message = "The authenticated user is inactive or does not have changecontrol.verify permission." });
        }
        catch (PostgresException ex) when (ex.SqlState == "45004")
        {
            return BadRequest(new { success = false, message = SafeDatabaseMessage(ex.MessageText) });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "External Change Control synchronization failed for {Mode}.", mode);
            return StatusCode(500, new { success = false, message = "The Change Control could not be synchronized." });
        }
    }

    private async Task<(DocumentDetail Document, DocumentVersion Version, RenditionStatus Status)?> ResolveSignedDocumentAsync(long documentId, long versionId, int userId, string? signature, CancellationToken ct)
    {
        if (!security.IsValidDocumentLink(documentId, versionId, userId, signature)) return null;

        var document = await unitOfWork.Document.GetByIdAsync(documentId);
        if (document == null || document.is_obsolete) return null;
        var access = await unitOfWork.Document.GetAccessAsync(documentId, userId);
        if (!access.can_view || !access.can_view_content) return null;
        var version = (await unitOfWork.Document.GetVersionsAsync(documentId)).FirstOrDefault(item => item.id == versionId);
        if (version == null) return null;
        var status = await unitOfWork.Rendition.GetStatusAsync(versionId);
        if (status?.rendered_pdf_path == null || !status.IsCurrent || !status.base_ready) return null;
        return (document, version, status);
    }

    private async Task<DocumentVersion?> FindViewableVersionAsync(DocumentDetail document, List<DocumentVersion> versions, CancellationToken ct)
    {
        var ordered = new List<DocumentVersion>();
        if (document.effective_version_id is long effectiveId)
        {
            var effective = versions.FirstOrDefault(version => version.id == effectiveId);
            if (effective != null) ordered.Add(effective);
        }
        ordered.AddRange(versions.OrderByDescending(version => version.version_seq).Where(version => ordered.All(existing => existing.id != version.id)));
        foreach (var version in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var status = await unitOfWork.Rendition.GetStatusAsync(version.id);
            if (status?.rendered_pdf_path != null && status.IsCurrent && status.base_ready) return version;
        }
        return null;
    }

    private async Task<string?> GetOrCreateExternalShareUrlAsync(long documentId, int readerId, string publicBase, CancellationToken ct, bool allowRecreate = false)
    {
        const string purpose = "External API";
        const string marker = "Managed by External API v1";
        var links = await unitOfWork.Rendition.GetShareLinksAsync(documentId);
        var managed = links.Where(link => string.Equals(link.purpose, purpose, StringComparison.Ordinal) &&
                                         string.Equals(link.note, marker, StringComparison.Ordinal)).ToList();

        for (var generation = 0; generation < managed.Count; generation++)
        {
            ct.ThrowIfCancellationRequested();
            var token = security.CreateExternalShareToken(documentId, generation);
            var known = managed.FirstOrDefault(link => string.Equals(link.token_hint, token[..16], StringComparison.Ordinal));
            if (known?.is_active == true) return $"{publicBase}/s/{token}";
        }

        // Listing must not silently undo a withdrawal. Reissue only through the explicit
        // POST endpoint so a DMS administrator can intentionally restore access.
        if (managed.Count > 0 && !allowRecreate) return null;

        var nextToken = security.CreateExternalShareToken(documentId, managed.Count);
        var created = await unitOfWork.Rendition.CreateShareLinkAsync(documentId, pinCurrent: false, days: 0,
            purpose, marker, ExternalIntegrationSecurity.HashShareToken(nextToken), nextToken[..16], readerId, ClientIp());
        if (created.Success) return $"{publicBase}/s/{nextToken}";

        // Concurrent API requests can race to create the same stable token. The database
        // migration makes creation idempotent; this fallback also supports a warm replica.
        if (created.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
            created.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase))
        {
            var refreshed = await unitOfWork.Rendition.GetShareLinksAsync(documentId);
            if (refreshed.Any(link => link.is_active && link.token_hint == nextToken[..16] &&
                                      link.purpose == purpose && link.note == marker))
                return $"{publicBase}/s/{nextToken}";
        }

        var status = created.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) ? 403 : 503;
        throw new ExternalShareLinkException(status, created.Message);
    }

    private static string? CurrentVersionLabel(DocumentDetail document, List<DocumentVersion> versions)
        => versions.FirstOrDefault(version => version.id == document.current_version_id)?.display_revision
            ?? document.current_version_no;

    private bool TryGetPublicBaseUrl(out string baseUrl)
    {
        baseUrl = (configuration["Integration:External:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps ||
                (environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp && System.Net.IPAddress.TryParse(uri.Host, out var ip) && System.Net.IPAddress.IsLoopback(ip)) ||
                (environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))) &&
               string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
               baseUrl == uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private string? ExternalUserHeader()
    {
        var value = Request.Headers["X-External-User"].ToString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || "@._+-".Contains(character)).Take(128).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? null : safe;
    }

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private sealed class ExternalShareLinkException(int statusCode, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }

    private static string SafeDatabaseMessage(string message)
    {
        var separator = message.IndexOf(':');
        return separator >= 0 && separator + 1 < message.Length ? message[(separator + 1)..].Trim() : "The Change Control update conflicts with its current state.";
    }

    private void SetViewOnlyHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    }

    private sealed class ChangeControlSyncResult
    {
        public long id { get; set; }
        public string operation { get; set; } = "";
        public string status { get; set; } = "";
        public long? verified_by { get; set; }
        public DateTime updated_at { get; set; }
    }

    private sealed class ExternalAuthAccount
    {
        public int pk_user { get; set; }
        public string? password { get; set; }
        public bool? isactive { get; set; }
        public bool? isarchived { get; set; }
        public bool must_change_password { get; set; }
        public bool is_locked { get; set; }
        public DateTime? locked_until { get; set; }
    }
}

public sealed class ExternalTokenRequest
{
    [JsonPropertyName("username"), Required, StringLength(128, MinimumLength = 1)] public string Username { get; set; } = "";
    [JsonPropertyName("password"), Required, StringLength(256, MinimumLength = 1)] public string Password { get; set; } = "";
}

public sealed class ExternalChangeControlRequest
{
    public static readonly string[] AllowedStatuses = ["UNVERIFIED", "PENDING_APPROVAL", "VERIFIED", "CLOSED", "CANCEL"];

    [JsonPropertyName("cc_number"), Required, StringLength(64, MinimumLength = 3)] public string? cc_number { get; set; }
    [JsonPropertyName("title"), Required, StringLength(256, MinimumLength = 3)] public string? title { get; set; }
    [JsonPropertyName("initiated_on"), Required] public DateOnly? initiated_on { get; set; }
    [JsonPropertyName("remarks"), StringLength(4000)] public string? remarks { get; set; }
    [JsonPropertyName("applicability"), Required] public string? applicability { get; set; }
    [JsonPropertyName("plant_reference")] public long? plant_reference { get; set; }
    [JsonPropertyName("verified_by")] public int? verified_by { get; set; }
    [JsonPropertyName("change_type"), Required] public string? change_type { get; set; }
    [JsonPropertyName("description"), Required, StringLength(4000, MinimumLength = 3)] public string? description { get; set; }
    [JsonPropertyName("reason_for_change"), Required, StringLength(4000, MinimumLength = 3)] public string? reason_for_change { get; set; }
    [JsonPropertyName("impact_assessment"), Required, StringLength(4000, MinimumLength = 3)] public string? impact_assessment { get; set; }
    [JsonPropertyName("requires_action_plan")] public bool requires_action_plan { get; set; }
    [JsonPropertyName("status"), StringLength(32)] public string? status { get; set; }
}

public sealed class ExternalDocumentDto
{
    [JsonPropertyName("document_id")] public long DocumentId { get; set; }
    [JsonPropertyName("document_number")] public string? DocumentNumber { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("type_code")] public string? TypeCode { get; set; }
    [JsonPropertyName("type_name")] public string? TypeName { get; set; }
    [JsonPropertyName("department")] public string? Department { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("is_obsolete")] public bool IsObsolete { get; set; }
    [JsonPropertyName("effective_version_id")] public long? EffectiveVersionId { get; set; }
    [JsonPropertyName("effective_version_no")] public string? EffectiveVersionNo { get; set; }
    [JsonPropertyName("effective_from")] public string? EffectiveFrom { get; set; }
    [JsonPropertyName("current_version_id")] public long? CurrentVersionId { get; set; }
    [JsonPropertyName("current_version_no")] public string? CurrentVersionNo { get; set; }
    [JsonPropertyName("current_state")] public string? CurrentState { get; set; }
    [JsonPropertyName("revision_in_progress")] public bool RevisionInProgress { get; set; }
    [JsonPropertyName("viewable_version_id")] public long? ViewableVersionId { get; set; }
    [JsonPropertyName("document_url")] public string? DocumentUrl { get; set; }
}

public sealed class ExternalDocumentViewModel(string documentNumber, string title, string revision, string state, DateTime? effectiveFrom)
{
    public string DocumentNumber { get; } = documentNumber;
    public string Title { get; } = title;
    public string Revision { get; } = revision;
    public string State { get; } = state;
    public DateTime? EffectiveFrom { get; } = effectiveFrom;
    public string PdfUrl { get; set; } = "#";
}
