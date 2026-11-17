using System.Text.Json;
using System.Text.RegularExpressions;
using DMS.Model.Documents;

namespace DMS.Web.Services.Authoring;

/// <summary>One presentation for the audit screen, detail and every export.</summary>
public static class UserAuditPresentation
{
    public sealed record Change(string Label, string OldValue, string NewValue, bool OldMissing=false, bool NewMissing=false);
    public static bool Visible(string? action, string? entity) =>
        !UserAuditPolicy.TechnicalEntities.Contains(entity ?? "", StringComparer.OrdinalIgnoreCase) &&
        !UserAuditPolicy.TechnicalActions.Any(p => Regex.IsMatch(action ?? "", "^" + Regex.Escape(p).Replace("%", ".*").Replace("_", ".") + "$", RegexOptions.IgnoreCase));

    public static string Words(string? value) => value?.ToUpperInvariant() switch {
        "OFFICE_EDIT_SAVED" => "Document saved", "CONTENT_UPDATED" => "Document content updated",
        "SUBMIT_FOR_REVIEW" => "Submitted for review", "MAKE_EFFECTIVE" => "Made effective",
        "AI_REVIEW" => "Advisory compliance review", "REVIEW_COMPLETE" => "Review completed",
        "REVIEW_FORWARD" => "Review forwarded", "UPDATE" => "Updated", "CREATE" => "Created",
        "REVIEW_REJECT" => "Review rejected", "APPROVAL_REJECT" => "Approval rejected", "DELETE" => "Deleted",
        "PUBLISH" => "Published", "AUTHORIZE" => "Authorized", "APPROVE" => "Approved",
        "READ" => "Document viewed", "OFFICE_TEMPLATE_SAVED" => "Template content saved",
        _ => string.IsNullOrWhiteSpace(value) ? "—" : char.ToUpperInvariant(value[0]) + value[1..].Replace('_', ' ').ToLowerInvariant()
    };

    // Do not expose fingerprints, keys, paths or implementation diagnostics even in legacy free text.
    public static string Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        var s = Regex.Replace(value, @"\b(?:SHA[-_ ]?256|hash|fingerprint)\s*[:=]?\s*[a-fA-F0-9]{32,128}\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b[a-fA-F0-9]{32,128}\b|\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b", "");
        return string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
    }

    static bool Technical(string name)
    {
        // Contextual document text is business evidence, even in a heading such as "Shared equipment".
        if(name.StartsWith("Content change",StringComparison.OrdinalIgnoreCase)||name.StartsWith("Paragraph_",StringComparison.OrdinalIgnoreCase))return false;
        var n = Regex.Replace(name, "[_-]", "").ToLowerInvariant();
        return n.EndsWith("id") || n.EndsWith("ids") ||
            new[] { "sha", "hash", "password", "secret", "token", "credential", "apikey", "salt", "privatekey", "path", "session", "provider", "engine", "rowversion", "sizebytes", "mime", "replacedbyupload", "bodychanges", "correlation", "documentkey", "url" }.Any(n.Contains);
    }

    public static IReadOnlyList<Change> Changes(string? before, string? after)
    {
        var b = Parse(before); var a = Parse(after); var rows = new List<Change>();
        // Historical full-content snapshots are displayed as changed spans without rewriting evidence.
        if(b.TryGetValue("DocumentContent",out var oldBody) && a.TryGetValue("DocumentContent",out var newBody))
        {
            string? Html(JsonElement e) => e.ValueKind==JsonValueKind.String ? string.Join("",(e.GetString()??"").Split('\n').Select(s=>"<p>"+System.Net.WebUtility.HtmlEncode(s)+"</p>")) : null;
            var delta=SavedContentAudit.Changes(Html(oldBody),null,Html(newBody),null);
            b.Remove("DocumentContent");a.Remove("DocumentContent");
            foreach(var p in Parse(delta.Before))b[p.Key]=p.Value;
            foreach(var p in Parse(delta.After))a[p.Key]=p.Value;
        }
        foreach (var key in b.Keys.Union(a.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (Technical(key)) continue;
            if((a.ContainsKey("DocumentContent") || b.ContainsKey("DocumentContent")) && key.StartsWith("Paragraph_",StringComparison.OrdinalIgnoreCase)) continue;
            var hasOld=b.TryGetValue(key,out var old); var hasNew=a.TryGetValue(key,out var next);
            if (hasOld && hasNew && old.GetRawText()==next.GetRawText()) continue;
            if ((!hasOld || old.ValueKind==JsonValueKind.Null) && (!hasNew || next.ValueKind==JsonValueKind.Null)) continue;
            rows.Add(new(Label(key), hasOld ? Value(old) : "Not recorded", hasNew ? Value(next) : "Not recorded",
                !hasOld || old.ValueKind==JsonValueKind.Null,!hasNew || next.ValueKind==JsonValueKind.Null));
        }
        return rows;
    }

    static Dictionary<string,JsonElement> Parse(string? json)
    {
        try {
            using var d=JsonDocument.Parse(json ?? "{}");
            var result=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
            if(d.RootElement.ValueKind==JsonValueKind.Object)
                foreach(var p in d.RootElement.EnumerateObject())result[p.Name]=p.Value.Clone();
            return result;
        }
        catch(JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }
    static string Label(string key) => key.Equals("DocumentContent",StringComparison.OrdinalIgnoreCase) ? "Document content" : AuditChangeRules.Label(key);
    static string Value(JsonElement e) => e.ValueKind switch {
        JsonValueKind.Null => "Not set", JsonValueKind.True => "Yes", JsonValueKind.False => "No",
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.Array => string.Join("; ",e.EnumerateArray().Select(Value)),
        JsonValueKind.Object => string.Join("; ",e.EnumerateObject().Where(p=>!Technical(p.Name)).Select(p=>$"{Label(p.Name)}: {Value(p.Value)}")),
        _ => "Not recorded"
    };
    public static string Detail(string? action, IReadOnlyList<Change> changes) => changes.Count>0
        ? $"{changes.Count} value{(changes.Count==1?"":"s")} changed: " + string.Join(", ",changes.Take(3).Select(c=>c.Label)) + (changes.Count>3?$" and {changes.Count-3} more.":".")
        : Words(action) + ".";

    public static string RecordKey(string? document,string? key)
    {
        if(!string.IsNullOrWhiteSpace(document))return document;
        if(string.IsNullOrWhiteSpace(key) || long.TryParse(key,out _) || Guid.TryParse(key,out _) || Regex.IsMatch(key,"^[a-fA-F0-9]{32,128}$"))return "—";
        return Text(key);
    }
}
