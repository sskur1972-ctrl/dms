using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using DMS.Model.Documents;
using DMS.Web.Services.Authoring;

namespace DMS.Web.Services.Export;

public sealed record AuditReport(IReadOnlyList<TableData> Sheets, int EventCount, int ChangeCount);
public static class AuditReportBuilder
{
    public static bool IsLifecycle(AuditRow r) => new[]{"SIGN","STATE_CHANGE","PUBLISH","MAKE_EFFECTIVE","APPROVE","AUTHORIZE","SUBMIT_FOR_REVIEW","REVIEW_COMPLETE","REVIEW_FORWARD","REVIEW_REJECT","AI_REVIEW","AI_REVIEW_EXPORTED"}.Contains(r.action??"",StringComparer.OrdinalIgnoreCase);
    public static string Roles(string? roles) => string.Join(", ",(roles??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(s=>s.Split('/').Last().Trim()).Select(s=>s.ToUpperInvariant() switch {
        "AUTHOR"=>"Author","REVIEWER"=>"Reviewer","APPROVER"=>"Head of Department","QA_CONTROLLER"=>"QA Controller","TECHNICAL_DIRECTOR"=>"Technical Director","ADMIN"=>"Administrator","OPERATOR"=>"Operator","AUDITOR"=>"Auditor","VIEWER"=>"Viewer",_=>s }));
    public static string Actor(AuditRow r) => string.IsNullOrWhiteSpace(r.actor_full_name)?r.actor_user_name??"System":$"{r.actor_full_name} ({r.actor_user_name})";
    public static string Reference(AuditRow r)=>$"AT-{r.id:00000000}";
    public static string LocalTime(AuditRow r)
    {
        var utc=DateTime.SpecifyKind(r.event_at_utc,DateTimeKind.Utc);TimeZoneInfo zone;
        try{zone=TimeZoneInfo.FindSystemTimeZoneById(r.time_zone_id??"UTC");}catch(TimeZoneNotFoundException){zone=TimeZoneInfo.Utc;}catch(InvalidTimeZoneException){zone=TimeZoneInfo.Utc;}
        var value=new DateTimeOffset(utc).ToOffset(zone.GetUtcOffset(utc));var offset=value.Offset;
        return value.ToString("dd-MMM-yyyy HH:mm:ss.fff",CultureInfo.InvariantCulture)+$" (UTC{(offset<TimeSpan.Zero?"-":"+")}{offset.Duration():hh\\:mm})";
    }
    public static string Reason(AuditRow r)=>string.IsNullOrWhiteSpace(r.reason)?"Not recorded":r.reason;
    public static string Signature(AuditRow r)
    {
        if(!string.IsNullOrWhiteSpace(r.signature_manifestation))return r.signature_manifestation;
        if(r.signature_id==null)return "Not applicable";
        var meaning=Property(r.after_json,"signature_meaning","SignatureMeaning");
        var signer=Property(r.after_json,"signer_printed_name","SignerPrintedName");
        var time=Property(r.after_json,"signed_at_local_display","SignedAtLocalDisplay");
        return string.Join("; ",new[]{meaning,signer,time}.Where(s=>!string.IsNullOrWhiteSpace(s))) is {Length:>0} text?text:"Signed; manifestation not recorded in this event";
    }
    public static string Property(string? json,params string[] names)
    {
        try {using var d=JsonDocument.Parse(json??"{}");if(d.RootElement.ValueKind!=JsonValueKind.Object)return "";
            foreach(var p in d.RootElement.EnumerateObject())if(names.Any(n=>n.Equals(p.Name,StringComparison.OrdinalIgnoreCase)))return p.Value.ValueKind==JsonValueKind.String?p.Value.GetString()??"":p.Value.ToString();}
        catch(JsonException){}return "";
    }
    public static IReadOnlyList<UserAuditPresentation.Change> Changes(AuditRow r) => IsLifecycle(r)?[]:UserAuditPresentation.Changes(r.before_json,r.after_json)
        .Where(c=>!new[]{"Authored","Numbering mode","Content availability","Reauthenticated","Reauth method"}.Contains(c.Label,StringComparer.OrdinalIgnoreCase))
        .Where(c=>!c.Label.StartsWith("Content change",StringComparison.OrdinalIgnoreCase) || NormalizeText(c.OldMissing?"":c.OldValue)!=NormalizeText(c.NewMissing?"":c.NewValue))
        .OrderBy(c=>Regex.Replace(c.Label,@"\d+",m=>m.Value.PadLeft(10,'0')),StringComparer.OrdinalIgnoreCase).ToArray();
    static string State(string value)=>value.ToUpperInvariant() switch {"DRAFT"=>"Draft","UNDER_REVIEW"=>"Under review","UNDER_APPROVAL"=>"Under approval","APPROVED"=>"Approved","PUBLISHED"=>"Published","EFFECTIVE"=>"Effective","OBSOLETE"=>"Obsolete","ARCHIVED"=>"Archived",_=>value};
    static string NormalizeText(string text)=>Regex.Replace(new string(text.Where(c=>!"•◦▪‣\uf0a7\uf0b7".Contains(c)).ToArray()),@"\s+"," ").Trim();
    public static string Description(AuditRow r)
    {
        if(string.Equals(r.action,"SIGN",StringComparison.OrdinalIgnoreCase)) {
            var action=Property(r.after_json,"action_code","ActionCode");return string.IsNullOrEmpty(action)?"Electronic signature applied":UserAuditPresentation.Words(action);
        }
        if(string.Equals(r.action,"STATE_CHANGE",StringComparison.OrdinalIgnoreCase))return "Document status changed";
        return UserAuditPresentation.Words(r.action);
    }
    public static AuditReport Build(IReadOnlyList<AuditRow> input,AuditFilter filter,string preparedBy,DateTime generatedUtc)
    {
        var rows=input.Where(r=>UserAuditPresentation.Visible(r.action,r.entity_type)).ToArray();
        var common=new List<string>{ $"Generated {generatedUtc:dd-MMM-yyyy HH:mm:ss} UTC by {preparedBy}",
            $"Period: {filter.From?.ToString("dd-MMM-yyyy")??"Beginning of available history"} to {filter.To?.ToString("dd-MMM-yyyy")??"Latest available event"}. Scope: {(filter.Scope=="ALL"?"All business activity":"Document business activity")}. Newest first.",
            $"Source: DMS immutable audit trail. {rows.Length:N0} events selected. Audit references link related entries across this extract.",
            "Not recorded indicates missing historical evidence. Multi-part values continue under the same reference and field; no value is truncated." };
        TableData Sheet(string title,params string[] columns)=>new(){Title=title,Heading=new(common),Columns=columns.ToList()};
        var changes=Sheet("Document changes","Audit reference","Date/time (UTC)","Date/time (local, offset)","Document / revision","Field / location","Old Value","New Value","Changed by","Role at event","Reason for change","Electronic signature");
        var events=Sheet("Lifecycle and signatures","Audit reference","Date/time (UTC)","Date/time (local, offset)","Document / revision","Action","Previous status","New status","Performed by","Role at event","Reason / comment","Electronic signature");
        var other=Sheet("Other business activity","Audit reference","Date/time (UTC)","Date/time (local, offset)","Record","Action","Performed by","Role at event","Reason / comment");
        int count=0;
        foreach(var r in rows)
        {
            var revision=r.document_revision??Property(r.after_json,"VersionNo","version_no");
            var document=UserAuditPresentation.RecordKey(r.document_number,r.entity_key)+(string.IsNullOrWhiteSpace(revision)?"":" / "+revision);
            var fields=Changes(r);
            if(r.document_id==null || new[]{"READ","LOGIN","LOGOUT","EXPORT"}.Contains(r.action??"",StringComparer.OrdinalIgnoreCase)) {
                other.Rows.Add([Reference(r),r.event_at_utc,LocalTime(r),document,Description(r),Actor(r),Roles(r.actor_role_codes),Reason(r)]);continue;
            }
            if(IsLifecycle(r)) {
                var oldState=State(Property(r.before_json,"State","state_code","CurrentState","state"));var newState=State(Property(r.after_json,"State","state_code","CurrentState","state"));
                events.Rows.Add([Reference(r),r.event_at_utc,LocalTime(r),document,Description(r),oldState,newState,Actor(r),Roles(r.actor_role_codes),Reason(r),Signature(r)]);continue;
            }
            if(fields.Count==0) {
                // A saved event is retained even when historic readable values are unavailable.
                var noChanges=(r.before_json?.Trim()=="{}" && r.after_json?.Trim()=="{}");
                var savedFile=string.Equals(r.action,"OFFICE_EDIT_SAVED",StringComparison.OrdinalIgnoreCase)||string.Equals(r.action,"CONTENT_UPDATED",StringComparison.OrdinalIgnoreCase);
                events.Rows.Add([Reference(r),r.event_at_utc,LocalTime(r),document,savedFile?"Document file saved; no readable value changes recorded":Description(r)+(noChanges?"; no readable field values changed":"; readable field values not recorded"),null,null,Actor(r),Roles(r.actor_role_codes),Reason(r),Signature(r)]);continue;
            }
            foreach(var field in fields) {
                count++;
                var oldParts=Chunks(field.OldValue);var newParts=Chunks(field.NewValue);var parts=Math.Max(oldParts.Count,newParts.Count);
                for(int i=0;i<parts;i++)changes.Rows.Add([Reference(r),r.event_at_utc,LocalTime(r),document,Description(r)+"\n"+field.Label+(parts>1?$" (part {i+1} of {parts})":""),i<oldParts.Count?oldParts[i]:"",i<newParts.Count?newParts[i]:"",Actor(r),Roles(r.actor_role_codes),Reason(r),Signature(r)]);
            }
            if(changes.Rows.Count>200000)throw new InvalidOperationException("This extract contains more than 200,000 change rows. Narrow the document or date filters.");
        }
        changes.Heading[2]=$"Source: DMS audit trail. {count:N0} recorded field changes across {changes.Rows.Select(r=>r[0]).Distinct().Count():N0} change events. {changes.Rows.Count:N0} output rows, including continuations.";
        events.Heading[2]=$"Source: DMS audit trail. {events.Rows.Count:N0} lifecycle, signature or saved-record events. Audit references identify the original entries.";
        var sheets=new List<TableData>{changes,events};if(other.Rows.Count>0)sheets.Add(other);
        return new(sheets,rows.Length,count);
    }
    static IReadOnlyList<string> Chunks(string text) {
        var result=new List<string>();for(int start=0;start<text.Length;){int length=Math.Min(900,text.Length-start);if(start+length<text.Length&&char.IsHighSurrogate(text[start+length-1]))length--;result.Add(text.Substring(start,length));start+=length;}return result.Count==0?[""]:result;
    }
}

