//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5b - URS-7.22 bulk import of legacy masters with a retrospective electronic signature.
 * Ported from Bcl.Dms ILegacyImportService / LegacyImportViewModel. The work is done by Services/Legacy/LegacyImportService. */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Model.Legacy;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.Legacy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace DMS.Web.Controllers
{
    public class LegacyImportController : DmsBaseController
    {
        private readonly LegacyImportService _import;
        private readonly IConfigService _config;
        private readonly ILogger<LegacyImportController> _logger;

        public LegacyImportController(
            IUnitOfWork unitOfWork,
            LegacyImportService import,
            IConfigService config,
            ILogger<LegacyImportController> logger)
            : base(unitOfWork)
        {
            _import = import;
            _config = config;
            _logger = logger;
        }

        private bool Allowed =>
            HasPermission(CompliancePermissions.LegacyImport);

        // ============================================================
        // EXISTING BULK IMPORT
        // ============================================================

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (!Allowed) return NoPermission();

            ViewBag.batches =
                await _unitOfWork.Compliance.GetLegacyBatchesAsync(50);

            ViewBag.meaning =
                await _config.GetStringAsync(
                    "legacy.import.signatureMeaning",
                    "Approved by");

            ViewBag.maxMb =
                await _config.GetIntAsync(
                    "legacy.import.maxPackageMb",
                    500);

            ViewBag.userName =
                (await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))
                ?.username ?? "";

            return View(new LegacyImportForm());
        }

        [HttpGet]
        public IActionResult Template()
        {
            if (!Allowed) return NoPermission();

            return File(
                System.Text.Encoding.UTF8
                    .GetPreamble()
                    .Concat(
                        System.Text.Encoding.UTF8.GetBytes(
                            LegacyImportService.ManifestTemplate))
                    .ToArray(),
                "text/csv; charset=utf-8",
                "manifest.csv");
        }

        [HttpPost, ValidateAntiForgeryToken,
  RequestSizeLimit(2_147_483_647),
  RequestFormLimits(
      MultipartBodyLengthLimit = 2_147_483_647)]
        public async Task<IActionResult> Run(
     LegacyImportForm form,
     IFormFile? package)
        {
            if (!Allowed)
                return NoPermission();

            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                return RedirectToAction(nameof(Index));
            }

            var maxMb = await _config.GetIntAsync(
                "legacy.import.maxPackageMb",
                500);

            if (package == null ||
                package.Length == 0 ||
                !package.FileName.EndsWith(
                    ".zip",
                    StringComparison.OrdinalIgnoreCase))
            {
                NotifyError(
                    "Choose the .zip package (manifest.csv and the master files).");

                return RedirectToAction(nameof(Index));
            }

            if (package.Length > (long)maxMb * 1024 * 1024)
            {
                NotifyError(
                    $"The package is larger than {maxMb} MB (legacy.import.maxPackageMb). Split it into smaller batches.");

                return RedirectToAction(nameof(Index));
            }

            // Validate access scope and determine the user's plant.
            var scope = form.AccessScope?.Trim().ToUpperInvariant();

            if (scope != "PLANT" && scope != "GLOBAL")
            {
                NotifyError("Select Plant or Global access scope.");
                return RedirectToAction(nameof(Index));
            }

            int? plantId = null;

            if (scope == "PLANT")
            {
                if (!int.TryParse(
                        User.FindFirst("PlantId")?.Value,
                        out var parsedPlantId)
                    || parsedPlantId <= 0)
                {
                    NotifyError(
                        "Your account does not have a valid plant assignment.");

                    return RedirectToAction(nameof(Index));
                }

                plantId = parsedPlantId;
            }

            // Ensure the normalized scope is passed to the service.
            form.AccessScope = scope;

            var signed = false;

            if (!form.DryRun)
            {
                if (!form.MeaningAcknowledged)
                {
                    NotifyError(
                        "Confirm the meaning of the retrospective signature you are applying.");

                    return RedirectToAction(nameof(Index));
                }

                (signed, var error) = await VerifySignerAsync(
                    form.SignatureUserName,
                    form.SignaturePassword);

                if (error != null || !signed)
                {
                    NotifyError(
                        error ??
                        "Enter your user ID and password to apply the retrospective signature.");

                    return RedirectToAction(nameof(Index));
                }
            }

            try
            {
                await using var stream = package.OpenReadStream();

                var batchId = await _import.RunAsync(
                    stream,
                    package.FileName,
                    form,
                    signed,
                    CurrentUserId,
                    plantId,
                    ClientIp,
                    HttpContext.RequestAborted);

                var batch = (await _unitOfWork.Compliance
                        .GetLegacyBatchesAsync(50))
                    .FirstOrDefault(b => b.id == batchId);

                var text = form.DryRun
                    ? $"Validation of {batch?.batch_reference}: {batch?.total_rows} rows, {batch?.failed_rows} with problems. Nothing was imported."
                    : $"Import {batch?.batch_reference}: {batch?.imported_rows} of {batch?.total_rows} masters imported, {batch?.failed_rows} not imported.";

                if (batch?.failed_rows > 0)
                    NotifyError(text);
                else
                    NotifySuccess(text);

                return RedirectToAction(
                    nameof(Batch),
                    new { id = batchId });
            }
            catch (LegacyImportException ex)
            {
                NotifyError(ex.Message);
                return RedirectToAction(nameof(Index));
            }
        }

        // ============================================================
        // SINGLE LEGACY DOCUMENT IMPORT
        // ============================================================

        [HttpGet]
        public async Task<IActionResult> SingleImport()
        {
            await LoadSingleImportViewDataAsync();

            var userName = (await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))?.username ?? "";

            return View(new SingleLegacyImportForm
            {
                DryRun = true,
                SignatureUserName = userName
            });
        }

        //private async Task LoadSingleImportViewDataAsync()
        //{
        //    ViewBag.meaning =
        //        await _config.GetStringAsync(
        //            "legacy.import.signatureMeaning",
        //            "Approved by");

        //    ViewBag.maxMb =
        //        await _config.GetIntAsync(
        //            "legacy.import.maxPackageMb",
        //            500);

        //    ViewBag.userName =
        //        (await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))
        //        ?.username ?? "";

        //    // Document Type dropdown
        //    ViewBag.DocumentTypes =
        //        await _unitOfWork.DocumentType.GetListAsync(false);

        //    // Department dropdown
        //    ViewBag.Departments =
        //        await _unitOfWork.UserService.GetDepartmentsForDropDown();

        //    // Owner dropdown - populate after confirming the user-list method
        //    ViewBag.Owners = await _unitOfWork.UserService.GetUsersForDropDown();
        //}
        private async Task LoadSingleImportViewDataAsync()
        {
            var documentTypes =
                await _unitOfWork.DocumentType.GetListAsync(false);

            ViewBag.DocumentTypes = documentTypes
         .Select(t => new SelectListItem
         {
             Value = t.code ?? "",
             Text = $"{t.code} - {t.name}"
         })
         .ToList();

            var departments =
                await _unitOfWork.UserService.GetDepartmentsForDropDown();

            //ViewBag.Departments = departments
            //    .Select(d => new SelectListItem
            //    {
            //        Value = d.id.ToString(),
            //        Text = d.DisplayText

            //    })
            //    .ToList();
            ViewBag.Departments = departments
    .Where(d => !string.IsNullOrWhiteSpace(d.code))
    .Select(d => new SelectListItem
    {
        Value = d.code!,
        Text = d.DisplayText
    })
    .ToList();

            var users =
                await _unitOfWork.UserService.GetUserListForIndex();
            ViewBag.Owners = users
                .Where(u => !string.IsNullOrWhiteSpace(u.username))
                .Select(u => new SelectListItem
                {
                    Value = u.username!,
                    Text = !string.IsNullOrWhiteSpace(u.DisplayName)
                        ? u.DisplayName
                        : u.username!
                })
                .ToList();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SingleImport(
            SingleLegacyImportForm form,
            IFormFile? document)
        {
            await LoadSingleImportViewDataAsync();

            // Get the logged-in user's display name for the signature.
            var userName =
                (await _unitOfWork.Workflow.GetSignerAsync(CurrentUserId))
                ?.username ?? "";

            form.SignatureUserName = userName;

            // Validate the form.
            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                return View(form);
            }

            // Validate the selected access scope.
            var scope = form.AccessScope?.Trim().ToUpperInvariant();

            if (scope != "PLANT" && scope != "GLOBAL")
            {
                ModelState.AddModelError(
                    nameof(form.AccessScope),
                    "Select Plant or Global access scope.");

                NotifyError("Select Plant or Global access scope.");
                return View(form);
            }

            // Plant ID comes from the authenticated user's claim.
            int? plantId = null;

            if (scope == "PLANT")
            {
                if (!int.TryParse(
                        User.FindFirst("PlantId")?.Value,
                        out var parsedPlantId)
                    || parsedPlantId <= 0)
                {
                    ModelState.AddModelError(
                        nameof(form.AccessScope),
                        "Your account does not have a valid plant assignment.");

                    NotifyError(
                        "Your account does not have a valid plant assignment.");

                    return View(form);
                }

                plantId = parsedPlantId;
            }

            form.AccessScope = scope;

            // Validate the uploaded document.
            var maxMb = await _config.GetIntAsync(
                "legacy.import.maxPackageMb", 500);

            if (document == null || document.Length == 0)
            {
                NotifyError("Choose a document to import.");
                return View(form);
            }

            if (document.Length > (long)maxMb * 1024 * 1024)
            {
                NotifyError($"The document is larger than {maxMb} MB.");
                return View(form);
            }

            var extension = Path.GetExtension(document.FileName);

            if (!string.Equals(extension, ".doc", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
            {
                NotifyError("Only .doc and .docx files are supported.");
                return View(form);
            }

            // Actual imports require retrospective signature verification.
            var signed = false;

            if (!form.DryRun)
            {
                if (!form.MeaningAcknowledged)
                {
                    NotifyError(
                        "Confirm the meaning of the retrospective signature.");

                    return View(form);
                }

                (signed, var error) = await VerifySignerAsync(
                    form.SignatureUserName,
                    form.SignaturePassword);

                if (error != null || !signed)
                {
                    NotifyError(
                        error ?? "Enter valid signature credentials.");

                    return View(form);
                }
            }

            try
            {
                await using var stream = document.OpenReadStream();

                var batchId = await _import.RunSingleAsync(
     stream,
     document.FileName,
     document.Length,
     form,
     signed,
     CurrentUserId,
     plantId,
     ClientIp,
     HttpContext.RequestAborted);

                var batch = (await _unitOfWork.Compliance
                        .GetLegacyBatchesAsync(50))
                    .FirstOrDefault(b => b.id == batchId);

                if (batch?.failed_rows > 0)
                {
                    NotifyError(
                        $"Single-document import completed with problems. Batch: {batch.batch_reference}.");
                }
                else if (form.DryRun)
                {
                    NotifySuccess(
                        $"Single-document validation completed. Batch: {batch?.batch_reference}. No document was imported.");
                }
                else
                {
                    NotifySuccess(
                        $"Single-document import completed. Batch: {batch?.batch_reference}.");
                }

                return RedirectToAction(nameof(Batch), new { id = batchId });
            }
            catch (LegacyImportException ex)
            {
                NotifyError(ex.Message);
                return View(form);
            }
        }


        // ============================================================
        // EXISTING BATCH DETAILS
        // ============================================================

        [HttpGet]
        public async Task<IActionResult> Batch(long id)
        {
            if (!Allowed) return NoPermission();

            var batch =
                (await _unitOfWork.Compliance
                    .GetLegacyBatchesAsync(500))
                .FirstOrDefault(b => b.id == id);

            if (batch == null)
                return NotFound();

            ViewBag.batch = batch;

            return View(
                await _unitOfWork.Compliance
                    .GetLegacyBatchRowsAsync(id));
        }
    }
}