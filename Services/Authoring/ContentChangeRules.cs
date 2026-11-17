//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Globalization;

namespace DMS.Web.Services.Authoring;

/// <summary>
/// What the author actually changed in the body of a document, as paragraphs of
/// old and new text ready to be written into an audit row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A commit from the in-application editor used to be
/// audited by its fingerprints: the SHA-256 of the old body, the SHA-256 of the
/// new body, the file name and the size. That is enough to prove the body
/// changed and enough to detect tampering, and it is not enough for the
/// question people actually ask of an audit trail - <i>what</i> changed. A
/// reader of the trail was told two hex strings differed and sent to the
/// Compare screen to find out why. This records the answer in the row itself.
/// </para>
/// <para>
/// <b>Paragraphs, not the whole body.</b> The alternative - storing both
/// complete bodies - would put a copy of the document into an append-only,
/// hash-chained table on every save, and a document revised twenty times would
/// occupy the trail twenty-one times over. It would also be unreadable: the
/// answer to "what changed" is not "here are two documents". So what is
/// recorded is the paragraphs that differ, each with the text either side of
/// the change, and nothing else.
/// </para>
/// <para>
/// <b>It is bounded, and it says when it has been bounded.</b> Every limit
/// below has a counted overflow that is reported rather than dropped silently,
/// because an audit record that has quietly left something out is worse than
/// one that says how much it left out. The .docx and the stored body are both
/// still there in full, and the Compare screen still reads them, so nothing
/// recorded here is the only copy of anything.
/// </para>
/// <para>
/// <b>Pure.</b> No clock, no database, no HTML parser beyond the one
/// <see cref="TextDiffRules"/> already uses for the Compare screen - which
/// matters, because the two must not be able to disagree about what a document
/// says.
/// </para>
/// </remarks>
public static class ContentChangeRules
{
    /// <summary>
    /// The most changed paragraphs written into one audit row.
    /// </summary>
    /// <remarks>
    /// Twenty-five is about as many rows as a person reads off a change table
    /// before they stop reading it. Past that, an edit is a rewrite, and the
    /// honest record of a rewrite is that it was one - which is what the
    /// summary says.
    /// </remarks>
    public const int MaxBlocks = 25;

    /// <summary>
    /// The most characters one recorded paragraph occupies, mark included.
    /// </summary>
    /// <remarks>
    /// Tied to <see cref="AuditChangeRules.MaxValueLength"/> on purpose. That
    /// is the width the audit report and the audit detail page cut a value to,
    /// and a paragraph stored any longer than it would be truncated a second
    /// time on the way out - producing a value carrying two truncation marks,
    /// the second of them understating how much had actually been left out.
    /// One cut, in one place, with one honest count.
    /// </remarks>
    public const int MaxBlockLength = AuditChangeRules.MaxValueLength;

    /// <summary>
    /// The budget of body text one audit row will spend before it stops taking
    /// further paragraphs.
    /// </summary>
    /// <remarks>
    /// Checked BEFORE a paragraph is recorded rather than while it is being
    /// recorded, so a paragraph is never half-written into the row: once the
    /// budget is spent the rest are counted as omitted and said to be. The
    /// consequence is that the last paragraph accepted can carry the row past
    /// this figure - by its own two capped sides at most, which is what
    /// <see cref="MaximumRecordedCharacters"/> states.
    /// </remarks>
    public const int MaxTotalCharacters = 12_000;

    /// <summary>
    /// Room set aside within <see cref="MaxBlockLength"/> for the
    /// "… (+N characters)" mark on a paragraph that was cut short. Thirty-two
    /// covers the longest mark a body of any size this application will store
    /// can produce.
    /// </summary>
    private const int TruncationMarkerAllowance = 32;

    /// <summary>How much of the paragraph itself survives a cut.</summary>
    private const int BodyAllowance = MaxBlockLength - TruncationMarkerAllowance;

    /// <summary>
    /// The hard ceiling on body text in one audit row: the budget, plus the one
    /// paragraph that was in progress when the budget ran out.
    /// </summary>
    public const int MaximumRecordedCharacters = MaxTotalCharacters + 2 * MaxBlockLength;

