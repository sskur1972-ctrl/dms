//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Department master (old DMS: departments table, used by users, numbering and workflow). */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class DepartmentController : DmsBaseController
    {
        public DepartmentController(IUnitOfWork unitOfWork) : base(unitOfWork) { }

        public async Task<IActionResult> Index()
        {
            if (!HasPermission(AdminPermissions.Department)) return NoPermission();
            return View(await _unitOfWork.Department.GetListAsync());
        }

        [HttpGet]
        public async Task<IActionResult> Edit(long id = 0)
        {
            if (!HasPermission(AdminPermissions.Department)) return NoPermission();
            var model = id > 0 ? await _unitOfWork.Department.GetByIdAsync(id) : new Department();
            if (model == null)
            {
                NotifyError("Department not found.");
                return RedirectToAction(nameof(Index));
            }
            ViewBag.parents = (await _unitOfWork.Department.GetListAsync()).Where(d => d.id != id && d.is_active).ToList();
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Department model)
        {
            if (!HasPermission(AdminPermissions.Department)) return NoPermission();
            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                ViewBag.parents = (await _unitOfWork.Department.GetListAsync()).Where(d => d.id != model.id && d.is_active).ToList();
                return View(model);
            }
            var result = await _unitOfWork.Department.SaveAsync(model, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            if (!result.Success)
            {
                ViewBag.parents = (await _unitOfWork.Department.GetListAsync()).Where(d => d.id != model.id && d.is_active).ToList();
                return View(model);
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetActive(long id, bool isActive)
        {
            if (!HasPermission(AdminPermissions.Department)) return NoPermission();
            var result = await _unitOfWork.Department.SetActiveAsync(id, isActive, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Index));
        }
    }
}
