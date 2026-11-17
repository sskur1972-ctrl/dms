using System.Security.Claims;
using System.Text.Encodings.Web;
using DMS.DataAccess.Repository.IRepository;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DMS.Web.Services;

// Reuses the existing External HS256 JWT contract and PTPL-encrypted secret.
// No separate JWT issuer, key or token format is introduced.
public sealed class DmsBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, ExternalIntegrationSecurity security, IConfiguration configuration,
    IUnitOfWork unitOfWork) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (string.IsNullOrWhiteSpace(Request.Headers.Authorization)) return AuthenticateResult.NoResult();
        if (!security.TryGetAuthorizedUserId(Request.Headers.Authorization, out var userId))
            return AuthenticateResult.Fail("Invalid bearer token.");
        try
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, Scheme.Name);
            var principal = await CommonAccountPrincipal.RefreshAsync(configuration, unitOfWork,
                new ClaimsPrincipal(identity), userId, Context.RequestAborted);
            if (principal is null) return AuthenticateResult.Fail("Account is unavailable.");
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Bearer account validation failed; rejecting the request.");
            return AuthenticateResult.Fail("Account validation failed.");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Response.WriteAsJsonAsync(new { success = false, message = "Unauthorized." }, Context.RequestAborted);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { success = false, message = "Forbidden." }, Context.RequestAborted);
    }
}
