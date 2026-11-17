//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - System configuration (system_config). Keys are seeded; only values change.
 * GxP-relevant keys need a reason for change; secret values are never shown or audited in clear text.
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class SystemConfigController : DmsBaseController
    {
        private readonly PasswordCipher _cipher;
        public SystemConfigController(IUnitOfWork unitOfWork,PasswordCipher cipher) : base(unitOfWork) { _cipher=cipher; }

        public async Task<IActionResult> Index(string? category)
        {
            if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
            var items = await _unitOfWork.SystemConfig.GetListAsync();
            items = items.Where(i => !Services.Configuration.InfrastructureSettings.IsFileManaged(i.config_key ?? "")).ToList();
            items=items.GroupBy(i=>i.config_key?.Trim(),StringComparer.OrdinalIgnoreCase)
                .Select(g=>g.OrderByDescending(i=>i.updated_at).ThenByDescending(i=>i.id).First()).ToList();
            ViewBag.categories = items.Select(i => i.category ?? "").Distinct().OrderBy(c => c).ToList();
            ViewBag.category = category;
            if (!string.IsNullOrWhiteSpace(category)) items = items.Where(i => i.category == category).ToList();
            return View(items);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(string key)
        {
            if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
            if (Services.Configuration.InfrastructureSettings.IsFileManaged(key)) return BadRequest("This infrastructure setting is managed in application.json.");
            var item = (await _unitOfWork.SystemConfig.GetListAsync()).FirstOrDefault(i => i.config_key == key);
            if (item == null)
            {
                NotifyError("Configuration key not found.");
                return RedirectToAction(nameof(Index));
            }
            if (item.IsSecret) item.config_value = string.Empty;
            return View(item);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string key, string? value, string? reason, bool keepSecret = false)
        {
            if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
            if (Services.Configuration.InfrastructureSettings.IsFileManaged(key)) return BadRequest("This infrastructure setting is managed in application.json.");
            var item = (await _unitOfWork.SystemConfig.GetListAsync()).FirstOrDefault(i => i.config_key == key);
            if (item == null) return NotFound();
            if (item.IsSecret && keepSecret)
            {
                NotifySuccess("No change made.");
                return RedirectToAction(nameof(Index), new { category = item.category });
            }
            if (string.Equals(item.value_type, "bool", StringComparison.OrdinalIgnoreCase)) value = (value ?? "false").ToLowerInvariant();

            var storedValue=value?.Trim();
            var result = await _unitOfWork.SystemConfig.SaveAsync(key, storedValue, reason, CurrentUserId, ClientIp);
            Notify(result, $"{key} has been updated.");
            if (!result.Success)
            {
                item.config_value = item.IsSecret ? string.Empty : value;
                return View(item);
            }
            return RedirectToAction(nameof(Index), new { category = item.category });
        }
    }
}
