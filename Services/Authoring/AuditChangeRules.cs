//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Globalization;
using System.Text.Json;

namespace DMS.Web.Services.Authoring;

/// <summary>
/// What actually changed, field by field, from an audit row's before/after JSON.
/// </summary>
/// <remarks>
/// <para>
/// AUDIT_TRAIL has carried <c>before_json</c> and <c>after_json</c> since V003,
/// but nothing ever read them apart from the detail page, which printed the two
/// blobs side by side and left the reader to spot the difference. That is not
/// what an inspector asks for: they ask what the value was, what it became, who
/// changed it and why. This turns the pair into exactly that.
/// </para>
/// <para>
/// <b>It reads the stored JSON and nothing else.</b> It does not use the stored
/// <c>changed_fields</c> column, and it does not need to: that column is a
/// convenience index written at insert time, it is part of the hashed row, and
/// re-deriving the answer from the values themselves means the report cannot
/// disagree with the evidence it is reporting on. Rows written before this class
/// existed produce correct output for the same reason.
/// </para>
/// <para>
/// <b>Pure, and therefore testable.</b> No clock, no database, no formatting
/// culture from the ambient thread. Every judgement about what a change looks
/// like is in here rather than spread across a report, a view and an export.
/// </para>
/// </remarks>
public static class AuditChangeRules
{
    /// <summary>
    /// The longest value the report will print before cutting it short.
    /// </summary>
    /// <remarks>
    /// A change summary or a rejection remark can run to a paragraph. The whole
    /// value is always still in <c>before_json</c>/<c>after_json</c> on the
    /// detail page - this cap is about a table staying readable, not about
    /// withholding anything, which is why a truncated value is marked as one.
    /// </remarks>
    public const int MaxValueLength = 400;

    /// <summary>Shown where a value did not exist before.</summary>
    public const string NotSet = "(not set)";

    /// <summary>Shown where a value was removed.</summary>
    public const string Cleared = "(cleared)";

    /// <summary>Shown where one side of the pair does not carry the field at all.</summary>
    public const string NotRecorded = "(not recorded)";

    /// <summary>Stands in for a value that must never be written into a report.</summary>
    public const string Withheld = "(withheld)";

    /// <summary>
    /// Field names whose value is never printed, matched case-insensitively as a
    /// substring.
    /// </summary>
    /// <remarks>
    /// Nothing in this application puts a credential into an audit payload, and
    /// it would be a defect if it did. This is here because the audit trail is
    /// append-only: a secret written into it once cannot be taken out again, and
    /// the report is the thing most likely to be exported, mailed and printed.
    /// The field NAME is still reported, so the change remains visible - only
    /// the value is held back.
    /// </remarks>
    private static readonly string[] NeverPrint =
    {
        "password", "secret", "token", "credential", "apikey", "api_key", "salt", "privatekey"
    };

