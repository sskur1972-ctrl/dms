using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace DMS.Web.Services;

public static class SharedAuthenticationSettings
{
    public static bool IsPortal(IConfiguration configuration) =>
        string.Equals(configuration["SharedAuthentication:Mode"], "Portal", StringComparison.OrdinalIgnoreCase);

    public static string LoginUrl(IConfiguration configuration, string returnUrl) =>
        QueryHelpers.AddQueryString(RequireUrl(configuration, "CommonLoginUrl"), "returnUrl", returnUrl);

    public static string LogoutUrl(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration["SharedAuthentication:CommonLogoutUrl"])
            ? new Uri(new Uri(RequireUrl(configuration, "CommonLoginUrl")), "LogOut").AbsoluteUri
            : RequireUrl(configuration, "CommonLogoutUrl");

    private static string RequireUrl(IConfiguration configuration, string key)
    {
        var value = configuration["SharedAuthentication:" + key];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"SharedAuthentication:{key} requires an absolute HTTP(S) URL.");
        return value!;
    }

    public static void ConfigureCookie(CookieAuthenticationOptions options, IConfiguration configuration,
        string contentRoot, bool development)
    {
        var mode = configuration["SharedAuthentication:Mode"] ?? "Standalone";
        if (!IsPortal(configuration) && !mode.Equals("Standalone", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SharedAuthentication:Mode must be Standalone or Portal.");
        var keyPath = configuration["SharedAuthentication:KeyPath"];
        if (IsPortal(configuration))
        {
            _ = LoginUrl(configuration, "/");
            _ = LogoutUrl(configuration);
            if (string.IsNullOrWhiteSpace(keyPath))
                throw new InvalidOperationException("Portal mode requires SharedAuthentication:KeyPath matching the Common portal.");
        }
        if (string.IsNullOrWhiteSpace(keyPath)) keyPath = Path.Combine(contentRoot, "App_Data", "authentication-keys");
        if (!OperatingSystem.IsWindows() && keyPath.Contains('\\'))
            throw new InvalidOperationException("SharedAuthentication:KeyPath must be a Linux path on this server.");
        keyPath = Path.GetFullPath(keyPath, contentRoot);
        Directory.CreateDirectory(keyPath);
        options.LoginPath = "/Account/Login";
        options.EventsType = typeof(CommonAccountCookieEvents);
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        if (!string.IsNullOrWhiteSpace(configuration["SharedAuthentication:CookieDomain"]))
            options.Cookie.Domain = configuration["SharedAuthentication:CookieDomain"];
        options.Cookie.Name = configuration["SharedAuthentication:CookieName"] ?? ".UnifiedPlatform.SharedAuth";
        options.DataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(keyPath),
            protection => protection.SetApplicationName(configuration["SharedAuthentication:ApplicationName"] ?? "UnifiedPlatform.SharedAuthentication"));
    }
}
