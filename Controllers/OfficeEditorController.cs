//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Administration > Office Editor - the Collabora / WOPI preflight (old Admin/OfficeEditor).
 * A WOPI integration fails silently (a grey frame), so every check that can be made from this server is made here.
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.CommonUtils;
using DMS.Web.Services.Office;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class OfficeEditorController : DmsBaseController
    {
        private readonly IWopiPreflightService _preflight;

        public OfficeEditorController(IUnitOfWork unitOfWork, IWopiPreflightService preflight) : base(unitOfWork)
        {
            _preflight = preflight;
        }

        public async Task<IActionResult> Providers(CancellationToken ct) {
            if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
            return View(await HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.DocumentEditor.EditorHealthService>().RunAsync(ct));
        }
        public async Task<IActionResult> Index(CancellationToken ct)
        {
            if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
            return View(await _preflight.RunAsync(ct));
        }
    }
}
