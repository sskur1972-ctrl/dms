namespace DMS.Web.Services.Configuration;

public static class InfrastructureSettings
{
    public static string? PathFor(string key) => key.ToLowerInvariant() switch
    {
        "office.editor.enabled" => "DocumentEditor:Enabled",
        "office.editor.provider" or "office.wopi.editor" => "DocumentEditor:Provider",
        "office.wopi.enabled" => "DocumentEditor:Collabora:Enabled",
        "office.wopi.discoveryurl" => "DocumentEditor:Collabora:DiscoveryUrl",
        "office.wopi.publicbaseurl" => "DocumentEditor:PublicBaseUrl",
        "office.wopi.tokenlifetimeminutes" => "DocumentEditor:SessionMinutes",
        _ => key.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ? key.Replace('.', ':') : null
    };

    public static bool IsFileManaged(string key) =>
        key.StartsWith("office.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("redis.", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("rendition.libreOfficePath", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("rendition.collaboraUrl", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("rendition.provider", StringComparison.OrdinalIgnoreCase);

    public static string ReadPath(string key) => PathFor(key) ?? "Infrastructure:" + key;
}
