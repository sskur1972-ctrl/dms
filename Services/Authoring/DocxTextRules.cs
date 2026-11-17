//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Text;
using System.Xml.Linq;

namespace DMS.Web.Services.Authoring;

/// <summary>
/// What a Word document SAYS, read out of its WordprocessingML.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A document can be changed by four routes - the
/// in-application editor, the Office editor, an uploaded replacement file, and a
/// revision carried forward - and only the first of them held the body as HTML
/// the DMS could read. The other three changed a <c>.docx</c> in the file store
/// and the audit trail recorded two SHA-256 values, which proves the file
/// changed and says nothing whatever about what changed IN it. This turns the
/// stored file back into the paragraphs a person would say the document consists
/// of, so <see cref="ContentChangeRules"/> can do for an uploaded or
/// Office-edited document exactly what it already does for an authored one.
/// </para>
/// <para>
/// <b>It is text extraction, not rendering.</b> <c>OpenXmlPreviewRenderer</c>
/// turns the same XML into HTML for people to look at, with images, alignment
/// and table markup. None of that belongs in a change report: a reader asking
/// what changed wants the words. So this is deliberately a second, much smaller
/// reading of the same part - and being pure, it is testable without a file
/// store, which the renderer is not.
/// </para>
/// <para>
/// <b>Pure and total.</b> It takes the XML as a string and never touches a file,
/// a zip or a database - <c>IDocumentTextExtractor</c> does that. Malformed XML
/// returns nothing rather than throwing: this runs on the save path of somebody
/// who pressed Save, and no defect in a change report may cost them their work.
/// </para>
/// </remarks>
public static class DocxTextRules
{
    /// <summary>The WordprocessingML main namespace.</summary>
    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Above this many paragraphs the rest are dropped.
    /// </summary>
    /// <remarks>
    /// The comparison downstream is O(n*m) over paragraphs and is bounded at its
    /// own, lower limit; this one only stops a pathological package from being
    /// materialised into memory first. A controlled SOP does not have four
    /// thousand paragraphs, and one that does is not going to be reviewed from a
    /// change table.
    /// </remarks>
    public const int MaxParagraphs = 4000;

    /// <summary>
    /// The paragraphs of the document, in reading order.
    /// </summary>
    /// <param name="documentXml">
    /// The contents of <c>word/document.xml</c>. Null, empty or unparseable
    /// gives an empty list.
    /// </param>
    /// <remarks>
    /// <para>
    /// A <c>w:p</c> is one paragraph and a <c>w:tr</c> is one row: without the
    /// row break every table in the document would run together into a single
    /// enormous paragraph, and a change to one cell would report as a change to
    /// the whole table. Cells within a row are joined with a tab, which is what
    /// they look like on the page and keeps a row on one line of the report.
    /// </para>
    /// <para>
    /// DELETED TEXT IS NOT INCLUDED. Word keeps tracked deletions in the package
    /// as <c>w:delText</c>; including it would put text the document no longer
    /// says into a report of what the document says. Insertions (<c>w:ins</c>)
    /// ARE included - they are part of the text as it now reads.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Paragraphs(string? documentXml)
    {
        if (string.IsNullOrWhiteSpace(documentXml)) return Array.Empty<string>();

        XElement? body;

        try
        {
            // DTD processing is off by default on XDocument.Parse, which is what
            // keeps an untrusted package from carrying an external entity.
            body = XDocument.Parse(documentXml, LoadOptions.PreserveWhitespace).Root?.Element(W + "body");
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<string>();
        }

        if (body is null) return Array.Empty<string>();

        var paragraphs = new List<string>();
        Walk(body, paragraphs);

        return paragraphs;
    }

    /// <summary>
    /// Body-level children in reading order: paragraphs as themselves, tables as
    /// one line per row.
    /// </summary>
    /// <remarks>
    /// An explicit walk rather than <c>Descendants()</c>. Descendants would visit
    /// the paragraphs inside a table cell as well as the row that contains them,
    /// so every cell's text would appear twice - once in its row and once on its
    /// own - and a change report would show each table change twice over.
    /// </remarks>
    private static void Walk(XElement container, List<string> paragraphs)
    {
        foreach (var node in container.Elements())
        {
            if (paragraphs.Count >= MaxParagraphs) return;

            if (node.Name == W + "p")
            {
                Add(paragraphs, TextOf(node));
            }
            else if (node.Name == W + "tbl")
            {
                foreach (var row in node.Elements(W + "tr"))
                {
                    if (paragraphs.Count >= MaxParagraphs) return;

                    // Cells joined as they read across the page, so one row is
                    // one line of the report rather than one line per cell.
                    var cells = row.Elements(W + "tc").Select(TextOf).Where(t => t.Length > 0);
                    Add(paragraphs, string.Join(" | ", cells));
                }
            }
            else if (node.Name == W + "sdt" || node.Name == W + "sdtContent")
            {
                // A content control wraps real content - a title, a revision box
                // - and skipping it would lose text the document plainly says.
                Walk(node, paragraphs);
            }
        }
    }

