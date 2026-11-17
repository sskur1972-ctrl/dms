//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Rejection reason master. Entries are never deleted: deactivate instead, so history still resolves. */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class RejectionReasonController : DmsBaseController
    {
        public RejectionReasonController(IUnitOfWork unitOfWork) : base(unitOfWork) { }

        public async Task<IActionResult> Index(string stage = "REVIEW")
        {
            if (!HasPermission(AdminPermissions.RejectionReason)) return NoPermission();
            stage = NormaliseStage(stage);
            ViewBag.stage = stage;
            return View(await _unitOfWork.RejectionReason.GetListAsync(stage, false));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(long id = 0, string stage = "REVIEW")
        {
            if (!HasPermission(AdminPermissions.RejectionReason)) return NoPermission();
            var model = id > 0
                ? (await _unitOfWork.RejectionReason.GetListAsync(NormaliseStage(stage), false)).FirstOrDefault(x => x.id == id)
                : new RejectionReason{ stage_kind = NormaliseStage(stage) };
            if (model == null)
            {
                NotifyError("Rejection reason not found.");
                return RedirectToAction(nameof(Index), new { stage = NormaliseStage(stage) });
            }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(RejectionReason model)
        {
            if (!HasPermission(AdminPermissions.RejectionReason)) return NoPermission();
            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                return View(model);
            }
            var result = await _unitOfWork.RejectionReason.SaveAsync(model, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            if (!result.Success) return View(model);
            return RedirectToAction(nameof(Index), new { stage = model.stage_kind });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetActive(long id, bool isActive, string stage = "REVIEW")
        {
            if (!HasPermission(AdminPermissions.RejectionReason)) return NoPermission();
            var result = await _unitOfWork.RejectionReason.SetActiveAsync(id, isActive, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Index), new { stage = NormaliseStage(stage) });
        }

        private static string NormaliseStage(string? stage) =>
            string.Equals(stage, "APPROVAL", StringComparison.OrdinalIgnoreCase) ? "APPROVAL" : "REVIEW";
    }
}
