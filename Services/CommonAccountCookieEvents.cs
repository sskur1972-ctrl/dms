using DMS.DataAccess.Repository.IRepository;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DMS.Web.Services;

// Shared common tables determine access on every authenticated request.
// Identity IDs are the canonical common IDs. Legacy cookies must reauthenticate.
public sealed class CommonAccountCookieEvents(
    IConfiguration configuration, IUnitOfWork unitOfWork,
    ILogger<CommonAccountCookieEvents> logger) : CookieAuthenticationEvents
{
    public const string AccountSql = """
        SELECT c.pk_user AS CommonUserId, c.role_id AS CommonRoleId,
               c.isactive=1 AND coalesce(c.isarchived,0)=0 AS Active,
               coalesce(r.isactive,0)=1 AND coalesce(r.isarchived,0)=0 AS RoleActive,
               r.role_name AS RoleName, c.role_id AS DomainRoleId,
               c.displayname AS DisplayName, c.emailid AS Email, c.username AS UserName,
               c.fk_department_id AS DepartmentId,
               coalesce(c.isfirstlogin,false) AS MustChangePassword
        FROM common.users c
        LEFT JOIN common.roles r ON r.roleid=c.role_id
        WHERE c.pk_user=@domainUserId
        """;

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        if (string.Equals(configuration["SharedAuthentication:Mode"], "Portal", StringComparison.OrdinalIgnoreCase))
        {
            var currentUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            context.Response.Redirect(SharedAuthenticationSettings.LoginUrl(configuration, currentUrl));
        }
        else
        {
            var currentPath = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            context.Response.Redirect($"{context.Request.PathBase}/Account/Login?returnUrl={Uri.EscapeDataString(currentPath)}");
        }
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.FindFirst("IdentitySource")?.Value != "common-v1" ||
            !int.TryParse(context.Principal?.FindFirst("UserID")?.Value, out var domainUserId))
        {
            await Reject(context);
            return;
        }
        try
        {
            var principal = await CommonAccountPrincipal.RefreshAsync(configuration, unitOfWork,
                context.Principal!, domainUserId, context.HttpContext.RequestAborted);
            if (principal is null) { await Reject(context); return; }
            context.ReplacePrincipal(principal);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Shared account validation failed; rejecting the session.");
            await Reject(context);
        }
    }

    private static async Task Reject(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }

    public sealed class SessionAccount
    {
        public int CommonUserId { get; set; }
        public int? CommonRoleId { get; set; }
        public bool Active { get; set; }
        public bool RoleActive { get; set; }
        public int? DomainRoleId { get; set; }
        public string? RoleName { get; set; }
        public string? DisplayName { get; set; }
        public string? Email { get; set; }
        public string? UserName { get; set; }
        public int? DepartmentId { get; set; }
        public bool MustChangePassword { get; set; }
    }
}
