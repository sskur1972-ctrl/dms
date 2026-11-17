//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

using DMS.Web.Services;

namespace DMS.Web.Services.Office;

/// <summary>
/// Signs and verifies WOPI access tokens with the application's Data Protection
/// key ring.
///
/// <para><b>Why this is the security boundary of the whole integration</b></para>
/// A WOPI call arrives from the EDITOR SERVER, not from a browser. It carries no
/// cookie, no session and no antiforgery token: the access token in the query
/// string is the entire authorisation. So the token must be
///
/// <list type="bullet">
///   <item>unforgeable - it is signed, and Data Protection's payload is
///         authenticated, so a single altered byte fails to unprotect;</item>
///   <item>unwidenable - the permissions are INSIDE the signed payload, not
///         alongside it, so a caller cannot turn a read-only session into a
///         writable one by editing a parameter;</item>
///   <item>scoped - it names one revision and one user, and the service checks
///         the revision on every call, so a token for a document the user may
///         edit cannot be replayed against one they may not;</item>
///   <item>short-lived - it expires, and expiry is checked here rather than
///         trusted from the caller.</item>
/// </list>
///
/// <para>
/// A purpose string separates these tokens from every other protected payload
/// in the application, so a cookie or an antiforgery token can never be
/// unprotected as a WOPI token or the reverse.
/// </para>
///
/// <para>
/// The key ring is a secret and is already documented as one - see
/// <c>docs/LIMITATIONS.md §18</c>. Losing it invalidates open editor sessions,
/// which is an inconvenience; leaking it would let somebody mint tokens, which
/// is not.
/// </para>
/// </summary>
public sealed class WopiTokenProtector : IWopiTokenProtector
{
    private const string Purpose = "Bcl.Dms.Wopi.AccessToken.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<WopiTokenProtector> _logger;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public WopiTokenProtector(IDataProtectionProvider provider, ILogger<WopiTokenProtector> logger)
    {
        _protector = provider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(WopiTokenDto token)
        => _protector.Protect(JsonSerializer.Serialize(token, Json));

    public WopiTokenDto? Unprotect(string? protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken)) return null;

        string payload;
        try
        {
            payload = _protector.Unprotect(protectedToken);
        }
        catch (Exception ex)
        {
            // Expected in normal operation: an expired browser tab, a token from
            // before a key rotation, or somebody probing the endpoint. Logged at
            // information, not error, so that the log stays readable - but logged,
            // because a burst of them is worth seeing.
            _logger.LogInformation(ex, "A WOPI access token could not be verified and was rejected.");
            return null;
        }

        WopiTokenDto? token;
        try
        {
            token = JsonSerializer.Deserialize<WopiTokenDto>(payload, Json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "A WOPI access token verified but did not deserialise.");
            return null;
        }

        if (token is null) return null;

        // Expiry is enforced here rather than left to the caller, so that no
        // code path can forget to check it.
        if (token.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _logger.LogInformation(
                "A WOPI access token for revision {VersionId} expired at {ExpiresAt:u} and was rejected.",
                token.DocumentVersionId, token.ExpiresAtUtc);

            return null;
        }

        if (token.UserId <= 0 || token.Permissions is null) return null;
        if (token.AttachmentId is not null)
        {
            if (token.AttachmentId <= 0 || token.AttachmentDocumentId is null or <= 0 ||
                token.DocumentVersionId != 0 || token.Permissions.CanWrite ||
                token.Permissions.CanPrint || token.Permissions.CanExport) return null;
        }
        else if (token.DocumentVersionId <= 0 || token.AttachmentDocumentId is not null) return null;

        return token;
    }
}
