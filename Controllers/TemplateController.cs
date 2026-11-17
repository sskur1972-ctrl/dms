//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 3a - Document templates (old Admin/Templates, URS-7.1).
 * A template is the controlled blank an author starts a document from. It is never deleted:
 * withdrawing it stops new use, and documents already created from it stay traceable.
 * Editor (in-application) templates come with the document editor in Phase 4.
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.DocumentEditor;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class TemplateController : DmsBaseController
    {
        private readonly FileStore _fileStore;

        public TemplateController(IUnitOfWork unitOfWork, FileStore fileStore) : base(unitOfWork)
        {
            _fileStore = fileStore;
        }

        public async Task<IActionResult> Index(bool withdrawn = false)
        {
            if (!HasPermission(AdminPermissions.Template)) return NoPermission();
            ViewBag.withdrawn = withdrawn;
            ViewBag.types = await _unitOfWork.DocumentType.GetListAsync(false);
            ViewBag.departments = await _unitOfWork.UserService.GetDepartmentsForDropDown();
            return View(await _unitOfWork.Template.GetListAsync(withdrawn));
        }

        [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(52_428_800)]
        public async Task<IActionResult> Upload(long documentTypeId, long? departmentId, string? name, string? templateVersion,
                                                DateTime? effectiveFrom, string? reason, IFormFile? file, CancellationToken ct)
        {
            if (!HasPermission(AdminPermissions.Template)) return NoPermission();
            if (file == null || file.Length == 0)
            {
                NotifyError("Choose the template file to upload.");
                return RedirectToAction(nameof(Index));
            }
            if (string.IsNullOrWhiteSpace(reason))
            {
                NotifyError("A reason is required: templates are controlled GxP content.");
                return RedirectToAction(nameof(Index));
            }

            Model.Documents.StoredFile stored;
            try
            {
                stored = await _fileStore.SaveUploadAsync(file, "templates", ct);
            }
            catch (FileStoreException ex)
            {
                NotifyError(ex.Message);
                return RedirectToAction(nameof(Index));
            }

            var result = await _unitOfWork.Template.AddAsync(documentTypeId, departmentId is > 0 ? departmentId : null,
                (name ?? "").Trim(), string.IsNullOrWhiteSpace(templateVersion) ? "01" : templateVersion.Trim(),
                effectiveFrom, stored, reason, CurrentUserId, ClientIp);
            if (!result.Success) _fileStore.Archive(stored.RelativePath);   // nothing refers to it - keep it out of the store
            Notify(result, result.Message);
            return result.Success && DMS.Web.Services.Office.WopiService.IsEditableFile(stored.FileName)
                ? RedirectToAction(nameof(Content),new{id=result.Id}) : RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Supersede(long id, string? reason)
        {
            if (!HasPermission(AdminPermissions.Template)) return NoPermission();
            var result = await _unitOfWork.Template.SupersedeAsync(id, reason, CurrentUserId, ClientIp);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Index));
        }

        /// <summary>The template file itself, for QA to check what authors will receive.</summary>
        [HttpGet]
        public async Task<IActionResult> Download(long id)
        {
            if (!HasPermission(AdminPermissions.Template)) return NoPermission();
            var template = await _unitOfWork.Template.GetByIdAsync(id);
            if (template?.storage_path == null) return NotFound();
            try
            {
                return File(await _fileStore.OpenCachedReadAsync(template.storage_path), template.mime_type ?? FileStore.MimeFromExtension(template.file_name ?? ""),
                            template.file_name);
            }
            catch (FileStoreException ex)
            {
                NotifyError(ex.Message);
                return RedirectToAction(nameof(Index));
            }
        }

        // ------------------------------------------------------------------ templates written in the application

        /// <summary>A template with no file: its standard body is laid out in the editor (old TemplateCreateBlank).</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateBlank(long documentTypeId, long? departmentId, string? name, string? templateVersion,
                                                     DateTime? effectiveFrom, string? reason)
        {
            if (!HasPermission(AdminPermissions.Template)) return NoPermission();
            var result = await _unitOfWork.Authoring.CreateBlankTemplateAsync(documentTypeId, departmentId is > 0 ? departmentId : null,
                (name ?? "").Trim(), string.IsNullOrWhiteSpace(templateVersion) ? "01" : templateVersion.Trim(), effectiveFrom, reason,
                CurrentUserId, ClientIp);
            Notify(result, result.Message);
            return result.Success ? RedirectToAction(nameof(Content), new { id = result.Id }) : RedirectToAction(nameof(Index));
        }

        /// <summary>The template's standard body and page setup, in the same editor authors use.</summary>
        [HttpGet]
        public async Task<IActionResult> Content(long id)
        {
            if (!HasPermission(AdminPermissions.Template)) return NoPermission();
            var template = await _unitOfWork.Authoring.GetTemplateContentAsync(id);
            if (template == null) return NotFound();
            if (!template.is_current)
            {
                NotifyError("This template version has been withdrawn. Add a new version rather than editing a withdrawn one.");
                return RedirectToAction(nameof(Index));
            }
            var config = HttpContext.RequestServices.GetRequiredService<IConfigService>();
            if(await UsesOfficeEditorAsync(config))
            {
                var editor=HttpContext.RequestServices.GetRequiredService<IDocumentEditorService>();
                var launch=await editor.OpenAsync(new EditorOpenContext(0,CurrentUserId,true,ClientIp,EditorMode.Edit,
                    $"{Request.Scheme}://{Request.Host}",id),HttpContext.RequestAborted);
                Response.Headers.CacheControl="no-store";
                ViewBag.templateId=id;
                ViewBag.error=launch.Error;
                return View("OfficeContent",launch.Session);
            }
            var authoring = HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.Authoring.DocumentAuthoringService>();
            ViewBag.pageSetupJson = await authoring.TemplatePageSetupJsonAsync(template.settings_json);
            // shown as HTML in the editor: always through the whitelist, whatever is stored
            var sanitizer = HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.Authoring.IEditorHtmlSanitizer>();
            template.content_html = sanitizer.Sanitize(template.content_html, DMS.Web.Services.Authoring.EditorSanitizeLimits.Default).Html;
            ViewBag.maxImageBytes = await config.GetIntAsync(ConfigKeys.AuthoringMaxImageBytes, 2 * 1024 * 1024);
            return View(template);
        }

        [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(33_554_432)]
        public async Task<IActionResult> SaveContent([FromBody] DocumentsController.ComposeSaveRequest request)
        {
            if (!HasPermission(AdminPermissions.Template)) return Json(new { ok = false, message = "You are not permitted to do this." });
            if (request?.TemplateId is not { } templateId) return Json(new { ok = false, message = "The request could not be read." });
            if(await UsesOfficeEditorAsync(HttpContext.RequestServices.GetRequiredService<IConfigService>()))
                return Json(new{ok=false,message="Save the template using the configured Office editor."});
            var authoring = HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.Authoring.DocumentAuthoringService>();
            var result = await authoring.SaveTemplateContentAsync(templateId, request.Html, request.AuthoringMode, request.PageSetup,
                                                                  CurrentUserId, ClientIp);
            return Json(result.Success
                ? new { ok = true, message = (string?)null, fileName = (string?)null }
                : new { ok = false, message = (string?)result.Message, fileName = (string?)null });
        }
        private static async Task<bool> UsesOfficeEditorAsync(IConfigService config) =>
            await config.GetBoolAsync(ConfigKeys.EditorEnabled,true) &&
            !string.Equals(await config.GetStringAsync(ConfigKeys.EditorProvider,"COLLABORA"),"DISABLED",StringComparison.OrdinalIgnoreCase);
    }
}