    /// <summary>
    /// Above this many paragraphs a side, the paragraph-level comparison is not
    /// attempted and only the summary is recorded.
    /// </summary>
    /// <remarks>
    /// The comparison is a longest-common-subsequence over a matrix of
    /// paragraphs: fifteen hundred a side is around two and a quarter million
    /// cells, which is a few megabytes and a few milliseconds. Four thousand a
    /// side - the Compare screen's limit, where a reviewer has asked for the
    /// answer and is waiting for it - is sixteen million, and this runs on the
    /// save path of somebody who only pressed Save. The limit is lower here on
    /// purpose.
    /// </remarks>
    public const int MaxLinesForBlockDiff = 1500;

    /// <summary>Key prefix for a paragraph that was added or rewritten.</summary>
    private const string ChangedPrefix = "Paragraph_";

    /// <summary>Key prefix for a paragraph that was taken out.</summary>
    private const string RemovedPrefix = "Paragraph_removed_";

    /// <summary>
    /// One paragraph that differs, keyed so it can be written straight into the
    /// before and after payloads as a field of its own.
    /// </summary>
    /// <param name="Key">
    /// The property name to record under. Numbered and zero-padded so the audit
    /// report - which sorts its rows by label - lists the paragraphs of a
    /// document in the order they appear in it rather than as 1, 10, 11, 2.
    /// </param>
    /// <param name="OldText">The paragraph as it was, or null where it is new.</param>
    /// <param name="NewText">The paragraph as it is now, or null where it was removed.</param>
    public sealed record BodyChange(string Key, string? OldText, string? NewText);

    /// <summary>Everything that differs between two bodies, and the counts behind it.</summary>
    /// <param name="Changes">The paragraphs recorded, already capped and truncated.</param>
    /// <param name="Added">Paragraphs in the new body that were not in the old one.</param>
    /// <param name="Removed">Paragraphs in the old body that are not in the new one.</param>
    /// <param name="Modified">Paragraphs present in both but reworded.</param>
    /// <param name="Omitted">Changed paragraphs that did not fit within the caps.</param>
    /// <param name="Measured">
    /// False when the bodies were too large to compare paragraph by paragraph,
    /// in which case the counts are zero and only the note is meaningful.
    /// </param>
    public sealed record BodyChangeSet(
        IReadOnlyList<BodyChange> Changes,
        int Added,
        int Removed,
        int Modified,
        int Omitted,
        bool Measured)
    {
        /// <summary>Whether anything at all differs.</summary>
        public bool HasChanges => Added > 0 || Removed > 0 || Modified > 0;
    }

    private static readonly BodyChangeSet Unmeasured =
        new(Array.Empty<BodyChange>(), 0, 0, 0, 0, Measured: false);

    /// <summary>
    /// The paragraphs that differ between the previously committed body and the
    /// one being committed.
    /// </summary>
    /// <remarks>
    /// Never throws. A body that cannot be compared - because it is enormous,
    /// or because the text extraction failed on it - comes back as an unmeasured
    /// set, and the caller still records the fingerprints it always did. A
    /// defect in a change report must not be able to fail a save and lose an
    /// author's work.
    /// </remarks>
    public static BodyChangeSet Compare(string? oldHtml, string? newHtml)
    {
        try
        {
            var left = TextDiffRules.ToLines(oldHtml);
            var right = TextDiffRules.ToLines(newHtml);

            if (left.Count > MaxLinesForBlockDiff || right.Count > MaxLinesForBlockDiff) return Unmeasured;

            var rows = TextDiffRules.Compare(left, right);
            return Collect(rows);
        }
        catch (Exception)
        {
            // Deliberately broad. This runs on the save path; there is no
            // failure here worth losing a document over, and the caller records
            // the hashes either way.
            return Unmeasured;
        }
    }

    /// <summary>
    /// A sentence describing the change set, for the summary field and for a
    /// list view that has no room for the paragraphs themselves.
    /// </summary>
    public static string Describe(BodyChangeSet set)
    {
        if (!set.Measured)
            return "The document is too large to record paragraph by paragraph; " +
                   "use Compare to see the difference between the revisions.";

        if (!set.HasChanges)
            return "No change to the text of the document.";

        var parts = new List<string>(3);

        if (set.Modified > 0) parts.Add($"{set.Modified} paragraph{S(set.Modified)} reworded");
        if (set.Added > 0) parts.Add($"{set.Added} added");
        if (set.Removed > 0) parts.Add($"{set.Removed} removed");

        var text = string.Join(", ", parts) + ".";

        if (set.Omitted > 0)
            text += $" {set.Omitted} further changed paragraph{S(set.Omitted)} " +
                    (set.Omitted == 1 ? "is" : "are") +
                    " not listed individually here; use Compare to see the whole difference.";

        return text;
    }

