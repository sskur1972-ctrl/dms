//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Document type master and document numbering rules. */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class DocumentTypeController : DmsBaseController
    {
        public DocumentTypeController(IUnitOfWork unitOfWork) : base(unitOfWork) { }

        public async Task<IActionResult> Index()
        {
            if (!HasPermission(AdminPermissions.DocumentType) && !HasPermission(AdminPermissions.Numbering)) return NoPermission();
            ViewBag.canEdit = HasPermission(AdminPermissions.DocumentType);
            ViewBag.canNumbering = HasPermission(AdminPermissions.Numbering);
            return View(await _unitOfWork.DocumentType.GetListAsync(true));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(long id = 0)
        {
            if (!HasPermission(AdminPermissions.DocumentType)) return NoPermission();
            var model = id > 0 ? await _unitOfWork.DocumentType.GetByIdAsync(id) : new DocumentType();
            if (model == null)
            {
                NotifyError("Document type not found.");
                return RedirectToAction(nameof(Index));
            }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(DocumentType model, string? reason)
        {
            if (!HasPermission(AdminPermissions.DocumentType)) return NoPermission();
            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                return View(model);
            }
            var result = await _unitOfWork.DocumentType.SaveAsync(model, reason, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            if (!result.Success) return View(model);
            return RedirectToAction(nameof(Index));
        }

        // ---------------- numbering rules ----------------

        public async Task<IActionResult> NumberingRules(long? documentTypeId)
        {
            if (!HasPermission(AdminPermissions.Numbering)) return NoPermission();
            ViewBag.types = await _unitOfWork.DocumentType.GetListAsync(true);
            ViewBag.documentTypeId = documentTypeId;
            return View(await _unitOfWork.DocumentType.GetNumberingRulesAsync(documentTypeId));
        }

        [HttpGet]
        public async Task<IActionResult> NumberingRuleEdit(long id = 0, long? documentTypeId = null)
        {
            if (!HasPermission(AdminPermissions.Numbering)) return NoPermission();
            NumberingRule? model;
            if (id > 0)
            {
                model = (await _unitOfWork.DocumentType.GetNumberingRulesAsync(null)).FirstOrDefault(r => r.id == id);
                if (model == null)
                {
                    NotifyError("Numbering rule not found.");
                    return RedirectToAction(nameof(NumberingRules));
                }
            }
            else
            {
                model = new NumberingRule { document_type_id = documentTypeId ?? 0 };
                if (documentTypeId.HasValue)
                {
                    var type = await _unitOfWork.DocumentType.GetByIdAsync(documentTypeId.Value);
                    model.prefix = type?.code;
                }
            }
            await LoadNumberingLookups();
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> NumberingRuleEdit(NumberingRule model, string? reason)
        {
            if (!HasPermission(AdminPermissions.Numbering)) return NoPermission();
            var result = await _unitOfWork.DocumentType.SaveNumberingRuleAsync(model, reason, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            if (!result.Success)
            {
                await LoadNumberingLookups();
                return View(model);
            }
            return RedirectToAction(nameof(NumberingRules), new { documentTypeId = model.document_type_id });
        }

        private async Task LoadNumberingLookups()
        {
            ViewBag.types = await _unitOfWork.DocumentType.GetListAsync(true);
            ViewBag.departments = await _unitOfWork.UserService.GetDepartmentsForDropDown();
        }
    }
}