    /// <summary>
    /// The document as one block of text, one paragraph per line.
    /// </summary>
    /// <remarks>
    /// The shape <see cref="ContentChangeRules"/> and <see cref="TextDiffRules"/>
    /// already consume, so a document that arrived as a file and one that was
    /// authored in the application are compared by exactly the same code.
    /// </remarks>
    public static string PlainText(string? documentXml) => string.Join("\n", Paragraphs(documentXml));

    /// <summary>
    /// The same text wrapped as HTML paragraphs, for the existing comparison.
    /// </summary>
    /// <remarks>
    /// <see cref="ContentChangeRules.Compare"/> takes the stored HTML body,
    /// because that is what the in-application editor holds. Rather than give it
    /// a second entry point and two code paths to keep in step, an extracted
    /// document is presented in the same shape. The text is HTML-ENCODED on the
    /// way in: document text is exactly where a stray angle bracket lives, and
    /// one unencoded would silently swallow the rest of a paragraph when the
    /// comparison stripped tags.
    /// </remarks>
    public static string AsHtml(string? documentXml)
    {
        var paragraphs = Paragraphs(documentXml);
        if (paragraphs.Count == 0) return string.Empty;

        var html = new StringBuilder(paragraphs.Count * 48);

        foreach (var paragraph in paragraphs)
            html.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(paragraph)).Append("</p>");

        return html.ToString();
    }

    // -------------------------------------------------------------------------

    private static void Add(List<string> paragraphs, string text)
    {
        // Empty paragraphs are spacing, not content. Keeping them would make a
        // change report full of rows saying nothing became nothing.
        if (text.Length > 0) paragraphs.Add(text);
    }

    /// <summary>
    /// The visible text of an element: its runs, its tabs and its line breaks.
    /// </summary>
    private static string TextOf(XElement element)
    {
        var text = new StringBuilder();

        foreach (var node in element.Descendants())
        {
            // Office packages can carry the same text in alternate drawing representations.
            // Read the chosen representation once, excluding deleted/moved/hidden runs.
            if(node.Ancestors().Any(a=>a.Name.LocalName=="Fallback" && a.Parent?.Name.LocalName=="AlternateContent" && a.Parent.Elements().Any(c=>c.Name.LocalName=="Choice")))continue;
            if(node.Ancestors(W+"del").Any() || node.Ancestors(W+"moveFrom").Any())continue;
            if(node.Ancestors(W+"r").Any(r=>r.Element(W+"rPr")?.Elements().Any(p=>(p.Name==W+"vanish"||p.Name==W+"webHidden") && (string?)p.Attribute(W+"val") is not ("0" or "false"))==true))continue;
            if (node.Name == W + "t")
            {
                // A standalone list marker is formatting, not an edited field value.
                if(node.Value.Trim().Length>0 && node.Value.Trim().All(c=>"•◦▪‣\uf0a7\uf0b7".Contains(c)))continue;
                text.Append(node.Value);
            }
            else if (node.Name == W + "tab")
            {
                text.Append(' ');
            }
            else if (node.Name == W + "br" || node.Name == W + "cr")
            {
                text.Append(' ');
            }

            // w:delText is deliberately absent: it is what the document USED to
            // say, and this reports what it says now.
        }

        // Runs of whitespace collapse, as they do on the page. Without this a
        // paragraph re-flowed by a different editor would report as changed
        // because it gained two spaces, and noise in a change report is what
        // teaches people to skim it.
        return Collapse(text.ToString());
    }

    private static string Collapse(string text)
    {
        var result = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text)
        {
            // Non-breaking space included: it is a space to a reader, and a
            // document that gained one must not read as changed.
            var isSpace = char.IsWhiteSpace(c) || c == ' ';

            if (isSpace)
            {
                if (!lastWasSpace) result.Append(' ');
                lastWasSpace = true;
                continue;
            }

            result.Append(c);
            lastWasSpace = false;
        }

        return result.ToString().Trim();
    }
}
