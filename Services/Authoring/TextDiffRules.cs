//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Text;
using System.Text.RegularExpressions;

namespace DMS.Web.Services.Authoring;

/// <summary>What happened to one line between two revisions.</summary>
public enum DiffKind
{
    /// <summary>Present, and the same, in both.</summary>
    Unchanged,

    /// <summary>Only in the later revision.</summary>
    Added,

    /// <summary>Only in the earlier revision.</summary>
    Removed
}

/// <summary>
/// One line of a side-by-side comparison. Both sides are carried on the same row
/// so a view can render two aligned columns without doing any of the thinking.
/// </summary>
public sealed class DiffRow
{
    public DiffKind Kind { get; init; }

    /// <summary>The earlier revision's text, or null where the line was added.</summary>
    public string? Left { get; init; }

    /// <summary>The later revision's text, or null where the line was removed.</summary>
    public string? Right { get; init; }

    /// <summary>1-based line number in the earlier revision, where it has one.</summary>
    public int? LeftLineNo { get; init; }

    /// <summary>1-based line number in the later revision, where it has one.</summary>
    public int? RightLineNo { get; init; }
}

/// <summary>
/// Comparing what two revisions of a document actually SAY.
///
/// <para><b>Why this is written here rather than taken from a package</b></para>
/// A diff library would be a third-party component in a validated system: it
/// would need qualifying, and its output is shown to a reviewer deciding whether
/// a controlled document changed. This is about eighty lines of standard
/// longest-common-subsequence, it has no dependencies, and it is directly
/// testable - which is worth more here than the extra polish a library would
/// bring to edge cases that GMP prose does not contain.
///
/// <para><b>What it is compared FROM</b></para>
/// Text, extracted from the stored HTML body. Not the PDF: the PDF is a picture
/// of the document and two renders of identical text can differ byte for byte,
/// so diffing renditions would report changes that are not changes. The
/// file-level fingerprints answer "did the file change at all"; this answers
/// "and what changed in it".
///
/// <para><b>What it is not</b></para>
/// Not a merge tool and not a tracked-changes editor. It reports; nothing here
/// writes to a document, and a reviewer reading it is looking at two revisions
/// that are both already stored exactly as they were.
/// </summary>
public static class TextDiffRules
{
    /// <summary>
    /// Above this many lines a side the comparison is refused rather than run.
    ///
    /// <para>
    /// The LCS matrix is O(n*m): two thousand lines a side is four million cells
    /// and is fine; fifty thousand would be two and a half billion, and a
    /// reviewer clicking Compare would take the web server down. A refusal that
    /// says why is a better outcome than a page that never loads.
    /// </para>
    /// </summary>
    public const int MaximumLines = 4000;

