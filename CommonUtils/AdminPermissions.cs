//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Permission display names (permissions.permission) used by the Phase 2 administration screens.
 * The old Bcl.Dms codes are shown in brackets - they are kept in permissions.permission_code.
 */
namespace DMS.Web.CommonUtils
{
    public static class AdminPermissions
    {
        public const string Department      = "Manage departments";                 // admin.department.manage
        public const string DocumentType    = "Manage document types";              // admin.doctype.manage
        public const string Numbering       = "Manage numbering rules";             // admin.numbering.manage
        public const string Workflow        = "Manage workflow configuration";      // admin.workflow.manage
        public const string AccessMatrix    = "Manage the access matrix";           // admin.access.manage
        public const string ChangeReason    = "Manage reasons for change";          // changereason.manage
        public const string RejectionReason = "Manage rejection reason masters";    // rejectionreason.manage
        public const string Printer         = "Manage registered printers";         // printer.manage
        public const string ComplianceRule  = "Maintain advisory review rules";     // compliance.rule.manage
        public const string ChangeControl   = "Verify change control reference";    // changecontrol.verify
        public const string SystemConfig    = "Manage system configuration";        // admin.config.manage
        public const string Backup          = "Manage backups";                     // admin.backup.manage
        public const string Template        = "Manage templates";                   // admin.template.manage

        /// <summary>Every permission that opens something under the Administration menu.</summary>
        public static readonly string[] All =
        {
            Department, DocumentType, Numbering, Workflow, AccessMatrix, ChangeReason, RejectionReason,
            Printer, ComplianceRule, ChangeControl, SystemConfig, Backup, Template
        };
    
    }

    /// <summary>Permission display names used by the document screens (Phase 3).</summary>
    public static class DocumentPermissions
    {
        public const string View        = "View documents";            // document.view
        public const string Create      = "Create document";           // document.create
        public const string Edit        = "Edit draft content";        // document.edit
        public const string Attachments = "Manage attachments";        // attachment.manage
        public const string Links       = "Manage document links";     // document.link.manage
    }
}

namespace DMS.Web.CommonUtils
{
    /// <summary>Phase 5a - controlled print (URS-7.14, 7.15).</summary>
    public static class PrintPermissions
    {
        public const string Request   = "Request controlled print";          // print.request
        public const string Authorize = "Authorize controlled print";        // print.authorize
        public const string Issue     = "Issue / return controlled copies";  // print.issue
        public const string Log       = "View print log";                    // print.log.view

        public static readonly string[] All = { Request, Authorize, Issue, Log };
    }
}

namespace DMS.Web.CommonUtils
{
    /// <summary>Phase 5b - audit trail review, reports, legacy import.</summary>
    public static class CompliancePermissions
    {
        public const string AuditView    = "View audit trail";              // audit.view
        public const string AuditExport  = "Export audit trail";            // audit.export
        public const string AuditReview  = "Record audit trail review";     // audit.review.record
        public const string ReportView   = "View compliance reports";       // report.view
        public const string ReportExport = "Export compliance reports";     // report.export
        public const string LegacyImport = "Bulk import legacy masters";    // document.legacy.import

        public static readonly string[] All = { AuditView, AuditExport, AuditReview, ReportView, ReportExport, LegacyImport };
    }
}
