//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5a - Administration > Scheduler: the last runs of the scheduled jobs, the e-mail queue, run a job now. */
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.Scheduler;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class SchedulerController : DmsBaseController
    {
        private readonly SchedulerJobs _jobs;
        private readonly IConfigService _config;

        public SchedulerController(IUnitOfWork unitOfWork, SchedulerJobs jobs, IConfigService config) : base(unitOfWork)
        {
            _jobs = jobs; _config = config;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
            ViewBag.summary = await _unitOfWork.Scheduler.GetEmailSummaryAsync();
            ViewBag.enabled = await _config.GetBoolAsync(ConfigKeys.SchedulerEnabled, true);
            ViewBag.interval = await _config.GetIntAsync(ConfigKeys.SchedulerIntervalMinutes, 5);
            ViewBag.emailEnabled = await _config.GetBoolAsync(ConfigKeys.EmailEnabled, false);
            ViewBag.smtpHost = await _config.GetStringAsync(ConfigKeys.SmtpHost, null);
            ViewBag.lastPass = SchedulerWorker.LastPassUtc;
            return View(await _unitOfWork.Scheduler.GetRunsAsync(200));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunNow(string? job)
        {
            if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
            if (!string.IsNullOrEmpty(job) && !SchedulerJobs.JobCodes.Contains(job)) return BadRequest();
            var results = await _jobs.RunAsync(string.IsNullOrEmpty(job) ? null : job, HttpContext.RequestAborted);
            var text = string.Join(" · ", results.Select(r => $"{r.Job}: {r.Status}, {r.Items} item(s)" + (string.IsNullOrEmpty(r.Message) ? "" : $" ({r.Message})")));
            if (results.All(r => r.Status == "OK")) NotifySuccess(text); else NotifyError(text);
            if (string.IsNullOrEmpty(job))
                await _unitOfWork.Scheduler.LogRunAsync("MANUAL_PASS", DateTime.UtcNow, "OK", results.Sum(r => r.Items), "Run from Administration > Scheduler by user " + CurrentUserId);
            return RedirectToAction(nameof(Index));
        }
    }
}
