//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - common helpers for the administration controllers
 * (permission check on the serializedPermissionList claim, current user, client IP, notification).
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Npgsql;

namespace DMS.Web.Controllers
{
    [Authorize]
    public abstract class DmsBaseController : Controller
    {
        protected readonly IUnitOfWork _unitOfWork;

        protected DmsBaseController(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork;
        }

        /// <summary>The JSON list of permission names written at login.</summary>
        protected string PermissionList =>
            User.Claims.FirstOrDefault(c => c.Type == "serializedPermissionList")?.Value ?? string.Empty;

        /// <summary>True when the user holds the permission (display name, e.g. "Manage departments").</summary>
        protected bool HasPermission(string permission) => PermissionList.Contains("\"" + permission + "\"");

        protected IActionResult NoPermission() => RedirectToAction("PermissionErrorPage", "AuthErrorPage");

        protected int CurrentUserId =>
            int.TryParse(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value, out var id) ? id : 0;

        protected int CurrentPlantId =>
    int.TryParse(
        User.Claims.FirstOrDefault(c => c.Type == "PlantId")?.Value,
        out var plantId)
        ? plantId
        : 0;

        protected string? ClientIp
        {
            get
            {
                var forwarded = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(forwarded)) return forwarded.Split(',')[0].Trim();
                return HttpContext.Connection.RemoteIpAddress?.ToString();
            }
        }

        protected void NotifySuccess(string message)
        {
            TempData["success"] = message;
            TempData["Valid"] = "1";
        }

        protected void NotifyError(string message)
        {
            TempData["success"] = message;
            TempData["Valid"] = "0";
        }

        /// <summary>Shows the success text, or the reason the database refused the change.</summary>
        protected void Notify(DbResult result, string successMessage)
        {
            if (result.Success) NotifySuccess(successMessage);
            else NotifyError(string.IsNullOrWhiteSpace(result.Message) ? "The change was not saved." : result.Message);
        }

        /// <summary>
        /// A missing database function / table means a Database script was not run on the database in
        /// appsettings.json (DMSConnection). Say so on the dashboard instead of showing a stack trace.
        /// </summary>
        public override void OnActionExecuted(ActionExecutedContext context)
        {
            if (!context.ExceptionHandled && FindPostgresException(context.Exception) is { } pg
                && (pg.SqlState == PostgresErrorCodes.UndefinedFunction || pg.SqlState == PostgresErrorCodes.UndefinedTable
                    || pg.SqlState == PostgresErrorCodes.UndefinedColumn))
            {
                NotifyError("The database is not up to date: " + pg.MessageText + ". " +
                            (DMS.Web.Services.DatabaseUpgrader.Problem is { } problem
                                ? "Start-up upgrade failed - " + problem
                                : "Restart the application so it can apply the missing Database script, or run it manually and check with Database/99_verify_install.sql."));
                context.Result = RedirectToAction("Index", "Home");
                context.ExceptionHandled = true;
            }
            base.OnActionExecuted(context);
        }

        private static PostgresException? FindPostgresException(Exception? ex)
        {
            while (ex != null)
            {
                if (ex is PostgresException pg) return pg;
                ex = ex.InnerException;
            }
            return null;
        }

        /// <summary>
        /// ESignatureService.VerifyCredentialsAsync (21 CFR 11.200): the signer re-enters his OWN user ID and password.
        /// No password = no signature offered (the database then refuses a step that needs one).
        /// </summary>
        protected async Task<(bool Signed, string? Error)> VerifySignerAsync(string? userName, string? password)
        {
            if (string.IsNullOrEmpty(password)) return (false, null);
            if (string.IsNullOrWhiteSpace(userName))
                return (false, "Enter your user ID and password to apply your electronic signature.");

            var signer = await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId);
            if (signer == null || !signer.isactive || signer.isarchived)
                return (false, "The user ID or password is incorrect. The signature was not applied.");
            if (!string.Equals(userName.Trim(), signer.username, StringComparison.OrdinalIgnoreCase))
                return (false, "The credentials entered belong to a different user. Only you can sign your own step.");

            var passwordCrypto = HttpContext.RequestServices.GetRequiredService<DMS.Web.CommonUtils.PasswordCipher>();
            if (!passwordCrypto.Verify(password, signer.password))
            {
                HttpContext.RequestServices.GetRequiredService<ILogger<DmsBaseController>>()
                    .LogWarning("E-signature refused for user {UserId}: wrong password", signer.pk_user);
                return (false, "The user ID or password is incorrect. The signature was not applied.");
            }
            return (true, null);
        }

        /// <summary>First model-state error, used when a posted form fails validation.</summary>
        protected string FirstModelError() =>
            ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            ?? "Please correct the highlighted fields.";
    }
}
