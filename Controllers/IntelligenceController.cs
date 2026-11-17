using DMS.DataAccess.Repository.IRepository;
using DMS.Web.CommonUtils;
using DMS.Web.Services.Intelligence;
using Microsoft.AspNetCore.Mvc;
namespace DMS.Web.Controllers;
public sealed class IntelligenceController(IUnitOfWork uow, DocumentIntelligenceService intelligence, RagService rag, AiConfigurationService settings, AiProviderClient ai, DMS.Web.Services.FileStore store, DMS.Web.Services.DocumentEditor.IEditorProviderResolver editorResolver, DMS.Web.Services.IConfigService config, DMS.Web.Services.Rendition.LibreOfficeConverter libreOffice, DMS.Web.Services.Authoring.IHtmlDocxWriter docxWriter, DMS.Web.Services.Authoring.IEditorHtmlSanitizer sanitizer) : DmsBaseController(uow)
{
    [HttpGet("/Intelligence"), HttpGet("/Intelligence/Index")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!HasPermission(DocumentPermissions.View)) return NoPermission();
        var flags = new Dictionary<string, bool>(); foreach (var key in new[] { "ai.ocr.enabled", "ai.search.semanticEnabled", "ai.autoIndex.enabled", "ai.complianceAssistant.enabled", "ai.trends.enabled", "ai.review.modelEnabled" }) flags[key] = await intelligence.Enabled(key, ct);
        ViewBag.Status = await rag.Status(ct); ViewBag.CanConfigure = HasPermission(AdminPermissions.SystemConfig); ViewBag.CanReferences = CanReferences(); ViewBag.CanReport = HasPermission(CompliancePermissions.ReportView); return View(flags);
    }
    [HttpGet]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        if (!HasPermission(DocumentPermissions.View)) return NoPermission(); ViewBag.Query = q;
        if (q?.Length > 500) return BadRequest();
        try { var hits = string.IsNullOrWhiteSpace(q) ? new List<IntelligenceHit>() : await intelligence.Search(q, CurrentUserId, ct); ViewBag.SearchMode = intelligence.SearchMode; return View(hits); }
        catch (InvalidOperationException ex) { ViewBag.Error = ex.Message; return View(new List<IntelligenceHit>()); }
    }
    [HttpGet]
    public async Task<IActionResult> Tools(CancellationToken ct)
    {
        if (!HasPermission(DocumentPermissions.View)) return NoPermission();
        var (provider, error) = await editorResolver.ResolveAsync(ct); ViewBag.OfficeProvider = provider?.Name ?? "Unavailable"; ViewBag.OfficeError = error;
        var libreOfficeStatus = await libreOffice.ProbeAsync(await config.GetStringAsync(DMS.Web.Services.ConfigKeys.RenditionLibreOfficePath, null, ct), ct);
        ViewBag.CanConvert = libreOfficeStatus.IsAvailable; ViewBag.PdfToWordStatus = libreOfficeStatus.Message;
        ViewBag.OcrEnabled = await intelligence.Enabled("ai.ocr.enabled", ct); return View();
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(27 * 1024 * 1024)]
    public async Task<IActionResult> OcrUpload(IFormFile? file, CancellationToken ct)
    {
        if (!HasPermission(DocumentPermissions.View)) return NoPermission();
        var ext = Path.GetExtension(file?.FileName ?? "").ToLowerInvariant();
        if (file == null || file.Length == 0 || file.Length > 25 * 1024 * 1024 || ext is not (".pdf" or ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp"))
        {
            return AiToolError("Choose a PDF or image file up to 25 MB.");
        }
        DMS.Model.Documents.StoredFile? stored = null;
        try
        {
            stored = await store.SaveUploadAsync(file, "ai-tools", ct, ".pdf,.png,.jpg,.jpeg,.tif,.tiff,.bmp");
            var (text, engine) = await intelligence.Extract(stored.RelativePath, stored.FileName, stored.SizeBytes, ct);
            var message = $"Text extracted with {engine}. Review the result before using it in a controlled record.";
            text = text.Length > 150000 ? text[..150000] + "\n[Output shortened to 150,000 characters.]" : text;
            if (IsAjaxRequest()) return Json(new { text, message });
            TempData["AiToolText"] = text; TempData["AiToolMessage"] = message;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DMS.Web.Services.FileStoreException or HttpRequestException) { return AiToolError(ex is HttpRequestException ? "OCR service unavailable. Check the configured OCR endpoint and credentials." : ex.Message); }
        finally { if (stored != null) store.Archive(stored.RelativePath); }
        return RedirectToAction(nameof(Tools));
    }
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(27 * 1024 * 1024)]
    public async Task<IActionResult> PdfToWord(IFormFile? file, CancellationToken ct)
    {
        if (!HasPermission(DocumentPermissions.View)) return NoPermission();
        if (file == null || file.Length < 5 || file.Length > 25 * 1024 * 1024 || !Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return AiToolError("Choose a PDF file up to 25 MB.");
        }
        DMS.Model.Documents.StoredFile? stored = null;
        try
        {
            stored = await store.SaveUploadAsync(file, "ai-pdf-to-word", ct, ".pdf");
            var path = await config.GetStringAsync(DMS.Web.Services.ConfigKeys.RenditionLibreOfficePath, null, ct);
            var status = await libreOffice.ProbeAsync(path, ct);
            if (!status.IsAvailable) throw new InvalidOperationException(status.Message);
            var timeout = await config.GetIntAsync(DMS.Web.Services.ConfigKeys.RenditionTimeoutSeconds, 180, ct);
            await using var source = await store.OpenCachedReadAsync(stored.RelativePath, ct);
            var converted = await libreOffice.ConvertPdfToWordAsync(path, source, timeout, ct);
            var method = "LibreOffice Writer PDF import";
            if (!DMS.Web.Services.Rendition.LibreOfficeConverter.HasExtractableWordText(converted))
            {
                if (!await intelligence.Enabled("ai.ocr.enabled", ct)) throw new InvalidOperationException("This PDF appears to be scanned. Enable ai.ocr.enabled and configure the OCR endpoint to make its text editable in Word.");
                var (text, engine) = await intelligence.Extract(stored.RelativePath, stored.FileName, stored.SizeBytes, ct);
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("OCR did not find readable text in this PDF.");
                if (text.Length > 500000) text = text[..500000] + "\n[OCR output shortened to 500,000 characters.]";
                var html = string.Join("", text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => "<p>" + System.Net.WebUtility.HtmlEncode(line) + "</p>"));
                var clean = sanitizer.Sanitize(html, DMS.Web.Services.Authoring.EditorSanitizeLimits.Default).Html;
                using var ocrDocx = docxWriter.Write(clean, new DMS.Web.Services.Authoring.DocxWriteMetadata { Title = Path.GetFileNameWithoutExtension(file.FileName) + " (OCR)" });
                using var buffer = new MemoryStream(); await ocrDocx.CopyToAsync(buffer, ct); converted = buffer.ToArray(); method = $"LibreOffice + {engine} OCR";
            }
            Response.Headers["X-DMS-PdfToWord-Method"] = method;
            return File(converted, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Path.GetFileNameWithoutExtension(file.FileName) + ".docx");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DMS.Web.Services.FileStoreException or HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return AiToolError(ex is TaskCanceledException or TimeoutException ? "Conversion timed out. Try a smaller PDF or check the LibreOffice/OCR service." : ex.Message);
        }
        finally { if (stored != null) store.Archive(stored.RelativePath); }
    }
    private bool IsAjaxRequest() => string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    private IActionResult AiToolError(string message) { if (IsAjaxRequest()) return BadRequest(new { error = message }); TempData["AiToolError"] = message; return RedirectToAction(nameof(Tools)); }
    [HttpGet]
    public async Task<IActionResult> Document(long versionId, CancellationToken ct)
    {
        if (!HasPermission(DocumentPermissions.View)) return NoPermission();
        var id = await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId); if (id == null) return NotFound();
        var access = await _unitOfWork.Document.GetAccessAsync(id.Value, CurrentUserId); if (!access.can_view || !access.can_view_content) return Forbid();
        ViewBag.Version = versionId; ViewBag.DocumentId = id; ViewBag.CanReview = access.can_comment; ViewBag.HasReview = await intelligence.LastReview(versionId, CurrentUserId, ct) != null;
        var indexed = await intelligence.Get(versionId, ct);
        var version = (await _unitOfWork.Document.GetVersionsAsync(id.Value)).FirstOrDefault(v => v.id == versionId);
        if (indexed != null && indexed.SourceSha256 != version?.source_sha256) { indexed.Status = "STALE"; indexed.Message = "Document content changed. Reindex to refresh the extracted text and keywords."; }
        return View(indexed);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Process(long versionId, CancellationToken ct)
    {
        if (!HasPermission(DocumentPermissions.View)) return NoPermission();
        try { var result = await intelligence.Index(versionId, CurrentUserId, ClientIp, ct, force: true); TempData["IntelligenceMessage"] = result.Status == "READY" ? "Digitization and indexing completed." : result.Message; }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { TempData["IntelligenceMessage"] = ex.Message; }
        return RedirectToAction(nameof(Document), new { versionId });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(long versionId, CancellationToken ct)
    {
        if (!HasPermission(DocumentPermissions.View)) return NoPermission(); ViewBag.Version = versionId;
        try { var review = await intelligence.Review(versionId, CurrentUserId, ClientIp, ct); await PrepareReviewPage(versionId); return View(review); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { TempData["IntelligenceMessage"] = ex.Message; }
        catch (HttpRequestException) { TempData["IntelligenceMessage"] = "AI service unavailable. Check the approved endpoint, credentials and model; no compliance conclusion was recorded."; }
        return RedirectToAction(nameof(Document), new { versionId });
    }
    [HttpGet]
    public async Task<IActionResult> ReviewResult(long versionId, CancellationToken ct)
    {
        if (!HasPermission(DocumentPermissions.View)) return NoPermission();
        var id = await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId); if (id == null) return NotFound();
        var access = await _unitOfWork.Document.GetAccessAsync(id.Value, CurrentUserId); if (!access.can_view || !access.can_view_content) return Forbid();
        var result = await intelligence.LastReview(versionId, CurrentUserId, ct); if (result == null) return NotFound(); await PrepareReviewPage(versionId); return View("Review", result);
    }
    async Task PrepareReviewPage(long versionId)
    {
        ViewBag.Version = versionId;
        var id = await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId);
        if (id == null) return;
        var document = await _unitOfWork.Document.GetByIdAsync(id.Value);
        var version = (await _unitOfWork.Document.GetVersionsAsync(id.Value)).FirstOrDefault(v => v.id == versionId);
        var access = await _unitOfWork.Document.GetAccessAsync(id.Value, CurrentUserId);
        ViewBag.DocumentId = id.Value; ViewBag.DocumentNumber = document?.document_number; ViewBag.DocumentTitle = document?.title;
        ViewBag.Revision = version?.display_revision; ViewBag.CanReview = access.can_comment;
        ViewBag.CanExportReview = HasPermission(CompliancePermissions.ReportExport) && access.can_export;
        ViewBag.CanConfigureAi = HasPermission(AdminPermissions.SystemConfig);
    }
    [HttpGet]
    public async Task<IActionResult> ExportReview(long versionId, bool actionPlan = false, CancellationToken ct = default)
    {
        if (!HasPermission(DocumentPermissions.View) || !HasPermission(CompliancePermissions.ReportExport)) return NoPermission();
        var id = await _unitOfWork.Document.GetDocumentIdOfVersionAsync(versionId); if (id == null) return NotFound();
        var access = await _unitOfWork.Document.GetAccessAsync(id.Value, CurrentUserId);
        if (!access.can_view || !access.can_view_content || !access.can_export) return Forbid();
        var review = await intelligence.LastReview(versionId, CurrentUserId, ct); if (review == null) return NotFound();
        var document = await _unitOfWork.Document.GetByIdAsync(id.Value);
        var table = new DMS.Web.Services.Export.TableData { Title = actionPlan ? "Proposed compliance action plan" : "Advisory compliance review report" };
        table.Heading.Add($"{document?.document_number} · {document?.title}");
        table.Heading.Add($"{ReviewPresentation.Status(review.ModelStatus)} · AI coverage {ReviewPresentation.Coverage(review)}% · Review {review.ReviewedAt:dd-MMM-yyyy HH:mm} UTC");
        table.Heading.Add("Advisory findings require QA assessment. Actions below are proposed; ownership, completion and effectiveness are not verified by this report.");
        table.Heading.Add("Reviewed source SHA-256: " + review.SourceSha256);
        foreach (var warning in review.Warnings) table.Heading.Add(warning);
        table.Columns.AddRange(["Observation", "Source", "Severity (initial)", "Section", "Gap / observation", "Recommendation", "Document evidence", "Reference IDs"]);
        if (actionPlan) table.Columns.AddRange(["QA risk assessment", "CAPA decision", "Action owner", "Target date", "Completion evidence", "Effectiveness verification", "Status"]);
        foreach (var observation in ReviewPresentation.Observations(review))
        {
            var row = new List<object?> { observation.Id, observation.Source, observation.Severity, observation.Section, observation.Finding, observation.Recommendation, observation.Evidence, string.Join(", ",observation.Citations) };
            if (actionPlan) row.AddRange(["", "", "", "", "", "", "Pending QA assessment"]);
            table.Rows.Add(row.ToArray());
        }
        var file = DMS.Web.Services.Export.TableExport.Create(table, "xlsx", actionPlan ? "compliance-action-plan" : "compliance-review");
        await _unitOfWork.Authoring.RecordVersionEventAsync(versionId, "AI_REVIEW_EXPORTED", $"{(actionPlan ? "Proposed action plan" : "Advisory review report")} exported; {table.Rows.Count} observations; SHA-256 {file.Sha256}", null, CurrentUserId, ClientIp);
        return File(file.Bytes, file.ContentType, file.FileName);
    }
    bool CanReferences() => HasPermission(AdminPermissions.ComplianceRule) || HasPermission(AdminPermissions.SystemConfig);
    [HttpGet]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
        ViewBag.Status = await rag.Status(ct);
        var values = (await _unitOfWork.SystemConfig.GetListAsync()).Where(s => s.config_key?.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) == true && !DMS.Web.Services.Configuration.InfrastructureSettings.IsFileManaged(s.config_key)).OrderBy(s => s.config_key).ToList();
        foreach (var value in values) if (value.IsSecret) value.config_value = "";
        return View(values);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TestProvider(bool embeddings, CancellationToken ct)
    {
        if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
        try
        {
            if (embeddings) { var options = await settings.Embeddings(ct) ?? throw new InvalidOperationException("Embeddings are disabled."); await ai.Embed(options, ["Synthetic GMP cleaning validation test"], ct); }
            else { await ai.Review(await settings.Review(ct), "{\"document\":{\"text\":\"Synthetic procedure: record cleaning date and operator.\"},\"references\":[]}", ct); }
            TempData["IntelligenceMessage"] = embeddings ? "Embedding model connection verified." : "Review model returned valid structured findings for the synthetic test.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException) { TempData["IntelligenceMessage"] = ex is InvalidOperationException ? ex.Message : "Provider returned an unsupported response."; }
        return RedirectToAction(nameof(Settings));
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> BuildEmbeddings(CancellationToken ct)
    {
        if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
        try { await rag.BackfillEmbeddings(ct); TempData["IntelligenceMessage"] = "Embedding batch processed. The background worker continues remaining chunks."; }
        catch (InvalidOperationException ex) { TempData["IntelligenceMessage"] = ex.Message; }
        return RedirectToAction(nameof(Settings));
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SelectProvider(string provider, string reason)
    {
        if (!HasPermission(AdminPermissions.SystemConfig)) return NoPermission();
        if (provider is not ("OPENAI" or "DEEPSEEK" or "OLLAMA" or "LOCAL" or "OPENAI_COMPATIBLE")) return BadRequest();
        TempData["IntelligenceMessage"] = "Select ai.review.provider in application.json and restart the application. AI settings are file-managed.";
        return RedirectToAction(nameof(Settings));
    }
    [HttpGet]
    public async Task<IActionResult> References(CancellationToken ct)
    {
        if (!CanReferences()) return NoPermission(); return View(await rag.Sources(ct));
    }
    [HttpGet]
    public async Task<IActionResult> Reference(long id, CancellationToken ct)
    {
        if (!CanReferences()) return NoPermission(); var source = await rag.Source(id, ct); return source == null ? NotFound() : View(source);
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddReference(string title, string? sourceUri, string? body, IFormFile? file, bool active, CancellationToken ct)
    {
        if (!CanReferences()) return NoPermission();
        try
        {
            if (file != null) { if (file.Length > 25 * 1024 * 1024) throw new InvalidOperationException("Reference upload exceeds 25 MB."); var stored = await store.SaveUploadAsync(file, "ai-references", ct); body = (await intelligence.Extract(stored.RelativePath, stored.FileName, stored.SizeBytes, ct)).text; }
            await rag.AddSource(title ?? "", sourceUri, body ?? "", active, CurrentUserId, ClientIp, ct); TempData["IntelligenceMessage"] = "Reference indexed. Active references are available for advisory review.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DMS.Web.Services.FileStoreException or HttpRequestException) { TempData["IntelligenceMessage"] = ex is HttpRequestException ? "Reference extraction service unavailable." : ex.Message; }
        return RedirectToAction(nameof(References));
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReferenceStatus(long id, bool active, CancellationToken ct)
    {
        if (!CanReferences()) return NoPermission(); await rag.SetActive(id, active, CurrentUserId, ClientIp, ct); return RedirectToAction(nameof(References));
    }
}
