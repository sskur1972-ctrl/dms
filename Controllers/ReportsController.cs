//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5b - compliance reports (Bcl.Dms IReportingService): on screen (printable), CSV and Excel.
 * Every export is written to the audit trail with its parameters, row count and the SHA-256 of the file. */
using System.Text.Json;
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.CommonUtils;
using DMS.Web.Services;
using DMS.Web.Services.Export;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class ReportsController : DmsBaseController
    {
        private readonly IConfigService _config;
        public ReportsController(IUnitOfWork unitOfWork, IConfigService config) : base(unitOfWork) => _config = config;

        [HttpGet]
        public IActionResult Index()
        {
            if (!HasPermission(CompliancePermissions.ReportView)) return NoPermission();
            return View(ReportCatalog.All.Where(r => r.AlsoRequires == null || HasPermission(r.AlsoRequires)).ToList());
        }

        [HttpGet]
        public async Task<IActionResult> Run(string id, [FromQuery] ReportQuery query)
        {
            if (!HasPermission(CompliancePermissions.ReportView)) return NoPermission();
            var report = ReportCatalog.Find(id);
            if (report == null) return NotFound();
            if(report.Code=="TRENDS"&&!await _config.GetBoolAsync("ai.trends.enabled",true))return BadRequest("Trend Analysis is disabled in AI settings.");
            if (report.AlsoRequires != null && !HasPermission(report.AlsoRequires)) return NoPermission();
            Defaults(report, query);
            var max = await _config.GetIntAsync("report.maxRows", 20000);
            ViewBag.report = report;
            ViewBag.query = query;
            ViewBag.canExport = HasPermission(CompliancePermissions.ReportExport);
            ViewBag.canPrint = HasPermission(PrintPermissions.Request);
            ViewBag.TrendEnabled = await _config.GetBoolAsync("ai.trends.enabled",true);
            ViewBag.departments = await _unitOfWork.UserService.GetDepartmentsForDropDown();
            ViewBag.types = await _unitOfWork.DocumentType.GetListAsync(true);
            ViewBag.states = await _unitOfWork.WorkflowConfig.GetStatesAsync();
            ViewBag.plants = await _unitOfWork.Plant.GetPlantsForDropDownAsync();
            return View(await _unitOfWork.Compliance.RunReportAsync(report.Sql, report.Arguments(query), max));
        }

        [HttpGet]
        public async Task<IActionResult> Export(string id, [FromQuery] ReportQuery query, string format = "csv")
        {
            if (!HasPermission(CompliancePermissions.ReportExport)) return NoPermission();
            var report = ReportCatalog.Find(id);
            if (report == null) return NotFound();
            if(report.Code=="TRENDS"&&!await _config.GetBoolAsync("ai.trends.enabled",true))return BadRequest("Trend Analysis is disabled in AI settings.");
            if (report.AlsoRequires != null && !HasPermission(report.AlsoRequires)) return NoPermission();
            Defaults(report, query);
            var max = await _config.GetIntAsync("report.maxRows", 20000);
            var result = await _unitOfWork.Compliance.RunReportAsync(report.Sql, report.Arguments(query), max);
            var keep = result.Columns.Select((c, i) => (c, i)).Where(x => x.c != "document_id").ToList();
            var parameters = report.Describe(query).Where(p => p.Value != null && !(p.Value is bool b && !b))
                                   .Select(p => $"{p.Key} {p.Value}").ToList();
            var data = new TableData
            {
                Title = $"DMS report - {report.Title}",
                Heading =
                {
                    report.Description,
                    "Parameters: " + (parameters.Count == 0 ? "none" : string.Join(", ", parameters)),
                    $"Produced by {User.Claims.FirstOrDefault(c => c.Type == "displayName")?.Value} ({User.Identity?.Name}) on " +
                    $"{DateTime.UtcNow:dd-MMM-yyyy HH:mm:ss} UTC · {result.Rows.Count} rows"
                    + (result.Truncated ? $" (TRUNCATED at {max} rows)" : "")
                },
                Columns = keep.Select(k => ReportCatalog.Heading(k.c)).ToList(),
                Rows = result.Rows.Select(r => keep.Select(k => r[k.i]).ToArray()).ToList()
            };
            var file = TableExport.Create(data, format, "report-" + report.Code.ToLowerInvariant());
            var logged = await _unitOfWork.Compliance.RecordReportExportAsync(report.Code, JsonSerializer.Serialize(report.Describe(query)), result.Rows.Count,
                                                                              format.Equals("xlsx", StringComparison.OrdinalIgnoreCase) ? "XLSX" : "CSV",
                                                                              file.Sha256, CurrentUserId, ClientIp);
            if (!logged.Success) { NotifyError(logged.Message); return RedirectToAction(nameof(Run), new { id }); }
            Response.Headers["X-Content-SHA256"] = file.Sha256;
            return File(file.Bytes, file.ContentType, file.FileName);
        }

        private static void Defaults(ReportDefinition report, ReportQuery q)
        {
            if (report.Params.HasFlag(ReportParams.Period))
            {
                q.To ??= DateTime.Today;
                q.From ??= q.To.Value.AddDays(-report.DefaultDays);
                if (q.From > q.To) (q.From, q.To) = (q.To, q.From);
            }
            if (report.Params.HasFlag(ReportParams.Days)) q.Days ??= report.DefaultDays;
            if (report.Params.HasFlag(ReportParams.Group) && q.Group != "week") q.Group = "month";
        }
    }
}
