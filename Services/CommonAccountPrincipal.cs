using System.Security.Claims;
using Dapper;
using DMS.DataAccess.Repository.IRepository;
using Newtonsoft.Json;
using Npgsql;

namespace DMS.Web.Services;

// Both authentication methods resolve identity and permissions from the same live account.
public static class CommonAccountPrincipal
{
    public static async Task<ClaimsPrincipal?> RefreshAsync(IConfiguration configuration, IUnitOfWork unitOfWork,
        ClaimsPrincipal principal, int userId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("DMSConnection"));
        var account = await connection.QuerySingleOrDefaultAsync<CommonAccountCookieEvents.SessionAccount>(
            new CommandDefinition(CommonAccountCookieEvents.AccountSql, new { domainUserId = userId }, cancellationToken: cancellationToken));
        if (account is null || !account.Active || !account.RoleActive || account.MustChangePassword || account.DomainRoleId is null)
            return null;
        var permissions = await unitOfWork.Role.getPermissionsForUser(userId);
        var identity = new ClaimsIdentity(principal.Identity as ClaimsIdentity);
        void Set(string type, string value)
        {
            foreach (var claim in identity.FindAll(type).ToArray()) identity.RemoveClaim(claim);
            identity.AddClaim(new Claim(type, value));
        }
        Set("UserID", account.CommonUserId.ToString());
        Set(ClaimTypes.NameIdentifier, account.CommonUserId.ToString());
        Set("IdentitySource", "common-v1");
        Set("CommonUserID", account.CommonUserId.ToString());
        Set("roles", account.DomainRoleId.Value.ToString());
        Set("role_name", account.RoleName ?? "");
        Set(ClaimTypes.Role, account.RoleName ?? "");
        Set("displayName", account.DisplayName ?? account.UserName ?? "");
        Set(ClaimTypes.Name, account.Email ?? account.UserName ?? "");
        Set("DepartmentId", (account.DepartmentId ?? 0).ToString());
        Set("MustChangePassword", "0");
        Set("serializedPermissionList", JsonConvert.SerializeObject(permissions));
        return new ClaimsPrincipal(identity);
    }
}
