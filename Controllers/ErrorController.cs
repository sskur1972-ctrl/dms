using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class ErrorController : Controller
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public ErrorController(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }
        public IActionResult Index()
        {
            return View();
        }
        public IActionResult PageNotFound()
        {
            return View();
        }
        public IActionResult Unauthorized()
        {
            return View();
        }
        public IActionResult BackToLogin()
        {
            _httpContextAccessor.HttpContext.Session.Remove("username");
            return RedirectToAction("Login", "Account", new { area = "" });

        }
        public IActionResult InternalServerException()
        {
            _httpContextAccessor.HttpContext.Session.Remove("username");
            return RedirectToAction("Index", "Home", new { area = "" });
        }
    }
}
