//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 3a - Documents: register / search, create (upload / template / empty draft), details,
 * draft edit, attachments, on-screen reading.
 * Ported from Bcl.Dms DocumentsController + DocumentService. The workflow actions (submit, review,
 * approve, authorize, publish with e-signatures) follow in Phase 3b; the PDF rendition, watermarked
 * reading and the in-application editor in Phase 4.
 *
 * Rights: the screen permission (View documents / Create document / Edit draft content / Manage attachments)
 * AND the access matrix for the document's type and state (sp_document_access, URS-7.16).
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Web.Caching;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.Office;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Text;
    using DMS.Web.Services;
using DMS.Web.Services.Rendition;


namespace DMS.Web.Controllers
{
    public partial class DocumentsController : DmsBaseController
    {
        private readonly EuroOfficePdfConverter _pdfConverter;

        private readonly IUnitOfWork _unitOfWork;
        //private readonly FileStore _fileStore;
        private readonly IDmsDocumentCache _documentCache;

        private const long MaxUploadBytes = 104_857_600;
        private static readonly string[] AttachmentTypes = { "REFERENCE", "ANNEXURE", "FORMAT", "SUPPORTING", "OTHER" };

        private readonly FileStore _fileStore;
        private readonly ILogger<DocumentsController> _logger;

        public DocumentsController(IUnitOfWork unitOfWork, FileStore fileStore, IDmsDocumentCache documentCache,  EuroOfficePdfConverter pdfConverter, ILogger<DocumentsController> logger) : base(unitOfWork)
        {
            _fileStore = fileStore;
            _logger = logger;
            _unitOfWork = unitOfWork;
            _fileStore = fileStore;
            _documentCache = documentCache;
                _pdfConverter = pdfConverter;

            
        }

        // ------------------------------------------------------------------ register / search

        [HttpGet]
        public async Task<IActionResult> Index([FromQuery] DocumentSearchQuery query)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            ViewBag.query = query;
            ViewBag.types = await _unitOfWork.DocumentType.GetListAsync(true);
            ViewBag.departments = await _unitOfWork.UserService.GetDepartmentsForDropDown();
            ViewBag.states = await _unitOfWork.WorkflowConfig.GetStatesAsync();
            ViewBag.canCreate = HasPermission(DocumentPermissions.Create);
            ViewBag.canTrends = HasPermission(CompliancePermissions.ReportView)
                && await HttpContext.RequestServices.GetRequiredService<IConfigService>().GetBoolAsync("ai.trends.enabled", true);
            return View(await _unitOfWork.Document.SearchAsync(query, CurrentUserId));
        }

        [HttpGet]
        public async Task<IActionResult> Lookup(string? q, long? exclude)
        {
            if (!HasPermission(DocumentPermissions.View)) return Forbid();
            var items = await _unitOfWork.Document.LookupAsync(q, exclude);
            return Json(items.Select(i => new { i.id, text = $"{i.document_number} - {i.title} ({i.state_code})" }));
        }

