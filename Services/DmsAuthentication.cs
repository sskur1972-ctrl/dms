using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DMS.Web.Services;

public static class DmsAuthentication
{
    public const string Selector = "JWT_OR_COOKIE";
    public const string Bearer = "Bearer";

    public static string SelectScheme(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
               request.Path.StartsWithSegments("/api")
            ? Bearer : CookieAuthenticationDefaults.AuthenticationScheme;
    }

    public static IServiceCollection AddDmsAuthentication(this IServiceCollection services,
        IConfiguration configuration, IWebHostEnvironment environment)
    {
        services.AddScoped<CommonAccountCookieEvents>();
        services.AddAuthorization();
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = Selector;
            options.DefaultChallengeScheme = Selector;
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultSignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddPolicyScheme(Selector, "JWT or Cookie Authentication", options =>
            options.ForwardDefaultSelector = context => SelectScheme(context.Request))
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            SharedAuthenticationSettings.ConfigureCookie(options, configuration, environment.ContentRootPath, environment.IsDevelopment()))
        .AddScheme<AuthenticationSchemeOptions, DmsBearerAuthenticationHandler>(Bearer, _ => { });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme).ValidateOnStart();
        return services;
    }
}