    /// <summary>
    /// Names that read badly when split from their casing, or that have a
    /// conventional spelling worth keeping.
    /// </summary>
    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sha256"] = "SHA-256",
        ["SourceSha256"] = "Source SHA-256",
        ["RenditionSha256"] = "Rendition SHA-256",
        ["FileSha256"] = "File SHA-256",
        ["Ocr"] = "OCR",
        ["OcrStatus"] = "OCR status",
        ["Url"] = "URL",
        ["Ip"] = "IP address",
        ["IpAddress"] = "IP address",
        ["Sla"] = "SLA",
        ["Id"] = "Id",
        ["ChangeControlId"] = "Change control",
        ["ChangeControlNumber"] = "Change control number",
        ["DocumentTypeId"] = "Document type",
        ["DepartmentId"] = "Department",
        ["AssignedUserId"] = "Assigned to",
        ["OwnerUserId"] = "Owner",
        ["ControllerUserId"] = "QA controller",
        ["CurrentStateId"] = "State",
        ["EffectiveFrom"] = "Effective from",
        ["PeriodicReviewDueOn"] = "Periodic review due",
        ["VersionNo"] = "Revision",
        ["TempRevisionNo"] = "Temporary revision",
    };

    /// <summary>How a field's value moved.</summary>
    public enum ChangeKind
    {
        /// <summary>There was no previous value; one was set.</summary>
        Set,

        /// <summary>A value was replaced by a different one.</summary>
        Changed,

        /// <summary>There was a value; now there is none.</summary>
        Cleared,
    }

    /// <summary>One field that changed, ready to print.</summary>
    /// <param name="Field">The property name as stored, for filtering and for the machine.</param>
    /// <param name="Label">The same name, made readable, for the human.</param>
    /// <param name="OldValue">The previous value, already formatted and capped.</param>
    /// <param name="NewValue">The new value, already formatted and capped.</param>
    /// <param name="Kind">Set, Changed or Cleared.</param>
    public sealed record FieldChange(string Field, string Label, string OldValue, string NewValue, ChangeKind Kind);

    /// <summary>
    /// Every field that differs between the two payloads, in a stable order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ordered alphabetically by label</b>, and that is a deliberate second
    /// choice. The obvious order would be the order the properties were written
    /// at the call site - somebody chose it, so it means something. That order
    /// does not survive storage: <c>before_json</c> and <c>after_json</c> are
    /// MySQL <c>JSON</c> columns, and MySQL normalises an object into its own
    /// internal key order on the way in. A payload written as
    /// <c>{Title, Keywords, Summary, ChangeSummary}</c> comes back as
    /// <c>{Title, Summary, Keywords, ChangeSummary}</c>. Since the authored
    /// order is already gone by the time anything reads the row, the choice is
    /// between MySQL's order - which looks arbitrary to a reader, because it
    /// sorts by key length first - and one a person can predict. Alphabetical
    /// is predictable, and it makes two reports of the same event comparable
    /// line by line.
    /// </para>
    /// <para>
    /// Returns an empty list - never null - when there is nothing to show: no
    /// payloads, malformed JSON, payloads that are not objects, or two payloads
    /// that agree. A report row is then produced for the event itself, so the
    /// event does not vanish; see the report builder.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<FieldChange> Extract(string? beforeJson, string? afterJson)
    {
        var before = ParseObject(beforeJson);
        var after = ParseObject(afterJson);

        if (before is null && after is null) return Array.Empty<FieldChange>();

        var changes = new List<FieldChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (after is not null)
        {
            foreach (var property in after.Value.EnumerateObject())
            {
                seen.Add(property.Name);

                JsonElement? previousValue = null;
                if (before is not null && before.Value.TryGetProperty(property.Name, out var found))
                    previousValue = found;

                // Identical raw text means an identical value, whatever the type.
                if (previousValue is not null &&
                    string.Equals(previousValue.Value.GetRawText(), property.Value.GetRawText(), StringComparison.Ordinal))
                {
                    continue;
                }

                // Nothing before and nothing after is not a change. This happens
                // on a creation where an optional field was left empty.
                if (IsAbsent(previousValue) && IsAbsent(property.Value)) continue;

                var oldText = previousValue is null
                    ? (before is null ? NotRecorded : NotSet)
                    : Format(property.Name, previousValue.Value, NotSet);

                var kind = IsAbsent(previousValue) ? ChangeKind.Set
                    : IsAbsent(property.Value) ? ChangeKind.Cleared
                    : ChangeKind.Changed;

                changes.Add(new FieldChange(
                    property.Name,
                    Label(property.Name),
                    oldText,
                    Format(property.Name, property.Value, Cleared),
                    kind));
            }
        }

        if (before is not null)
        {
            foreach (var property in before.Value.EnumerateObject())
            {
                if (seen.Contains(property.Name)) continue;
                if (IsAbsent(property.Value)) continue;

                // The two payloads describe different shapes. That is legitimate -
                // several call sites record a narrow "before" and a wider "after" -
                // so the field is reported with the honest statement that the other
                // side did not carry it, rather than being claimed as cleared.
                changes.Add(new FieldChange(
                    property.Name,
                    Label(property.Name),
                    Format(property.Name, property.Value, NotSet),
                    NotRecorded,
                    ChangeKind.Changed));
            }
        }

        // Sorted here rather than at each call site, so the report, the audit
        // list, the document history and the CSV all agree on the order. See
        // the remark above for why the authored order is not available.
        changes.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));

        return changes;
    }

    /// <summary>A one-line summary of a change set, for a list view.</summary>
    /// <remarks>
    /// Names at most <paramref name="maxFields"/> fields and then counts the
    /// rest, so a row that changed twelve things says so instead of wrapping
    /// over four lines.
    /// </remarks>
    public static string Summarise(IReadOnlyList<FieldChange> changes, int maxFields = 3)
    {
        if (changes.Count == 0) return string.Empty;

        var named = changes.Take(maxFields).Select(c => c.Label);
        var summary = string.Join(", ", named);

        var remaining = changes.Count - maxFields;
        return remaining > 0 ? $"{summary} and {remaining} more" : summary;
    }

    // -------------------------------------------------------------------------

    private static JsonElement? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            // Clone: the JsonDocument is disposed at the end of this method and
            // an un-cloned element would be reading freed memory afterwards.
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            // Malformed JSON in an append-only table cannot be repaired, and
            // throwing here would take out the whole report for one bad row.
            return null;
        }
    }

    private static bool IsAbsent(JsonElement? value)
        => value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static bool IsAbsent(JsonElement value)
        => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    /// <summary>Turns one JSON value into the text the report prints.</summary>
    private static string Format(string field, JsonElement value, string whenAbsent)
    {
        foreach (var forbidden in NeverPrint)
        {
            if (field.Contains(forbidden, StringComparison.OrdinalIgnoreCase)) return Withheld;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => whenAbsent,

            // Yes/No rather than true/false: this report is read by QA and by
            // inspectors. The literal JSON is still on the audit detail page and
            // in the row itself, so nothing is lost by making the table readable.
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",

            JsonValueKind.Number => value.GetRawText(),

            JsonValueKind.String => Cap(value.GetString() ?? string.Empty, whenAbsent),

            // An object or an array is rare in these payloads. Printed as compact
            // JSON rather than flattened: inventing a shape for it here would be
            // guessing, and the raw text is at least exactly what was recorded.
            _ => Cap(value.GetRawText(), whenAbsent),
        };
    }

    private static string Cap(string text, string whenEmpty)
    {
        if (text.Length == 0) return whenEmpty;

        // Newlines turn one table row into several. The value is reproduced
        // faithfully on the detail page; here it is put onto one line.
        var single = text.ReplaceLineEndings(" ").Trim();
        if (single.Length == 0) return whenEmpty;

        return single.Length <= MaxValueLength
            ? single
            : single[..MaxValueLength] + $"… (+{single.Length - MaxValueLength} characters)";
    }

    /// <summary>PascalCase or snake_case into something a person reads.</summary>
    internal static string Label(string field)
    {
        if (string.IsNullOrWhiteSpace(field)) return field;
        if (KnownLabels.TryGetValue(field, out var known)) return known;

        var spaced = new System.Text.StringBuilder(field.Length + 8);

        for (var i = 0; i < field.Length; i++)
        {
            var c = field[i];

            if (c is '_' or '-')
            {
                spaced.Append(' ');
                continue;
            }

            // A capital starts a new word, unless it is part of a run of them
            // (SHA256, ID) or the very first character.
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(field[i - 1]))
                spaced.Append(' ');

            spaced.Append(c);
        }

        var text = spaced.ToString().Trim();
        if (text.Length == 0) return field;

        // "Change control number", not "Change Control Number": sentence case
        // reads as prose in a table, which is what the surrounding columns are.
        return char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..].ToLower(CultureInfo.InvariantCulture);
    }
}
