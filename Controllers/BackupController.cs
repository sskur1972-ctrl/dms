//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Backups: history (backup_log) and a manual run through pg_dump (Services/PgBackupRunner). */
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class BackupController : DmsBaseController
    {
        private readonly PgBackupRunner _runner;

        public BackupController(IUnitOfWork unitOfWork, PgBackupRunner runner) : base(unitOfWork)
        {
            _runner = runner;
        }

        public async Task<IActionResult> Index()
        {
            if (!HasPermission(AdminPermissions.Backup)) return NoPermission();
            return View(await _unitOfWork.BackupLog.GetListAsync());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Run(string backupType, CancellationToken ct)
        {
            if (!HasPermission(AdminPermissions.Backup)) return NoPermission();
            var result = await _runner.RunAsync(string.IsNullOrWhiteSpace(backupType) ? "COMBINED" : backupType,
                                                "MANUAL", CurrentUserId, ClientIp, ct);
            Notify(result, "The backup has completed. Verify it against the restore runbook before relying on it.");
            return RedirectToAction(nameof(Index));
        }
    }
}