    private static readonly Regex BlockBreak = new(
        @"</(p|div|h[1-6]|li|tr|table|blockquote)\s*>|<br\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private static readonly Regex Tag = new("<[^>]+>", RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    private static readonly Regex Spaces = new(@"[ \t ]+", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Turns a stored HTML body into the lines a person would say the document
    /// consists of.
    ///
    /// <para>
    /// Block-level tags become line breaks first, THEN the remaining tags are
    /// removed. Stripping tags first would run every paragraph of the document
    /// together into one enormous line, and a diff of one line against one line
    /// says only "it changed" - which is exactly the answer the reviewer already
    /// had from the file hash.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ToLines(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Array.Empty<string>();

        string text;

        try
        {
            text = BlockBreak.Replace(html, "\n");
            text = Tag.Replace(text, string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            return Array.Empty<string>();
        }

        text = System.Net.WebUtility.HtmlDecode(text);

        return text.Split('\n')
            .Select(line => Spaces.Replace(line, " ").Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    /// <summary>
    /// The side-by-side comparison of two documents' lines.
    ///
    /// <para>
    /// Standard LCS: the longest run of lines common to both is found, and
    /// everything outside it is what changed. Lines are compared after trimming
    /// and collapsing runs of whitespace, so a reviewer is not shown a change
    /// because a paragraph gained two spaces when it was re-saved by a different
    /// editor - that is noise, and noise in a change report is what teaches
    /// people to skim it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DiffRow> Compare(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count > MaximumLines || right.Count > MaximumLines)
            throw new ArgumentOutOfRangeException(nameof(left),
                $"This comparison is too large to run in the browser ({left.Count} and {right.Count} lines; " +
                $"the limit is {MaximumLines} a side).");

        // lcs[i, j] = length of the longest common subsequence of left[i..] and
        // right[j..]. Built from the end so the walk below reads forwards.
        var lcs = new int[left.Count + 1, right.Count + 1];

        for (var i = left.Count - 1; i >= 0; i--)
        for (var j = right.Count - 1; j >= 0; j--)
            lcs[i, j] = SameLine(left[i], right[j])
                ? lcs[i + 1, j + 1] + 1
                : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var rows = new List<DiffRow>(Math.Max(left.Count, right.Count));
        int x = 0, y = 0;

        while (x < left.Count && y < right.Count)
        {
            if (SameLine(left[x], right[y]))
            {
                rows.Add(new DiffRow
                {
                    Kind = DiffKind.Unchanged,
                    Left = left[x], Right = right[y],
                    LeftLineNo = x + 1, RightLineNo = y + 1
                });

                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                rows.Add(new DiffRow { Kind = DiffKind.Removed, Left = left[x], LeftLineNo = x + 1 });
                x++;
            }
            else
            {
                rows.Add(new DiffRow { Kind = DiffKind.Added, Right = right[y], RightLineNo = y + 1 });
                y++;
            }
        }

        while (x < left.Count)
        {
            rows.Add(new DiffRow { Kind = DiffKind.Removed, Left = left[x], LeftLineNo = x + 1 });
            x++;
        }

        while (y < right.Count)
        {
            rows.Add(new DiffRow { Kind = DiffKind.Added, Right = right[y], RightLineNo = y + 1 });
            y++;
        }

        return rows;
    }

    /// <summary>
    /// Whether two lines say the same thing. Case-SENSITIVE on purpose: in a GMP
    /// procedure "shall" and "Shall" may not matter but "mg" and "MG" do, and a
    /// comparison that hides a case change in a unit is worse than one that shows
    /// a change nobody cares about.
    /// </summary>
    private static bool SameLine(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    /// Marks the words that differ within a changed line, so the view can
    /// highlight them inside the line rather than only colouring the whole row.
    ///
    /// <para>
    /// Returns HTML-ESCAPED text with <c>&lt;mark&gt;</c> around the differing
    /// runs. Escaping happens here, in the same place the markup is introduced,
    /// because a view that received a mix of trusted and untrusted text and had
    /// to remember which was which would eventually get it wrong - and the input
    /// is document body text, which is exactly where an injected tag would be
    /// most useful to somebody.
    /// </para>
    /// </summary>
    public static string HighlightWords(string? line, string? against)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        var mine = line.Split(' ');
        var theirs = new HashSet<string>((against ?? string.Empty).Split(' '), StringComparer.Ordinal);

        var result = new StringBuilder(line.Length + 32);

        for (var i = 0; i < mine.Length; i++)
        {
            if (i > 0) result.Append(' ');

            var word = System.Net.WebUtility.HtmlEncode(mine[i]);

            if (mine[i].Length > 0 && !theirs.Contains(mine[i]))
                result.Append("<mark>").Append(word).Append("</mark>");
            else
                result.Append(word);
        }

        return result.ToString();
    }

    /// <summary>How many lines changed, for the summary line above the table.</summary>
    public static (int Added, int Removed, int Unchanged) Summarise(IEnumerable<DiffRow> rows)
    {
        int added = 0, removed = 0, unchanged = 0;

        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case DiffKind.Added: added++; break;
                case DiffKind.Removed: removed++; break;
                default: unchanged++; break;
            }
        }

        return (added, removed, unchanged);
    }
}