    // -------------------------------------------------------------------------

    private static string S(int count) => count == 1 ? string.Empty : "s";

    /// <summary>
    /// Turns the row-by-row diff into paragraph changes, pairing a removal with
    /// the addition that replaced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reworded paragraph comes out of a longest-common-subsequence walk as
    /// two rows - the old one removed, the new one added - because the algorithm
    /// matches whole lines and a reworded line matches nothing. Reported that
    /// way it reads as a deletion and an unrelated insertion, and the reader has
    /// to work out for themselves that they are the same paragraph. So a run of
    /// consecutive changed rows is paired off in order: the first removal with
    /// the first addition, and so on. Whatever is left over at the end of a run
    /// genuinely was only added, or only removed.
    /// </para>
    /// <para>
    /// The pairing is a presentation choice and is allowed to be wrong about
    /// which paragraph replaced which - that is the same judgement the Compare
    /// screen makes, and both sides of every pair are recorded verbatim, so
    /// nothing is hidden by getting it wrong.
    /// </para>
    /// </remarks>
    private static BodyChangeSet Collect(IReadOnlyList<DiffRow> rows)
    {
        var changes = new List<BodyChange>(MaxBlocks);
        int added = 0, removed = 0, modified = 0, omitted = 0, budget = MaxTotalCharacters;

        var pendingRemovals = new List<DiffRow>();
        var pendingAdditions = new List<DiffRow>();

        void Record(string key, string? oldText, string? newText)
        {
            if (changes.Count >= MaxBlocks || budget <= 0)
            {
                omitted++;
                return;
            }

            var cappedOld = Cap(oldText);
            var cappedNew = Cap(newText);

            budget -= (cappedOld?.Length ?? 0) + (cappedNew?.Length ?? 0);

            changes.Add(new BodyChange(key, cappedOld, cappedNew));
        }

        void Flush()
        {
            var pairs = Math.Min(pendingRemovals.Count, pendingAdditions.Count);

            for (var i = 0; i < pairs; i++)
            {
                modified++;
                Record(Key(ChangedPrefix, pendingAdditions[i].RightLineNo),
                    pendingRemovals[i].Left, pendingAdditions[i].Right);
            }

            for (var i = pairs; i < pendingAdditions.Count; i++)
            {
                added++;
                Record(Key(ChangedPrefix, pendingAdditions[i].RightLineNo), null, pendingAdditions[i].Right);
            }

            for (var i = pairs; i < pendingRemovals.Count; i++)
            {
                removed++;
                Record(Key(RemovedPrefix, pendingRemovals[i].LeftLineNo), pendingRemovals[i].Left, null);
            }

            pendingRemovals.Clear();
            pendingAdditions.Clear();
        }

        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case DiffKind.Removed:
                    pendingRemovals.Add(row);
                    break;

                case DiffKind.Added:
                    pendingAdditions.Add(row);
                    break;

                default:
                    Flush();
                    break;
            }
        }

        Flush();

        return new BodyChangeSet(changes, added, removed, modified, omitted, Measured: true);
    }

    /// <summary>
    /// Zero-padded to four digits so an alphabetical sort of the labels is also
    /// a sort by position in the document. A document with more than 9,999
    /// paragraphs sorts imperfectly past that point and is still correct.
    /// </summary>
    private static string Key(string prefix, int? lineNumber)
        => prefix + (lineNumber ?? 0).ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Truncates one paragraph to its own limit, marking the cut so the reader
    /// knows it happened and how much was left out.
    /// </summary>
    /// <remarks>
    /// Never cuts to nothing. A paragraph that is recorded at all is recorded
    /// with enough of itself to be recognised, which is why the row's total
    /// budget is enforced by refusing the NEXT paragraph rather than by
    /// shortening this one down to whatever is left.
    /// </remarks>
    private static string? Cap(string? text)
    {
        if (text is null || text.Length <= MaxBlockLength) return text;

        return text[..BodyAllowance] + $"… (+{text.Length - BodyAllowance} characters)";
    }
}

