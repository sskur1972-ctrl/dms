//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Workflow configuration: review / approval / authorization stages per document type
 * and the document state transitions. Every change is a GxP configuration change: reason required.
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class WorkflowController : DmsBaseController
    {
        public WorkflowController(IUnitOfWork unitOfWork) : base(unitOfWork) { }

        public async Task<IActionResult> Index(long? documentTypeId)
        {
            if (!HasPermission(AdminPermissions.Workflow)) return NoPermission();
            var types = await _unitOfWork.DocumentType.GetListAsync(true);
            var selected = types.FirstOrDefault(t => t.id == documentTypeId) ?? types.FirstOrDefault();
            ViewBag.types = types;
            ViewBag.selectedType = selected;
            var stages = selected == null ? new List<WorkflowStage>() : await _unitOfWork.WorkflowConfig.GetStagesAsync(selected.id);
            return View(stages);
        }

        [HttpGet]
        public async Task<IActionResult> EditStage(long id = 0, long documentTypeId = 0)
        {
            if (!HasPermission(AdminPermissions.Workflow)) return NoPermission();
            WorkflowStage? model;
            if (id > 0)
            {
                model = (await _unitOfWork.WorkflowConfig.GetStagesAsync(documentTypeId)).FirstOrDefault(s => s.id == id);
                if (model == null)
                {
                    NotifyError("Stage not found.");
                    return RedirectToAction(nameof(Index), new { documentTypeId });
                }
            }
            else
            {
                var existing = await _unitOfWork.WorkflowConfig.GetStagesAsync(documentTypeId);
                model = new WorkflowStage
                {
                    document_type_id = documentTypeId,
                    stage_no = existing.Count == 0 ? 1 : existing.Max(s => s.stage_no) + 1
                };
            }
            await LoadStageLookups(model.document_type_id);
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> EditStage(WorkflowStage model, string? reason)
        {
            if (!HasPermission(AdminPermissions.Workflow)) return NoPermission();
            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                await LoadStageLookups(model.document_type_id);
                return View(model);
            }
            var result = await _unitOfWork.WorkflowConfig.SaveStageAsync(model, reason, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            if (!result.Success)
            {
                await LoadStageLookups(model.document_type_id);
                return View(model);
            }
            return RedirectToAction(nameof(Index), new { documentTypeId = model.document_type_id });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteStage(long id, long documentTypeId, string? reason)
        {
            if (!HasPermission(AdminPermissions.Workflow)) return NoPermission();
            var result = await _unitOfWork.WorkflowConfig.DeleteStageAsync(id, reason, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Index), new { documentTypeId });
        }

        // ---------------- state transitions ----------------

        public async Task<IActionResult> Transitions()
        {
            if (!HasPermission(AdminPermissions.Workflow)) return NoPermission();
            return View(await _unitOfWork.WorkflowConfig.GetTransitionsAsync());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetTransitionActive(long id, bool isActive, string? reason)
        {
            if (!HasPermission(AdminPermissions.Workflow)) return NoPermission();
            var result = await _unitOfWork.WorkflowConfig.SetTransitionActiveAsync(id, isActive, reason, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Transitions));
        }

        public async Task<IActionResult> States()
        {
            if (!HasPermission(AdminPermissions.Workflow)) return NoPermission();
            return View(await _unitOfWork.WorkflowConfig.GetStatesAsync());
        }

        private async Task LoadStageLookups(long documentTypeId)
        {
            ViewBag.documentType = await _unitOfWork.DocumentType.GetByIdAsync(documentTypeId);
            ViewBag.states = (await _unitOfWork.WorkflowConfig.GetStatesAsync()).Where(s => s.is_active).ToList();
            ViewBag.roles = await _unitOfWork.Role.GetRoleListForDropDown();
            ViewBag.users = await _unitOfWork.UserService.GetUserListForIndex();
            ViewBag.departments = await _unitOfWork.UserService.GetDepartmentsForDropDown();
        }
    }
}
