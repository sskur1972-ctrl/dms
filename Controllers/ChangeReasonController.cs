//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Reason for change master. Entries are never deleted: deactivate instead, so history still resolves. */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class ChangeReasonController : DmsBaseController
    {
        public ChangeReasonController(IUnitOfWork unitOfWork) : base(unitOfWork) { }

        public async Task<IActionResult> Index()
        {
            if (!HasPermission(AdminPermissions.ChangeReason)) return NoPermission();
            return View(await _unitOfWork.ChangeReason.GetListAsync(false));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(long id = 0)
        {
            if (!HasPermission(AdminPermissions.ChangeReason)) return NoPermission();
            var model = id > 0
                ? (await _unitOfWork.ChangeReason.GetListAsync(false)).FirstOrDefault(x => x.id == id)
                : new ChangeReason{};
            if (model == null)
            {
                NotifyError("Reason for change not found.");
                return RedirectToAction(nameof(Index));
            }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(ChangeReason model)
        {
            if (!HasPermission(AdminPermissions.ChangeReason)) return NoPermission();
            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                return View(model);
            }
            var result = await _unitOfWork.ChangeReason.SaveAsync(model, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            if (!result.Success) return View(model);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetActive(long id, bool isActive)
        {
            if (!HasPermission(AdminPermissions.ChangeReason)) return NoPermission();
            var result = await _unitOfWork.ChangeReason.SetActiveAsync(id, isActive, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Index));
        }
    }
}
