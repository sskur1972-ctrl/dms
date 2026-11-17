//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Printer master. Entries are never deleted: deactivate instead, so history still resolves. */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class PrinterController : DmsBaseController
    {
        public PrinterController(IUnitOfWork unitOfWork) : base(unitOfWork) { }

        public async Task<IActionResult> Index()
        {
            if (!HasPermission(AdminPermissions.Printer)) return NoPermission();
            return View(await _unitOfWork.Printer.GetListAsync(false));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(long id = 0)
        {
            if (!HasPermission(AdminPermissions.Printer)) return NoPermission();
            var model = id > 0
                ? (await _unitOfWork.Printer.GetListAsync(false)).FirstOrDefault(x => x.id == id)
                : new Printer{};
            if (model == null)
            {
                NotifyError("Printer not found.");
                return RedirectToAction(nameof(Index));
            }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Printer model)
        {
            if (!HasPermission(AdminPermissions.Printer)) return NoPermission();
            model.ip_address = string.IsNullOrWhiteSpace(model.ip_address) ? null : model.ip_address.Trim();
            if (model.ip_address != null && !IsValidIp(model.ip_address))
                ModelState.AddModelError(nameof(model.ip_address), "Enter a valid IP address (e.g. 10.0.0.25) or leave it blank.");
            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                return View(model);
            }
            var result = await _unitOfWork.Printer.SaveAsync(model, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            if (!result.Success) return View(model);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetActive(long id, bool isActive)
        {
            if (!HasPermission(AdminPermissions.Printer)) return NoPermission();
            var result = await _unitOfWork.Printer.SetActiveAsync(id, isActive, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Index));
        }

        private static bool IsValidIp(string value)
        {
            if (!System.Net.IPAddress.TryParse(value, out var ip)) return false;
            // IPAddress.TryParse accepts "10" or "10.1" - require the full dotted form for IPv4
            return ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || value.Split('.').Length == 4;
        }
    }
}
