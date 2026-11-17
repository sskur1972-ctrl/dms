//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Change control register. References are registered UNVERIFIED and QA verifies them.
 * There is no delete and no "unverify": both would rewrite the basis of an approved revision.
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class ChangeControlController : DmsBaseController
    {
        public ChangeControlController(IUnitOfWork unitOfWork) : base(unitOfWork) { }

        public async Task<IActionResult> Index(string? q, string? status)
        {
            if (!HasPermission(AdminPermissions.ChangeControl)) return NoPermission();
            ViewBag.q = q;
            ViewBag.status = status;
            ViewBag.SignerName=(await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))?.username;
            return View(await _unitOfWork.ChangeControl.SearchAsync(q, status));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Register(ChangeControl model)
        {
            // References originate in QAMS; forged form posts cannot create one.
            return StatusCode(403, "Change controls must be imported from QAMS.");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Verify(long id, string? remarks)
        {
            if (!HasPermission(AdminPermissions.ChangeControl)) return NoPermission();
            var result = await _unitOfWork.ChangeControl.VerifyAsync(id, remarks, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Index));
        }
        [HttpGet]
        public async Task<IActionResult> Details(long id){if(!HasPermission(AdminPermissions.ChangeControl))return NoPermission();var c=await _unitOfWork.ChangeControl.GetAsync(id);return c==null?NotFound():View(c);}
        [HttpPost,ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveDetails(ChangeControl model){if(!HasPermission(AdminPermissions.ChangeControl))return NoPermission();var result=await _unitOfWork.ChangeControl.SaveDetailsAsync(model,CurrentUserId,ClientIp);Notify(result,result.Message);return RedirectToAction(nameof(Details),new{id=model.id});}
        [HttpPost,ValidateAntiForgeryToken]
        public async Task<IActionResult> Sign(long id,string action,string? remarks,string? signatureUserName,string? signaturePassword,bool meaningAcknowledged)
        {
            if(!HasPermission(AdminPermissions.ChangeControl))return NoPermission();
            if(action is not ("VERIFY" or "APPROVE")){NotifyError("Unknown verification action.");return RedirectToAction(nameof(Index));}
            var (signed,error)=await VerifySignerAsync(signatureUserName,signaturePassword);
            if(error!=null){NotifyError(error);return RedirectToAction(nameof(Index));}
            var result=await _unitOfWork.ChangeControl.SignAsync(id,action,remarks,signed,meaningAcknowledged,CurrentUserId,ClientIp);
            Notify(result,result.Message);return RedirectToAction(nameof(Index));
        }
    }
}
