using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PTPLCRYPTORENGINE;

namespace DMS.Web.Services;

/// <summary>Validates External HS256 bearer tokens and signs stable, version-pinned view links.</summary>
public sealed class ExternalIntegrationSecurity(IConfiguration configuration, ILogger<ExternalIntegrationSecurity> logger)
{
    private const string Issuer = "UnifiedPlatform.External";
    private const string Audience = "UnifiedPlatform.Clients";
    private const long ClockSkewSeconds = 30;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    public bool IsAuthorized(string? authorizationHeader)
        => TryGetAuthorizedUserId(authorizationHeader, out _);

    public bool TryGetAuthorizedUserId(string? authorizationHeader, out int userId)
    {
        userId = 0;
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !TryGetKey(out var key)) return false;

        var token = authorizationHeader[7..].Trim();
        if (token.Length > 16_384) return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        try
        {
            using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
            if (header.RootElement.ValueKind != JsonValueKind.Object ||
                !header.RootElement.TryGetProperty("alg", out var alg) ||
                alg.ValueKind != JsonValueKind.String || alg.GetString() != "HS256") return false;
            using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
            var claims = payload.RootElement;
            if (claims.ValueKind != JsonValueKind.Object ||
                !claims.TryGetProperty("iss", out var issuer) || issuer.ValueKind != JsonValueKind.String || issuer.GetString() != Issuer ||
                !claims.TryGetProperty("aud", out var audience) || !HasAudience(audience) ||
                !claims.TryGetProperty("exp", out var expiration) || !expiration.TryGetInt64(out var exp) ||
                !claims.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String ||
                !int.TryParse(subject.GetString(), out var subjectUserId) || subjectUserId <= 0) return false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (exp <= now - ClockSkewSeconds) return false;
            if (claims.TryGetProperty("nbf", out var notBefore) &&
                (!notBefore.TryGetInt64(out var nbf) || nbf > now + ClockSkewSeconds)) return false;

            using var hmac = new HMACSHA256(key);
            var expected = hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            var supplied = FromBase64Url(parts[2]);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) return false;
            userId = subjectUserId;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueUserToken(int userId)
    {
        if (userId <= 0 || !TryGetKey(out var key))
            throw new InvalidOperationException("External token signing is not configured.");

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(TokenLifetime);
        var header = ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds()
        }));
        var signingInput = header + "." + payload;
        using var hmac = new HMACSHA256(key);
        var signature = ToBase64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput)));
        return (signingInput + "." + signature, expiresAt);
    }

    public string SignDocumentLink(long documentId, long versionId, int userId)
    {
        if (userId <= 0 || !TryGetKey(out var key)) throw new InvalidOperationException("External integration is not configured.");
        using var hmac = new HMACSHA256(key);
        return ToBase64Url(hmac.ComputeHash(LinkPayload(documentId, versionId, userId)));
    }

    /// <summary>Returns a stable opaque token for one managed External share-link generation.</summary>
    public string CreateExternalShareToken(long documentId, int generation)
    {
        if (documentId <= 0 || generation < 0 || !TryGetKey(out var key))
            throw new InvalidOperationException("External integration is not configured.");
        using var hmac = new HMACSHA256(key);
        var payload = string.Concat("external-share-link:v1:", documentId, ":", generation);
        return ToBase64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    public static string HashShareToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();

    public bool IsValidDocumentLink(long documentId, long versionId, int userId, string? signature)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(signature) || !TryGetKey(out var key)) return false;
        try
        {
            using var hmac = new HMACSHA256(key);
            var expected = hmac.ComputeHash(LinkPayload(documentId, versionId, userId));
            var supplied = FromBase64Url(signature);
            return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    private bool TryGetKey(out byte[] key)
    {
        key = [];
        var encryptedSecret = configuration["JwtSettings:Secret"];
        var cipherKey = configuration["Cipherkey"];
        if (string.IsNullOrWhiteSpace(encryptedSecret) || string.IsNullOrWhiteSpace(cipherKey)) return false;

        try
        {
            var secret = AESCryptor.Decrypt(encryptedSecret, cipherKey);
            if (string.IsNullOrWhiteSpace(secret)) return false;
            key = Encoding.UTF8.GetBytes(secret);
            return key.Length >= 32;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The External JWT secret could not be decrypted. Check the configured Cipherkey.");
            key = [];
            return false;
        }
    }

    private static bool HasAudience(JsonElement audience) => audience.ValueKind switch
    {
        JsonValueKind.String => audience.GetString() == Audience,
        JsonValueKind.Array => audience.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == Audience),
        _ => false
    };

    private static byte[] LinkPayload(long documentId, long versionId, int userId)
        => Encoding.ASCII.GetBytes($"external-document-view:v2:{documentId}:{versionId}:{userId}");

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }

    private static string ToBase64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
