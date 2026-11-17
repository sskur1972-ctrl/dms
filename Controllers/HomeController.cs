//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5a - the dashboard (ported from Bcl.Dms DashboardService): my tasks, drafts, periodic reviews due,
 * print requests to authorise, controlled copies to retrieve, documents recently made effective. */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Common;
using DMS.Model.Documents;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace DMS.Web.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<HomeController> _logger;

        public HomeController(IUnitOfWork unitOfWork, ILogger<HomeController> logger)
        {
            _unitOfWork = unitOfWork; _logger = logger;
        }

        private bool HasPermission(string p)
            => (User.Claims.FirstOrDefault(c => c.Type == "serializedPermissionList")?.Value ?? "").Contains("\"" + p + "\"");

        public async Task<IActionResult> Index()
        {

            var model = new DashboardViewModel();
            if (int.TryParse(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value, out var userId) && HasPermission(DocumentPermissions.View))
            {
                try
                {
                    model.Counts = await _unitOfWork.Scheduler.GetDashboardCountsAsync(userId) ?? new DashboardCounts();
                    model.ReviewsDue = await _unitOfWork.Scheduler.GetReviewsDueAsync(userId);
                    model.RecentEffective = await _unitOfWork.Scheduler.GetRecentEffectiveAsync(30);
                    if (HasPermission(PrintPermissions.Authorize) && model.Counts.print_to_authorize > 0)
                        model.PrintToAuthorize = (await _unitOfWork.Print.GetRequestsAsync("PENDING", null, null, 10))
                                                 .Where(r => r.requested_by != userId).ToList();
                    if (HasPermission(CompliancePermissions.AuditReview))
                        model.Audit = await _unitOfWork.Compliance.GetAuditStatusAsync();
                    if (HasPermission(PrintPermissions.Issue) && model.Counts.copies_to_retrieve > 0)
                        model.CopiesToRetrieve = (await _unitOfWork.Print.GetCopiesToRetrieveAsync()).Take(10).ToList();
                }
                catch (Npgsql.PostgresException ex)
                {
                    // the database is not upgraded yet (DatabaseUpgrader reports why) - show the plain dashboard
                    _logger.LogWarning("Dashboard figures unavailable: {Message}", ex.MessageText);
                    ViewBag.dashboardProblem = ex.MessageText;
                }
            }
            return View(model);
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
