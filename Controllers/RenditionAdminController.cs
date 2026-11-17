//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 4 - Administration > PDF Rendition: is the converter there, what is in the queue, retry a failed job. */
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.Services.Rendition;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class RenditionAdminController : DmsBaseController
    {
        private readonly RenditionProcessor _processor;

        public RenditionAdminController(IUnitOfWork unitOfWork, RenditionProcessor processor) : base(unitOfWork) => _processor = processor;

        private bool Allowed => HasPermission(DocumentsController.RenditionPermission) || HasPermission("Manage system configuration");

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!Allowed) return NoPermission();
            ViewBag.converter = await _processor.ConverterStatusAsync();
            return View(await _unitOfWork.Rendition.GetJobsAsync(200));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Retry(long id)
        {
            if (!Allowed) return NoPermission();
            var result = await _unitOfWork.Rendition.RetryAsync(id, CurrentUserId);
            Notify(result, result.Message);
            if (result.Success) RenditionWorker.Nudge();
            return RedirectToAction(nameof(Index));
        }

        /// <summary>Process the queue now (without waiting for the worker's next pass).</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunNow()
        {
            if (!Allowed) return NoPermission();
            var n = await _processor.RunBatchAsync(20, HttpContext.RequestAborted);
            NotifySuccess(n == 0 ? "Nothing was waiting in the queue." : $"{n} job(s) processed.");
            return RedirectToAction(nameof(Index));
        }
    }
}
