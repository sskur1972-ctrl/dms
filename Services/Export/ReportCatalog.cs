//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5b - the compliance reports (Bcl.Dms IReportingService). Each report is one read-only sp_report_* function;
 * its column names are the headings. A column "document_id" is not shown - it turns the document number into a link. */
namespace DMS.Web.Services.Export
{
    [Flags]
    public enum ReportParams
    {
        None = 0, Period = 1, Department = 2, DocumentType = 4, State = 8, Days = 16, Group = 32, User = 64,
        IncludeInactive = 128, IncludeObsolete = 256, PlantScope = 512
    }

    public sealed class ReportQuery
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public long? DepartmentId { get; set; }
        public long? TypeId { get; set; }
        public string? State { get; set; }
        public int? Days { get; set; }
        public string? Group { get; set; }
        public string? User { get; set; }
        public bool IncludeInactive { get; set; }
        public bool IncludeObsolete { get; set; }
        //Changes by Ash
        public int? PlantId { get; set; }

        public string? AccessScope { get; set; }

        public string? PlantScope { get; set; }
    }

    public sealed record ReportDefinition(string Code, string Title, string Description, string Reference, ReportParams Params,
                                          string Sql, Func<ReportQuery, object> Arguments, int DefaultDays = 30,
                                          string? AlsoRequires = null)
    {
        /// <summary>The parameters as they apply to this report (for the heading and the audit record).</summary>
        public Dictionary<string, object?> Describe(ReportQuery q)
        {
            var d = new Dictionary<string, object?>();
            if (Params.HasFlag(ReportParams.Period)) { d["From"] = q.From?.ToString("yyyy-MM-dd"); d["To"] = q.To?.ToString("yyyy-MM-dd"); }
            if (Params.HasFlag(ReportParams.Department)) d["DepartmentId"] = q.DepartmentId;
            if (Params.HasFlag(ReportParams.DocumentType)) d["DocumentTypeId"] = q.TypeId;
            if (Params.HasFlag(ReportParams.State)) d["State"] = q.State;
            if (Params.HasFlag(ReportParams.Days)) d["Days"] = q.Days;
            if (Params.HasFlag(ReportParams.Group)) d["Group"] = q.Group;
            if (Params.HasFlag(ReportParams.User)) d["User"] = q.User;
            if (Params.HasFlag(ReportParams.IncludeInactive)) d["IncludeInactive"] = q.IncludeInactive;
            if (Params.HasFlag(ReportParams.IncludeObsolete)) d["IncludeObsolete"] = q.IncludeObsolete;
            if (Params.HasFlag(ReportParams.PlantScope))
            {
                d["PlantId"] = q.PlantId;
                d["PlantScope"] = q.PlantScope;
            }
            return d;
        }
    }

    public static class ReportCatalog
    {
        public static readonly IReadOnlyList<ReportDefinition> All = new List<ReportDefinition>
        {
            new("REGISTER", "Document register", "Every controlled document with its state, current and in-force revision, effective and review dates, owner.",
                "", ReportParams.Department | ReportParams.DocumentType | ReportParams.State | ReportParams.IncludeObsolete,
                "SELECT * FROM DMS.sp_report_register(@t::bigint, @d::bigint, @s::text, @o::boolean);",
                q => new { t = q.TypeId, d = q.DepartmentId, s = q.State, o = q.IncludeObsolete }),
           new("OVERDUE", "Overdue workflow stages",
    "Review, approval and authorisation stages past their due date, with whom they are pending and whether they were escalated.",
    ", 7.6",
    ReportParams.Department | ReportParams.Period | ReportParams.PlantScope,
    "SELECT * FROM dms.sp_report_overdue(@d::bigint, @f::date, @to::date, @plant::integer, @scope::text);",
    q => new
    {
        d = q.DepartmentId,
        f = q.From?.Date,
        to = q.To?.Date,
        plant = q.PlantId,
        scope = q.PlantScope
    }),
         new("PERIODIC_REVIEW", "Periodic review status",
    "Documents in force whose periodic review is overdue or falls due within the chosen number of days.",
    "",
    ReportParams.Days | ReportParams.Department |
    ReportParams.DocumentType | ReportParams.PlantScope,
    "SELECT * FROM dms.sp_report_periodic_review(@n::integer, @d::bigint, @t::bigint, @plant::integer, @scope::text);",
    q => new
    {
        n = q.Days ?? 90,
        d = q.DepartmentId,
        t = q.TypeId,
        plant = q.PlantId,
        scope = q.PlantScope
    },
    90),
            new("TRENDS", "Activity trends", "Per month or week: documents created, submitted, rejected, made effective, revisions started, obsoleted, signatures, prints.",
                "Management review", ReportParams.Period | ReportParams.Group | ReportParams.Department,
                "SELECT * FROM DMS.sp_report_trends(@f::date, @to::date, @g::text, @d::bigint);",
                q => new { f = q.From?.Date, to = q.To?.Date, g = q.Group ?? "month", d = q.DepartmentId }, 365),
            new("CYCLE_TIMES", "Review cycle times", "Per document type: revisions made effective in the period and the days from first submission to effective, with review cycles.",
                "Management review", ReportParams.Period,
                "SELECT * FROM DMS.sp_report_cycle_times(@f::date, @to::date);", q => new { f = q.From?.Date, to = q.To?.Date }, 365),
            new("SIGNATURES", "Electronic signatures", "Every electronic signature applied in the period: signer, meaning, action and the SHA-256 of the signed content.",
                "21 CFR 11.50, 11.70", ReportParams.Period | ReportParams.User,
                "SELECT * FROM DMS.sp_report_signatures(@f::date, @to::date, @u::text);", q => new { f = q.From?.Date, to = q.To?.Date, u = q.User }),
            new("USER_ACCESS", "User access review", "Every user with roles, number of permissions, status, last login and password date - for the periodic access review.",
                "21 CFR 11.10(d), Annex 11 cl. 12", ReportParams.IncludeInactive,
                "SELECT * FROM DMS.sp_report_user_access(@i::boolean);", q => new { i = q.IncludeInactive },
                AlsoRequires: DMS.Web.CommonUtils.CompliancePermissions.AuditView),   // who has which access is not for every reader
           
new("PRINT_SUMMARY", "Controlled print summary",
    "Per document revision: print requests, pending requests, copies authorised, printed, reprinted, returned, destroyed, lost, still out, uncontrolled prints.",
    "URS-7.15",
    ReportParams.Period,
    "SELECT * FROM dms.sp_report_print_summary(@f::date, @to::date);",
    q => new { f = q.From?.Date, to = q.To?.Date },
    365),


new("TRAINING", "Training hand-off", "Documents that require training and were made effective (or imported) in the period - the list for the training system.",
    "", ReportParams.Period | ReportParams.Department,
    "SELECT * FROM DMS.sp_report_training(@f::date, @to::date, @d::bigint);", q => new
    { f = q.From?.Date, to = q.To?.Date, d = q.DepartmentId }),

        };

        public static ReportDefinition? Find(string? code)
            => All.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));

        public static string Heading(string column)
        {
            var words = column.Replace('_', ' ').Trim();
            return words.Length == 0 ? column : char.ToUpperInvariant(words[0]) + words[1..];
        }
    }
}
