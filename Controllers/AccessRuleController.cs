//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Document access matrix: what each role may do with a document in each state
 * (per document type, or for all types when no type is chosen).
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class AccessRuleController : DmsBaseController
    {
        public AccessRuleController(IUnitOfWork unitOfWork) : base(unitOfWork) { }

        public async Task<IActionResult> Index(long? documentTypeId, int? roleId)
        {
            if (!HasPermission(AdminPermissions.AccessMatrix)) return NoPermission();
            ViewBag.types = await _unitOfWork.DocumentType.GetListAsync(true);
            ViewBag.roles = await _unitOfWork.Role.GetRoleListForDropDown();
            ViewBag.documentTypeId = documentTypeId;
            ViewBag.roleId = roleId;
            return View(await _unitOfWork.AccessRule.GetRulesAsync(documentTypeId, roleId));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int roleId = 0, long stateId = 0, long? documentTypeId = null)
        {
            if (!HasPermission(AdminPermissions.AccessMatrix)) return NoPermission();
            AccessRule model = new AccessRule { role_id = roleId, state_id = stateId, document_type_id = documentTypeId };
            if (roleId > 0 && stateId > 0)
            {
                var existing = (await _unitOfWork.AccessRule.GetRulesAsync(documentTypeId, roleId))
                    .FirstOrDefault(r => r.state_id == stateId && r.document_type_id == documentTypeId);
                if (existing != null) model = existing;
            }
            await LoadLookups();
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(AccessRule model, string? reason)
        {
            if (!HasPermission(AdminPermissions.AccessMatrix)) return NoPermission();
            if (model.role_id <= 0 || model.state_id is null or <= 0)
            {
                NotifyError("Select the role and the document state.");
                await LoadLookups();
                return View(model);
            }
            var result = await _unitOfWork.AccessRule.SaveAsync(model, reason, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            if (!result.Success)
            {
                await LoadLookups();
                return View(model);
            }
            return RedirectToAction(nameof(Index), new { documentTypeId = model.document_type_id, roleId = (int)model.role_id });
        }

        private async Task LoadLookups()
        {
            ViewBag.types = await _unitOfWork.DocumentType.GetListAsync(true);
            ViewBag.roles = await _unitOfWork.Role.GetRoleListForDropDown();
            ViewBag.states = (await _unitOfWork.WorkflowConfig.GetStatesAsync()).Where(s => s.is_active).ToList();
        }
    }
}