        // ------------------------------------------------------------------ create (URS-7.1 / 7.2)

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (!HasPermission(DocumentPermissions.Create)) return NoPermission();
            var me = (await _unitOfWork.UserService.GetUsersById(CurrentUserId)).FirstOrDefault();
            await LoadCreateLookups();
            return View(new DocumentCreateViewModel { DepartmentId = me?.fk_department_id ?? 0 });
        }

        [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(MaxUploadBytes)]
        //public async Task<IActionResult> Create(DocumentCreateViewModel model, IFormFile? sourceFile, CancellationToken ct)
        //{
        //    if (!HasPermission(DocumentPermissions.Create)) return NoPermission();

        //    async Task<IActionResult> Again(string message)
        //    {
        //        NotifyError(message);
        //        await LoadCreateLookups();
        //        return View(model);
        //    }

        //    if (!ModelState.IsValid) return await Again(FirstModelError());
        //    if (!await ValidChangeControl(model.ChangeControlNumber)) return await Again("Select an active registered Change Control number.");
        //    // four routes, as in the old DMS (AuthoringChoices) + "create now, upload later"
        //    var choice = (model.AuthoringChoice ?? "").ToUpperInvariant();
        //    if (choice == "TEMPLATE") choice = "TEMPLATE_FILE";
        //    if (choice is not ("UPLOAD" or "TEMPLATE_FILE" or "WRITE" or "SCRATCH" or "LATER"))
        //        return await Again("Choose how this document will be written.");
        //    var opensEditor = choice is "WRITE" or "SCRATCH";

        //    // checked before any file is stored: the matrix rule of the initial state (URS-7.16)
        //    var access = await _unitOfWork.Document.GetCreateAccessAsync(model.DocumentTypeId, CurrentUserId);
        //    if (!access.can_create) return await Again("Your access rights do not permit creating a document of this type.");

        //    if (opensEditor)
        //    {
        //        if (!await Authoring.IsEnabledAsync())
        //            return await Again("The in-application editor is switched off. Upload the authored file instead.");
        //        if (!access.can_author) return await Again("Your access rights do not permit authoring document content.");
        //        if (sourceFile is { Length: > 0 })
        //            return await Again("Choose either to write the document in the application or to upload a file, not both.");
        //    }

        //    StoredFile? stored = null;
        //    long? templateId = null;
        //    try
        //    {
        //        if (choice == "UPLOAD")
        //        {
        //            if (sourceFile is not { Length: > 0 }) return await Again("Choose the document file to upload.");
        //            if (!access.can_upload) return await Again("Your access rights do not permit uploading a document file.");
        //            stored = await _fileStore.SaveUploadAsync(sourceFile, "documents", ct);
        //        }
        //        else if (choice is "TEMPLATE_FILE" or "WRITE")
        //        {
        //            if (sourceFile is { Length: > 0 }) return await Again("Choose either a template or a file to upload, not both.");
        //            var template = model.TemplateId is { } tid ? await _unitOfWork.Template.GetByIdAsync(tid) : null;
        //            if (template == null) return await Again("Select the template to start from.");
        //            if (!template.is_current) return await Again($"Template '{template.name}' v{template.template_version} has been withdrawn.");
        //            if (template.document_type_id != model.DocumentTypeId)
        //                return await Again($"Template '{template.name}' belongs to document type {template.document_type_code} and cannot be used for the type selected.");
        //            if (template.department_id is { } td && td != model.DepartmentId)
        //                return await Again($"Template '{template.name}' is restricted to another department.");

        //            if (choice == "WRITE")
        //            {
        //                // Preserve Office template layout, headers and tables without an HTML round trip.
        //                var body = await _unitOfWork.Authoring.GetTemplateContentAsync(template.id);
        //                if(!string.IsNullOrWhiteSpace(template.storage_path) && WopiService.IsEditableFile(template.file_name)
        //                    && await DocumentEditor.IsAvailableAsync())
        //                    stored=await _fileStore.CopyAsync(template.storage_path,"documents",
        //                        $"{FileStore.SafeFileName(model.Title)}-00{Path.GetExtension(template.file_name)}",ct);
        //                else if (body == null || string.IsNullOrWhiteSpace(body.content_html) || body.authoring_mode is not ("EDITOR" or "BOTH"))
        //                    return await Again($"Template '{template.name}' has no body to open in the editor. Ask QA to lay one out " +
        //                                       "under Administration → Document Templates, or start from the template file instead.");
        //            }
        //            else
        //            {
        //                if (string.IsNullOrWhiteSpace(template.storage_path) || template.authoring_mode == "EDITOR")
        //                    return await Again($"Template '{template.name}' is an editor template and has no file to copy. " +
        //                                       "Choose “Write it in the application”.");
        //                // an independent copy: the document never changes the template
        //                stored = await _fileStore.CopyAsync(template.storage_path, "documents",
        //                    $"{FileStore.SafeFileName(model.Title)}-00{Path.GetExtension(template.file_name)}", ct);
        //            }
        //            templateId = template.id;
        //        }
        //    }
        //    catch (FileStoreException ex)
        //    {
        //        return await Again(ex.Message);
        //    }

        //    var result = await _unitOfWork.Document.CreateAsync(new DocumentCreateRequest
        //    {
        //        DocumentTypeId = model.DocumentTypeId,
        //        DepartmentId = model.DepartmentId,
        //        Title = model.Title,
        //        ManualDocumentNumber = model.ManualDocumentNumber,
        //        MasterFormatNumber = model.MasterFormatNumber,
        //        Keywords = model.Keywords,
        //        Summary = model.Summary,
        //        TrainingRequired = model.TrainingRequired,
        //        ChangeControlNumber = model.ChangeControlNumber,
        //        TemplateId = templateId,
        //        ReferencedDocumentIds = model.ReferencedDocumentIds.Distinct().ToArray(),
        //        File = stored
        //    }, CurrentUserId, ClientIp);

        //    if (!result.Success)
        //    {
        //        if (stored != null) _fileStore.Archive(stored.RelativePath);   // never referenced - keep it out of the store
        //        return await Again(result.Message);
        //    }

        //    if (opensEditor)
        //    {
        //        var created = await _unitOfWork.Document.GetByIdAsync(result.Id);
        //        if (created?.current_version_id is { } versionId)
        //        {
        //            // outside the creation transaction: a template body that fails to copy must not lose the draft
        //            if (choice == "WRITE" && templateId is { } seedFrom)
        //            {
        //                try { await Authoring.SeedFromTemplateAsync(result.Id, versionId, seedFrom, CurrentUserId); }
        //                catch (Exception ex) { _logger.LogWarning(ex, "Document {Id} created but the template body was not copied.", result.Id); }
        //            }
        //            NotifySuccess("The draft has been created. Write the document below and save - the Word file is produced from what you write.");
        //            return RedirectToAction(nameof(Compose), new { versionId });
        //        }
        //    }

        //    NotifySuccess(stored == null
        //        ? "The draft has been created. Upload its file with Edit, or write it in the application, then submit it for review."
        //        : "The draft has been created. Check the content, then submit it for review.");
        //    return RedirectToAction(nameof(Details), new { id = result.Id });
        //}

       
  public async Task<IActionResult> Create(
    DocumentCreateViewModel model,
    IFormFile? sourceFile,
    CancellationToken ct)
        {
            if (!HasPermission(DocumentPermissions.Create))
                return NoPermission();

            async Task<IActionResult> Again(string message)
            {
                NotifyError(message);
                await LoadCreateLookups();
                return View(model);
            }

            if (!ModelState.IsValid)
                return await Again(FirstModelError());

            if (!await ValidChangeControl(model.ChangeControlNumber))
                return await Again("Select an active registered Change Control number.");

            // ------------------------------------------------------------
            // Access Scope
            // ------------------------------------------------------------

            var accessScope = (model.AccessScope ?? "")
                .Trim()
                .ToUpperInvariant();

            if (accessScope != "PLANT" && accessScope != "GLOBAL")
                return await Again("Select a valid Access Scope.");

            // ------------------------------------------------------------
            // Get logged-in user's Plant ID
            //
            // Plant ID comes from common.users through GetUsersById().
            // We do NOT trust a Plant ID coming from the browser.
            // ------------------------------------------------------------

            var currentUser = await _unitOfWork.UserService
                .GetUsersById(CurrentUserId);

            var userPlantId = currentUser
                .FirstOrDefault()?
                .plant_id;

            // PLANT documents require the logged-in user
            // to have a Plant ID mapped.
            if (accessScope == "PLANT" && userPlantId == null)
                return await Again("Your user account is not mapped to a plant.");

            // ------------------------------------------------------------
            // Determine document Plant ID
            //
            // PLANT  -> logged-in user's Plant ID
            // GLOBAL -> NULL
            // ------------------------------------------------------------

            int? documentPlantId = accessScope == "PLANT"
                ? userPlantId
                : null;


            // ------------------------------------------------------------
            // Authoring choice
            // ------------------------------------------------------------

            var choice = (model.AuthoringChoice ?? "").ToUpperInvariant();

            if (choice == "TEMPLATE")
                choice = "TEMPLATE_FILE";

            if (choice is not (
                "UPLOAD" or
                "TEMPLATE_FILE" or
                "WRITE" or
                "SCRATCH" or
                "LATER"))
            {
                return await Again("Choose how this document will be written.");
            }

            var opensEditor = choice is "WRITE" or "SCRATCH";


            // ------------------------------------------------------------
            // Check document creation access
            // ------------------------------------------------------------

            var access = await _unitOfWork.Document
                .GetCreateAccessAsync(model.DocumentTypeId, CurrentUserId);

            if (!access.can_create)
            {
                return await Again(
                    "Your access rights do not permit creating a document of this type.");
            }


            // ------------------------------------------------------------
            // Authoring validation
            // ------------------------------------------------------------

            if (choice == "WRITE")
            {
                if (!await Authoring.IsEnabledAsync())
                {
                    return await Again(
                        "The in-application editor is switched off. Upload the authored file instead.");
                }

                if (!access.can_author)
                {
                    return await Again(
                        "Your access rights do not permit authoring document content.");
                }

                if (sourceFile is { Length: > 0 })
                {
                    return await Again(
                        "Choose either to write the document in the application or to upload a file, not both.");
                }
            }

            if ((choice is "SCRATCH" or "LATER") && !new[] { "DOCX", "XLSX" }.Contains((model.ScratchFormat ?? "").ToUpperInvariant()))
                return await Again("Choose Word or Excel for the empty draft.");
            if (choice == "SCRATCH")
            {
                if (!access.can_author || !HasPermission(WopiService.EditInOfficePermission))
                    return await Again("Your access rights do not permit editing an empty Office document.");
                if (!await DocumentEditor.IsAvailableAsync())
                    return await Again("The configured Office editor is unavailable. Check Administration → Office Editor before creating an empty draft.");
                if (sourceFile is { Length: > 0 })
                    return await Again("Choose either an empty draft or an uploaded file, not both.");
            }


            StoredFile? stored = null;
            long? templateId = null;


            // ------------------------------------------------------------
            // File / Template processing
            // ------------------------------------------------------------

            try
            {
                // --------------------------------------------------------
                // Upload
                // --------------------------------------------------------

                if (choice == "UPLOAD")
                {
                    if (sourceFile is not { Length: > 0 })
                        return await Again("Choose the document file to upload.");

                    if (!access.can_upload)
                        return await Again(
                            "Your access rights do not permit uploading a document file.");

                    if (Path.GetExtension(sourceFile.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        await using var pdfStream = sourceFile.OpenReadStream();
                        byte[] docxBytes = await _pdfConverter.ConvertToDocxAsync(pdfStream, sourceFile.FileName, 60, ct);

                        var docxStream = new MemoryStream(docxBytes);
                        var docxFileName = Path.ChangeExtension(sourceFile.FileName, ".docx");

                        sourceFile = new FormFile(docxStream, 0, docxBytes.Length, sourceFile.Name, docxFileName)
                        {
                            Headers = new HeaderDictionary(),
                            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                        };
                    }

                    if (!await IsSupportedDocumentUpload(sourceFile))
                        return await Again("Only valid Word (.docx), Excel (.xlsx), and PDF (.pdf) files are supported.");

                    stored = await _fileStore.SaveUploadAsync(
                        sourceFile,
                        "documents",
                        ct);
                }


                // --------------------------------------------------------
                // Template file / Write
                // --------------------------------------------------------

                else if (choice is "TEMPLATE_FILE" or "WRITE")
                {
                    if (sourceFile is { Length: > 0 })
                    {
                        return await Again(
                            "Choose either a template or a file to upload, not both.");
                    }

                    var template = model.TemplateId is { } tid
                        ? await _unitOfWork.Template.GetByIdAsync(tid)
                        : null;

                    if (template == null)
                        return await Again("Select the template to start from.");

                    if (!template.is_current)
                    {
                        return await Again(
                            $"Template '{template.name}' v{template.template_version} has been withdrawn.");
                    }

                    if (template.document_type_id != model.DocumentTypeId)
                    {
                        return await Again(
                            $"Template '{template.name}' belongs to document type " +
                            $"{template.document_type_code} and cannot be used for the type selected.");
                    }

                    if (template.department_id is { } td &&
                        td != model.DepartmentId)
                    {
                        return await Again(
                            $"Template '{template.name}' is restricted to another department.");
                    }

                    if (choice == "TEMPLATE_FILE" && !IsSupportedOfficeExtension(template.file_name))
                        return await Again("Only Word (.docx) and Excel (.xlsx) template files can be used to create a document.");


                    // ----------------------------------------------------
                    // Write
                    // ----------------------------------------------------

                    if (choice == "WRITE")
                    {
                        // Preserve Office template layout, headers and tables
                        // without an HTML round trip.

                        var body = await _unitOfWork.Authoring
                            .GetTemplateContentAsync(template.id);

                        if (!string.IsNullOrWhiteSpace(template.storage_path)
                            && WopiService.IsEditableFile(template.file_name)
                            && await DocumentEditor.IsAvailableAsync())
                        {
                            stored = await _fileStore.CopyAsync(
                                template.storage_path,
                                "documents",
                                $"{FileStore.SafeFileName(model.Title)}-00" +
                                $"{Path.GetExtension(template.file_name)}",
                                ct);
                        }
                        else if (
                            body == null ||
                            string.IsNullOrWhiteSpace(body.content_html) ||
                            body.authoring_mode is not ("EDITOR" or "BOTH"))
                        {
                            return await Again(
                                $"Template '{template.name}' has no body to open in the editor. " +
                                "Ask QA to lay one out under Administration → Document Templates, " +
                                "or start from the template file instead.");
                        }
                    }

                    // ----------------------------------------------------
                    // Template File
                    // ----------------------------------------------------

                    else
                    {
                        if (string.IsNullOrWhiteSpace(template.storage_path) ||
                            template.authoring_mode == "EDITOR")
                        {
                            return await Again(
                                $"Template '{template.name}' is an editor template and has no file to copy. " +
                                "Choose “Write it in the application”.");
                        }

                        // Independent copy:
                        // the document never changes the template.

                        stored = await _fileStore.CopyAsync(
                            template.storage_path,
                            "documents",
                            $"{FileStore.SafeFileName(model.Title)}-00" +
                            $"{Path.GetExtension(template.file_name)}",
                            ct);
                    }

                    templateId = template.id;
                }

                else if (choice is "SCRATCH" or "LATER")
                {
                    var format = (model.ScratchFormat ?? "DOCX").ToUpperInvariant();
                    var extension = format == "XLSX" ? ".xlsx" : ".docx";
                    var mime = format == "XLSX"
                        ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                        : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                    var content = CreateEmptyOfficePackage(format);
                    await using var stream = new MemoryStream(content, writable: false);
                    stored = await _fileStore.SaveAsync(stream,
                        $"{FileStore.SafeFileName(model.Title)}-00{extension}", mime, "documents", ct);
                }
            }
            catch (FileStoreException ex)
            {
                return await Again(ex.Message);
            }


            // ------------------------------------------------------------
            // Create document
            // ------------------------------------------------------------

            var result = await _unitOfWork.Document.CreateAsync(
                new DocumentCreateRequest
                {
                    DocumentTypeId = model.DocumentTypeId,
                    DepartmentId = model.DepartmentId,
                    Title = model.Title,
                    ManualDocumentNumber = model.ManualDocumentNumber,
                    MasterFormatNumber = model.MasterFormatNumber,
                    Keywords = model.Keywords,
                    Summary = model.Summary,
                    TrainingRequired = model.TrainingRequired,
                    ChangeControlNumber = model.ChangeControlNumber,
                    TemplateId = templateId,

                    ReferencedDocumentIds = model.ReferencedDocumentIds
                        .Distinct()
                        .ToArray(),

                    File = stored,

                    // ----------------------------------------------------
                    // Access Scope
                    // ----------------------------------------------------

                    AccessScope = accessScope,

                    // PLANT  -> logged-in user's Plant ID
                    // GLOBAL -> NULL
                    PlantId = documentPlantId
                },
                CurrentUserId,
                ClientIp);


            // ------------------------------------------------------------
            // Handle creation failure
            // ------------------------------------------------------------

            if (!result.Success)
            {
                if (stored != null)
                    _fileStore.Archive(stored.RelativePath);

                return await Again(result.Message);
            }


            // ------------------------------------------------------------
            // Open editor after creation
            // ------------------------------------------------------------

            if (opensEditor)
            {
                var created = await _unitOfWork.Document
                    .GetByIdAsync(result.Id);

                if (created?.current_version_id is { } versionId)
                {
                    // Outside the creation transaction:
                    // a template body that fails to copy must not lose the draft.

                    if (choice == "WRITE" && templateId is { } seedFrom)
                    {
                        try
                        {
                            await Authoring.SeedFromTemplateAsync(
                                result.Id,
                                versionId,
                                seedFrom,
                                CurrentUserId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Document {Id} created but the template body was not copied.",
                                result.Id);
                        }
                    }

                    if (choice == "SCRATCH")
                    {
                        NotifySuccess($"The empty {model.ScratchFormat} draft has been created. Add its content in the configured Office editor.");
                        return RedirectToAction(nameof(Editor), new { versionId, mode = DMS.Web.Services.DocumentEditor.EditorMode.Edit });
                    }
                    NotifySuccess("The draft has been created. Write the document below and save - the Word file is produced from what you write.");
                    return RedirectToAction(nameof(Compose), new { versionId });
                }
            }


            // ------------------------------------------------------------
            // Final success message
            // ------------------------------------------------------------

            NotifySuccess(
                stored == null
                    ? "The draft has been created. Upload its file with Edit, or write it in the application, then submit it for review."
                    : "The draft has been created. Check the content, then submit it for review.");

            return RedirectToAction(
                nameof(Details),
                new { id = result.Id });
        }

        private static bool IsSupportedOfficeExtension(string? fileName)
            => Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase)
               || Path.GetExtension(fileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

        private static async Task<bool> IsSupportedDocumentUpload(IFormFile file)
        {
            if (!IsSupportedOfficeExtension(file.FileName) || file.Length < 4) return false;
            try
            {
                await using var stream = file.OpenReadStream();
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Path.GetExtension(file.FileName).Equals(".docx", StringComparison.OrdinalIgnoreCase)
                    ? names.Contains("[Content_Types].xml") && names.Contains("word/document.xml")
                    : names.Contains("[Content_Types].xml") && names.Contains("xl/workbook.xml");
            }
            catch (InvalidDataException) { return false; }
        }

        private static byte[] CreateEmptyOfficePackage(string format)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string name, string xml)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(xml);
                }
                if (format == "XLSX")
                {
                    Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
                    Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                    Add("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                    Add("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                    Add("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData/></worksheet>");
                }
                else
                {
                    Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
                    Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
                    Add("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p/><w:sectPr/></w:body></w:document>");
                }
            }
            return output.ToArray();
        }

        /// <summary>Create form: next number (consumes nothing), rights and templates for the chosen type / department.</summary>
        [HttpGet]
        public async Task<IActionResult> CreateOptions(long documentTypeId, long departmentId)
        {
            if (!HasPermission(DocumentPermissions.Create)) return Forbid();
            var preview = await _unitOfWork.Document.PreviewNumberAsync(documentTypeId, departmentId);
            var access = documentTypeId > 0 ? await _unitOfWork.Document.GetCreateAccessAsync(documentTypeId, CurrentUserId) : new CreateAccess();
            var type = documentTypeId > 0 ? await _unitOfWork.DocumentType.GetByIdAsync(documentTypeId) : null;
            var templates = documentTypeId > 0 ? await _unitOfWork.Template.GetForCreateAsync(documentTypeId, departmentId > 0 ? departmentId : null) : new();
            return Json(new
            {
                preview.is_automatic,
                preview.document_number,
                preview.explanation,
                access.can_create,
                access.can_upload,
                requires_master_format_number = type?.requires_master_format_number ?? false,
                master_format_number_label = type?.master_format_number_label,
                requires_change_control = type?.requires_change_control ?? false,
                requires_training = type?.requires_training ?? false,
                templates = templates.Select(t => new { t.id, text = $"{t.name} v{t.template_version}" + (t.has_file ? $" ({t.file_name})" : ""), t.has_file, has_body = t.has_body || (t.has_file && WopiService.IsEditableFile(t.file_name)) }),
                can_author = access.can_author
            });
        }

        private async Task LoadCreateLookups()
        {
            ViewBag.types = await _unitOfWork.DocumentType.GetListAsync(false);
            ViewBag.departments = await _unitOfWork.UserService.GetDepartmentsForDropDown();
        }

        // ------------------------------------------------------------------ details

        [HttpGet]
        public async Task<IActionResult> Details(long id)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(id);
            if (document == null)
            {
                NotifyError("The document was not found.");
                return RedirectToAction(nameof(Index));
            }
            var access = await _unitOfWork.Document.GetAccessAsync(id, CurrentUserId);
            if (!access.can_view)
            {
                NotifyError($"You are not permitted to view {document.document_number} in state {document.state_name}.");
                return RedirectToAction(nameof(Index));
            }

            var versions = await _unitOfWork.Document.GetVersionsAsync(id);
            var current = versions.FirstOrDefault(v => v.id == document.current_version_id) ?? versions.FirstOrDefault();
            var model = new DocumentDetailsViewModel
            {
                Document = document,
                Access = access,
                Versions = versions,
                CurrentVersion = current,
                Stages = current == null ? new() : await _unitOfWork.Document.GetStagesAsync(current.id),
                History = await _unitOfWork.Document.GetStateHistoryAsync(id),
                Links = await _unitOfWork.Document.GetLinksAsync(id),
                Attachments = await _unitOfWork.Document.GetAttachmentsAsync(id, true),
                Audit = await _unitOfWork.Document.GetAuditAsync(id, 100),
                CanManageAttachments = HasPermission(DocumentPermissions.Attachments)
            };
            ViewBag.canEdit = access.can_edit && HasPermission(DocumentPermissions.Edit);
            ViewBag.attachmentTypes = AttachmentTypes;
            ViewBag.fileMissing = current?.source_storage_path is { } p && !_fileStore.Exists(p);
            ViewBag.canCompose = access.can_author && current != null && current.id == document.current_version_id && await Authoring.IsEnabledAsync();
            ViewBag.canOffice = current?.source_storage_path != null && WopiService.IsEditableFile(current.source_file_name)
                                && HasPermission(WopiService.EditInOfficePermission) && await DocumentEditor.IsAvailableAsync();
            await LoadWorkflowAsync(model);
            return View(model);
        }

        // ------------------------------------------------------------------ edit draft

        [HttpGet]
        public async Task<IActionResult> Edit(long id)
        {
            if (!HasPermission(DocumentPermissions.Edit)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(id);
            if (document == null) return RedirectToAction(nameof(Index));
            var access = await _unitOfWork.Document.GetAccessAsync(id, CurrentUserId);
            if (!access.can_edit)
            {
                NotifyError($"This document cannot be edited in state '{document.state_name}'.");
                return RedirectToAction(nameof(Details), new { id });
            }
            var current = (await _unitOfWork.Document.GetVersionsAsync(id)).FirstOrDefault(v => v.id == document.current_version_id);
            return View(new DocumentEditViewModel
            {
                DocumentId = document.id,
                DocumentVersionId = current?.id ?? 0,
                ExpectedRowVersion = document.row_version,
                DocumentNumber = document.document_number,
                VersionLabel = current?.display_revision,
                RequiresMasterFormatNumber = document.requires_master_format_number,
                MasterFormatNumberLabel = document.master_format_number_label,
                CanUpload = access.can_upload,
                CurrentFileName = current?.source_file_name,
                Title = document.title,
                Keywords = document.keywords,
                Summary = document.summary,
                MasterFormatNumber = document.master_format_number,
                ChangeSummary = current?.change_summary,
                ChangeControlNumber = current?.change_control_number
            });
        }

        [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(MaxUploadBytes)]
        public async Task<IActionResult> Edit(DocumentEditViewModel model, IFormFile? newSourceFile, CancellationToken ct)
        {
            if (!HasPermission(DocumentPermissions.Edit)) return NoPermission();
            var previous = (await _unitOfWork.Document.GetVersionsAsync(model.DocumentId)).FirstOrDefault(v => v.id == model.DocumentVersionId);
            if (!await ValidChangeControl(model.ChangeControlNumber, previous?.change_control_number))
            { NotifyError("Select an active registered Change Control number."); return View(model); }
            if (!ModelState.IsValid)
            {
                NotifyError(FirstModelError());
                return View(model);
            }

            StoredFile? stored = null;
            if (newSourceFile is { Length: > 0 })
            {
                try
                {
                    stored = await _fileStore.SaveUploadAsync(newSourceFile, "documents", ct);
                }
                catch (FileStoreException ex)
                {
                    NotifyError(ex.Message);
                    return View(model);
                }
            }

            (string? Before,string? After) contentAudit=(null,null);
            if(stored != null)
            {
                var extractor=HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.Authoring.IDocumentTextExtractor>();
                contentAudit=await DMS.Web.Services.Authoring.SavedContentAudit.CaptureAsync(extractor,
                    previous?.source_storage_path,previous?.source_file_name,stored.RelativePath,stored.FileName,ct);
                contentAudit=DMS.Web.Services.Authoring.SavedContentAudit.WithEvidence((contentAudit.Before!,contentAudit.After!),
                    previous?.source_sha256,previous?.source_storage_path,stored.Sha256,stored.RelativePath);
            }
            var result = await _unitOfWork.Document.UpdateDraftAsync(new DocumentDraftUpdate
            {
                ContentBeforeJson = contentAudit.Before,
                ContentAfterJson = contentAudit.After,
                ExpectedSourceSha = previous?.source_sha256?.Trim(),
                DocumentId = model.DocumentId,
                DocumentVersionId = model.DocumentVersionId,
                Title = model.Title,
                Keywords = model.Keywords,
                Summary = model.Summary,
                MasterFormatNumber = model.MasterFormatNumber,
                ChangeSummary = model.ChangeSummary,
                ChangeControlNumber = model.ChangeControlNumber,
                ExpectedRowVersion = model.ExpectedRowVersion,
                File = stored
            }, CurrentUserId, ClientIp);

            if (!result.Success)
            {
                if (stored != null) _fileStore.Archive(stored.RelativePath);
                NotifyError(result.Message);
                return View(model);
            }
            NotifySuccess(stored == null ? "The draft has been updated." : $"The draft has been updated with the new file {stored.FileName}.");
            return RedirectToAction(nameof(Details), new { id = model.DocumentId });
        }

        // ------------------------------------------------------------------ attachments (URS-7.13)

        [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(MaxUploadBytes)]
        public async Task<IActionResult> AddAttachment(long documentId, string? attachmentType, string? title, string? remarks,
                                                       bool forCurrentRevision, IFormFile? file, CancellationToken ct)
        {
            if (!HasPermission(DocumentPermissions.Attachments)) return NoPermission();
            if (file is not { Length: > 0 })
            {
                NotifyError("Choose the file to attach.");
                return RedirectToAction(nameof(Details), new { id = documentId });
            }
            var type = (attachmentType ?? "REFERENCE").ToUpperInvariant();
            if (!AttachmentTypes.Contains(type)) type = "OTHER";

            var document = await _unitOfWork.Document.GetByIdAsync(documentId);
            if (document == null) return RedirectToAction(nameof(Index));

            StoredFile stored;
            try
            {
                stored = await _fileStore.SaveUploadAsync(file, "attachments", ct);
            }
            catch (FileStoreException ex)
            {
                NotifyError(ex.Message);
                return RedirectToAction(nameof(Details), new { id = documentId });
            }

            var result = await _unitOfWork.Document.AddAttachmentAsync(documentId,
                forCurrentRevision ? document.current_version_id : null, type,
                string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(stored.FileName) : title.Trim(),
                stored, remarks, CurrentUserId, ClientIp);
            if (!result.Success) _fileStore.Archive(stored.RelativePath);
            Notify(result, result.Message);
            return RedirectToAction(nameof(Details), new { id = documentId });
        }

        [HttpGet]
        public async Task<IActionResult> Attachment(long id, long documentId)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            var access = await _unitOfWork.Document.GetAccessAsync(documentId, CurrentUserId);
            var attachment = (await _unitOfWork.Document.GetAttachmentsAsync(documentId, true)).FirstOrDefault(a => a.id == id);
            if (attachment?.storage_path == null) return NotFound();
            if (!access.can_view_content)
            {
                NotifyError("You are not permitted to open the content of this document.");
                return RedirectToAction(nameof(Details), new { id = documentId });
            }
            try
            {
                if(!_fileStore.Exists(attachment.storage_path))throw new FileStoreException("The attachment file is missing from the configured file store. Ask the administrator to restore this attachment.");
                await _unitOfWork.Document.RecordReadAsync(documentId, attachment.document_version_id ?? 0,
                    $"Attachment '{attachment.title}' v{attachment.attachment_version} opened ({attachment.file_name})", CurrentUserId, ClientIp);
                var document=await _unitOfWork.Document.GetByIdAsync(documentId);
                var rendition=document?.current_version_id is long revision?await _unitOfWork.Rendition.GetStatusAsync(revision):null;
                ViewData["Watermark"]=access.apply_screen_watermark?DMS.Web.Services.DocumentEditor.ScreenCopyLabel.Resolve(rendition?.expected_watermark,document?.state_is_effective==true,document?.is_obsolete==true):null;
                Response.Headers.CacheControl="no-store";
                ViewBag.AttachmentTitle=attachment.title;
                ViewBag.AttachmentFileName=attachment.file_name;
                ViewBag.AttachmentContentUrl=Url.Action(nameof(AttachmentContent),new{id,documentId});
                var extension=Path.GetExtension(attachment.file_name??"").ToLowerInvariant();
                if(extension==".pdf")ViewBag.AttachmentKind="pdf";
                else if((attachment.mime_type??FileStore.MimeFromExtension(attachment.file_name??"")).StartsWith("image/",StringComparison.OrdinalIgnoreCase))ViewBag.AttachmentKind="image";
                else {
                    var renderer=HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.Authoring.IDocumentPreviewRenderer>();
                    if(renderer.CanRender(attachment.mime_type??FileStore.MimeFromExtension(extension),attachment.file_name??"")){
                        var rendered=await renderer.RenderAsync(attachment.storage_path,attachment.file_name??"",attachment.mime_type??FileStore.MimeFromExtension(extension));
                        var sanitizer=HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.Authoring.IEditorHtmlSanitizer>();
                        ViewBag.AttachmentHtml=string.IsNullOrWhiteSpace(rendered.Html)?null:sanitizer.Sanitize(rendered.Html,new DMS.Web.Services.Authoring.EditorSanitizeLimits()).Html;
                        ViewBag.AttachmentKind=ViewBag.AttachmentHtml==null?"unavailable":"html";
                    }else ViewBag.AttachmentKind="unavailable";
                }
                return View("AttachmentReadOnly");
            }
            catch (FileStoreException ex)
            {
                NotifyError(ex.Message);
                return RedirectToAction(nameof(Details), new { id = documentId });
            }
        }

        [HttpGet]
        public async Task<IActionResult> AttachmentContent(long id,long documentId)
        {
            if(!HasPermission(DocumentPermissions.View))return NoPermission();
            var access=await _unitOfWork.Document.GetAccessAsync(documentId,CurrentUserId);
            if(!access.can_view||!access.can_view_content)return Forbid();
            var attachment=(await _unitOfWork.Document.GetAttachmentsAsync(documentId,true)).FirstOrDefault(a=>a.id==id);
            if(attachment?.storage_path==null||!_fileStore.Exists(attachment.storage_path))return NotFound();
            Response.Headers["Content-Disposition"]="inline";
            Response.Headers["Cache-Control"]="no-store";
            Response.Headers["X-Content-Type-Options"]="nosniff";
            return File(await _fileStore.OpenCachedReadAsync(attachment.storage_path),attachment.mime_type??FileStore.MimeFromExtension(attachment.file_name??""),enableRangeProcessing:true);
        }

        // ------------------------------------------------------------------ reading the content

        /// <summary>Reads a revision on screen. Every opening is written to the audit trail.</summary>
        [HttpGet]
        public async Task<IActionResult> Read(long versionId, long id, string? view = null)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            var document = await _unitOfWork.Document.GetByIdAsync(id);
            var version = document == null ? null : (await _unitOfWork.Document.GetVersionsAsync(id)).FirstOrDefault(v => v.id == versionId);
            if (document == null || version == null) return RedirectToAction(nameof(Index));

            var access = await _unitOfWork.Document.GetAccessAsync(id, CurrentUserId);
            if (!access.can_view_content)
            {
                NotifyError("You are not permitted to read the content of this document in its current state.");
                return RedirectToAction(nameof(Details), new { id });
            }
            if (string.IsNullOrWhiteSpace(version.source_storage_path))
            {
                NotifyError("This revision has no file yet.");
                return RedirectToAction(nameof(Details), new { id });
            }

            // Phase 4: the controlled copy (watermarked PDF) is what a reader sees, once it exists; ?view=text shows the content as text
            var rendition = await _unitOfWork.Rendition.GetStatusAsync(versionId);
            ViewBag.rendition = rendition;
            if (rendition?.IsCurrent == true && rendition.base_ready && view != "text")
            {
                await _unitOfWork.Document.RecordReadAsync(id, versionId,
                    $"Controlled copy of rev {version.display_revision} opened on screen ({rendition.rendered_watermark ?? "no"} watermark)", CurrentUserId, ClientIp);
                ViewBag.document = document;
                ViewBag.access = access;
                ViewBag.renditionUrl = Url.Action(nameof(RenditionPdf), new { id, versionId });
                ViewBag.watermark = "";
                // Editor's read-only fallback reuses this action. Resolve the
                // reader explicitly instead of inheriting the caller's view name.
                return View("Read", version);
            }

            string? html = null, problem = null;
            var extension = Path.GetExtension(version.source_file_name ?? "").ToLowerInvariant();
            try
            {
                var renderer = HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.Authoring.IDocumentPreviewRenderer>();
                if (extension != ".pdf" && renderer.CanRender(version.source_mime_type ?? "", version.source_file_name ?? ""))
                {
                    // Open XML renderer of the old DMS (text, tables, images of .docx / .xlsx), then sanitised for display
                    var rendered = await renderer.RenderAsync(version.source_storage_path, version.source_file_name ?? "", version.source_mime_type ?? "");
                    var sanitizer = HttpContext.RequestServices.GetRequiredService<DMS.Web.Services.Authoring.IEditorHtmlSanitizer>();
                    html = string.IsNullOrWhiteSpace(rendered.Html) ? null
                         : sanitizer.Sanitize(rendered.Html, new DMS.Web.Services.Authoring.EditorSanitizeLimits { MaxContentBytes = 64L * 1024 * 1024, MaxImageBytes = 16L * 1024 * 1024, MaxTotalImageBytes = 64L * 1024 * 1024 }).Html;
                    if (html == null) problem = rendered.UnavailableReason ?? "The file could not be shown.";
                }
                else if (extension == ".docx")
                {
                    await using var stream = await _fileStore.OpenCachedReadAsync(version.source_storage_path);
                    html = DocxPreview.ToHtml(stream);
                }
                else if (extension != ".pdf")
                {
                    problem = $"{extension} files are shown as their PDF rendition. It is not ready yet - produce it from the document's page (PDF rendition).";
                }
                else if (!_fileStore.Exists(version.source_storage_path))
                {
                    problem = "The file of this revision is not in the file store. Check Dms:Storage:Root.";
                }
            }
            catch (Exception ex) when (ex is FileStoreException or InvalidDataException or System.Xml.XmlException)
            {
                problem = "The file could not be shown: " + ex.Message;
            }

            await _unitOfWork.Document.RecordReadAsync(id, versionId,
                $"Content of rev {version.display_revision} opened on screen ({version.source_file_name})", CurrentUserId, ClientIp);

            ViewBag.document = document;
            ViewBag.access = access;
            ViewBag.html = html;
            ViewBag.problem = problem;
            ViewBag.isPdf = extension == ".pdf" && problem == null;
            ViewBag.watermark = DMS.Web.Services.DocumentEditor.ScreenCopyLabel.Resolve(rendition?.expected_watermark,
                document.state_is_effective, document.is_obsolete
                    || (document.current_version_id != versionId && document.effective_version_id != versionId));
            return View("Read", version);
        }

        
        /// <summary>The PDF itself, for the reader page (inline, not as a download).</summary>
        [HttpGet]
        public async Task<IActionResult> Pdf(long versionId, long id)
        {
            if (!HasPermission(DocumentPermissions.View)) return NoPermission();
            var access = await _unitOfWork.Document.GetAccessAsync(id, CurrentUserId);
            var version = (await _unitOfWork.Document.GetVersionsAsync(id)).FirstOrDefault(v => v.id == versionId);
            if (!access.can_view_content || version?.source_storage_path == null) return Forbid();
            if (!string.Equals(Path.GetExtension(version.source_file_name), ".pdf", StringComparison.OrdinalIgnoreCase)) return NotFound();
            try
            {
                Response.Headers["Content-Disposition"] = "inline";
                Response.Headers["Cache-Control"] = "no-store";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(await _fileStore.OpenCachedReadAsync(version.source_storage_path), "application/pdf");
            }
            catch (FileStoreException)
            {
                return NotFound();
            }
        }
    }
}