/// <summary>
/// Builds the audit payloads for a document body change, whichever route
/// changed it.
/// </summary>
/// <remarks>
/// <para>
/// There are four ways a controlled document's content changes - the
/// in-application editor, the Office editor, an uploaded replacement file, and a
/// revision carried forward - and each used to write its own audit payload. The
/// authored one recorded paragraphs; the others recorded two hashes. A reader of
/// the trail therefore got a different quality of answer depending on which
/// button the author had happened to press, which is not a defensible thing for
/// an audit trail to do.
/// </para>
/// <para>
/// This is the one place those payloads are built, so every route produces the
/// same Field / Old value / New value rows through the same rules, and a change
/// to how content is reported cannot reach three of the four routes and miss one.
/// </para>
/// </remarks>
public static class ContentAuditPayload
{
    /// <summary>
    /// The before and after payloads for a content change.
    /// </summary>
    /// <param name="previousText">
    /// The body as it was, in the HTML-ish shape <see cref="ContentChangeRules"/>
    /// reads. Null where it could not be read - a PDF, a scan, a damaged package,
    /// or a first save with nothing before it.
    /// </param>
    /// <param name="newText">The body as it now is, same shape.</param>
    /// <param name="before">Fields describing the previous file, always recorded.</param>
    /// <param name="after">Fields describing the new file, always recorded.</param>
    /// <returns>
    /// The two payloads and the change set, so the caller can also put the
    /// summary in the action detail - which is the line the audit LIST shows.
    /// </returns>
    /// <remarks>
    /// <b>The fingerprints are never dropped.</b> Whatever can be said about the
    /// text, the SHA-256 and the size stay in the payload: they are what proves
    /// the record has not been altered since, and the paragraphs are an
    /// explanation of a change rather than evidence of one.
    /// </remarks>
    public static ContentAuditPayloadResult Build(
        string? previousText,
        string? newText,
        IReadOnlyDictionary<string, object?> before,
        IReadOnlyDictionary<string, object?> after)
    {
        var beforePayload = new Dictionary<string, object?>(before, StringComparer.Ordinal);
        var afterPayload = new Dictionary<string, object?>(after, StringComparer.Ordinal);

        // Nothing readable on EITHER side means there is nothing to say about
        // the text, and saying "the whole document was added" because the old
        // file was a PDF would be worse than saying nothing.
        var readable = !string.IsNullOrWhiteSpace(previousText) || !string.IsNullOrWhiteSpace(newText);

        if (!readable)
        {
            return new ContentAuditPayloadResult(beforePayload, afterPayload,
                ContentChangeRules.Compare(null, null), Readable: false);
        }

        var changes = ContentChangeRules.Compare(previousText, newText);

        // THE SUMMARY SENTENCE DOES NOT GO INTO THE PAYLOAD.
        //
        // It used to be written here as afterPayload["BodyChanges"] - present on
        // the after side and, because nothing before this change ever had a
        // "BodyChanges" field, absent on the before side. AuditChangeRules.Extract
        // treats exactly that shape as a changed field, so every content-carrying
        // save manufactured a phantom row: Field "Body Changes", Old value
        // "(not set)", New value the whole sentence ("3 paragraphs reworded, 1
        // added."). That is not a value that changed - it is a description of the
        // change - and it was pushing the real Paragraph_#### old/new pairs out of
        // the reader's attention, which is the "old and new value is not working
        // properly" a reviewer would report.
        //
        // The sentence is not lost: Detail (below) puts it on the audit list's own
        // summary line and in ActionDetail, which is where a summary belongs.
        //
        foreach (var change in changes.Changes)
        {
            beforePayload[change.Key] = change.OldText;
            afterPayload[change.Key] = change.NewText;
        }

        return new ContentAuditPayloadResult(beforePayload, afterPayload, changes, Readable: true);
    }
}

/// <summary>The payloads and the change set behind them.</summary>
/// <param name="Before">The before payload, ready to serialise.</param>
/// <param name="After">The after payload, ready to serialise.</param>
/// <param name="Changes">What differed, for the action detail line.</param>
/// <param name="Readable">
/// False when neither side's text could be read, so the caller can say
/// "fingerprints only" rather than implying nothing changed.
/// </param>
public sealed record ContentAuditPayloadResult(
    IReadOnlyDictionary<string, object?> Before,
    IReadOnlyDictionary<string, object?> After,
    ContentChangeRules.BodyChangeSet Changes,
    bool Readable)
{
    /// <summary>The sentence for the audit list, or one saying why there is none.</summary>
    public string Detail => Readable
        ? ContentChangeRules.Describe(Changes)
        : "The text of this file could not be read, so the change is recorded by its fingerprints.";
}
