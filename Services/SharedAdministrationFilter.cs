using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DMS.Web.Services;

// Shared administration owns these operations. Block posted URLs as well as UI links.
public sealed class SharedAdministrationFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var controller = context.RouteData.Values["controller"]?.ToString();
        var action = context.RouteData.Values["action"]?.ToString();
        if (controller is "User" or "Roles" or "Permissions" ||
            controller == "Account" && action is "ForgotPassword" or "ChangePassword" or "ResetPassword" or "ConfrimOldpassword")
            context.Result = new NotFoundResult();
    }
    public void OnActionExecuted(ActionExecutedContext context) { }
}
