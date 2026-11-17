//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace DMS.Web.Services.Authoring;

/// <summary>
/// URS-7.16. Renders an Office Open XML document to read-only HTML so the
/// protected viewer can show a reviewer the content of a .docx or .xlsx without
/// giving them the file.
///
/// <para><b>Why this exists at all</b></para>
/// The URS requires control of "view, edit, copy, screen shot &amp; printing".
/// Until this renderer, a revision with no PDF rendition could only be read by
/// downloading the authored file - which hands over an editable, copyable,
/// un-watermarked master. That is exactly the physical-document behaviour the
/// URS is replacing. A reviewer now reads the document; only an author or QA
/// takes the file.
///
/// <para><b>What it is not</b></para>
/// This is a REVIEW AID, not a controlled copy and not a rendition. It is
/// text-and-tables only: page geometry, headers and footers, images, numbering
/// and field codes are not reproduced. The controlled copy is always the
/// watermarked PDF, produced by the converter or attached by QA - never this.
/// <see cref="DocumentPreviewDto.IsTextOnly"/> is set so the viewer can say so
/// on the page rather than let a reader assume otherwise.
///
/// <para><b>Why it is written by hand against the zip</b></para>
/// The alternative is a package dependency (DocumentFormat.OpenXml) whose
/// licence, version and validation status would all have to be justified in the
/// IQ. Reading paragraphs and cells out of the part XML is a few dozen lines
/// against a published, stable schema, and it keeps the deployed dependency set
/// unchanged.
///
/// <para><b>Safety</b></para>
/// Every scrap of text taken from the document is HTML-encoded, and the output
/// is assembled from a fixed set of tags this class writes itself. No markup
/// from inside a document can reach the browser as markup, so a .docx carrying
/// a crafted string cannot inject script into the viewer. XML entity expansion
/// is off (<see cref="XmlResolver"/> null, DTD prohibited).
/// </summary>
public sealed class OpenXmlPreviewRenderer : IDocumentPreviewRenderer
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>
    /// PresentationML slide text lives in the DrawingML namespace, not in
    /// PresentationML's own: a:p paragraphs and a:t runs, wherever on the slide
    /// they sit.
    /// </summary>
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>Relationship parts, which map an r:embed id to the image file inside the package.</summary>
    private static readonly XNamespace REL = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly string[] WordExtensions = [".docx", ".docm"];
    private static readonly string[] ExcelExtensions = [".xlsx", ".xlsm"];
    private static readonly string[] PowerPointExtensions = [".pptx", ".pptm"];
    private static readonly string[] TextExtensions = [".txt", ".csv", ".md"];

    /// <summary>A document larger than this is not rendered inline; the reader is told why.</summary>
    private const long MaxBytes = 40L * 1024 * 1024;

    private readonly IFileStorageService _storage;
    private readonly ILogger<OpenXmlPreviewRenderer> _logger;

    public OpenXmlPreviewRenderer(IFileStorageService storage, ILogger<OpenXmlPreviewRenderer> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public bool CanRender(string mimeType, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return WordExtensions.Contains(extension)
            || ExcelExtensions.Contains(extension)
            || PowerPointExtensions.Contains(extension)
            || TextExtensions.Contains(extension);
    }

    public async Task<DocumentPreviewDto> RenderAsync(string relativePath, string fileName, string mimeType,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var result = new DocumentPreviewDto { SourceFormat = extension.TrimStart('.').ToUpperInvariant() };

        try
        {
            var absolute = _storage.ResolveAbsolutePath(relativePath);
            if (!File.Exists(absolute))
            {
                result.UnavailableReason = "The authored file is missing from the document store.";
                return result;
            }

            var info = new FileInfo(absolute);
            if (info.Length > MaxBytes)
            {
                result.UnavailableReason =
                    $"This file is {info.Length / (1024 * 1024)} MB, which is too large to display in the " +
                    "browser. Publish it so the PDF rendition can be viewed instead.";
                return result;
            }

            if (TextExtensions.Contains(extension))
            {
                var text = await File.ReadAllTextAsync(absolute, ct).ConfigureAwait(false);
                result.Kind = "HTML";
                result.IsTextOnly = true;
                result.Html = $"<pre class=\"dms-preview-plain\">{WebUtility.HtmlEncode(text)}</pre>";
                return result;
            }

            await using var stream = File.OpenRead(absolute);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            if (WordExtensions.Contains(extension))
            {
                result.Html = RenderWordprocessing(archive);
                result.HasImages = result.Html?.Contains("<img") == true;
                result.Kind = result.Html is null ? "NONE" : "HTML";
                result.IsTextOnly = true;
                result.UnavailableReason = result.Html is null
                    ? "The file is not a readable Word document."
                    : null;
                return result;
            }

            if (ExcelExtensions.Contains(extension))
            {
                result.Html = RenderSpreadsheet(archive);
                result.Kind = result.Html is null ? "NONE" : "HTML";
                result.IsTextOnly = true;
                result.UnavailableReason = result.Html is null
                    ? "The file is not a readable Excel workbook."
                    : null;
                return result;
            }

            if (PowerPointExtensions.Contains(extension))
            {
                result.Html = RenderPresentation(archive, out var slides);
                result.Kind = result.Html is null ? "NONE" : "HTML";
                result.IsTextOnly = true;
                result.PageCount = slides;
                result.UnavailableReason = result.Html is null
                    ? "The file is not a readable PowerPoint presentation."
                    : null;
                return result;
            }

            result.UnavailableReason = $"There is no read-only view for {extension} files.";
            return result;
        }
        catch (InvalidDataException)
        {
            result.UnavailableReason = "The file could not be opened; it may be corrupt or password-protected.";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview rendering failed for {Path}", relativePath);
            result.UnavailableReason = "The document could not be displayed.";
            return result;
        }
    }

    // -------------------------------------------------------------------------
    // WordprocessingML
    // -------------------------------------------------------------------------

    private static string? RenderWordprocessing(ZipArchive archive)
    {
        var entry = archive.GetEntry("word/document.xml");
        if (entry is null) return null;

        var document = LoadXml(entry);
        var body = document?.Root?.Element(W + "body");
        if (body is null) return null;

        // Images are inlined as data: URIs from inside the package. The alternative
        // - serving them from an endpoint - would mean handing out addressable
        // fragments of a controlled document, and there would then be a URL that
        // returns document content with no watermark and no audit entry. Inlining
        // keeps the whole rendering one response, governed by one access check.
        var images = LoadImageMap(archive, "word/_rels/document.xml.rels", "word");

        var html = new StringBuilder("<div class=\"dms-preview-doc\">");

        foreach (var node in body.Elements())
        {
            if (node.Name == W + "p") AppendParagraph(html, node, images);
            else if (node.Name == W + "tbl") AppendTable(html, node, images);
        }

        html.Append("</div>");
        return html.ToString();
    }

    private static void AppendParagraph(StringBuilder html, XElement paragraph,
        IReadOnlyDictionary<string, string> images)
    {
        var text = ParagraphHtml(paragraph, images);
        var properties = paragraph.Element(W + "pPr");
        var style = properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? string.Empty;

        // Paragraph alignment. A centred title or a right-aligned revision box in
        // an SOP carries meaning; flattening everything to the left makes the
        // rendering read as a different document.
        var alignment = properties?.Element(W + "jc")?.Attribute(W + "val")?.Value switch
        {
            "center" => " style=\"text-align:center\"",
            "right" => " style=\"text-align:right\"",
            "both" or "distribute" => " style=\"text-align:justify\"",
            _ => string.Empty
        };

        // Word's built-in heading styles are "Heading1".."Heading9" (and the
        // localised "berschrift1" variants, which are matched by the digit test
        // rather than by name).
        var level = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && style.Length > 7
                    && char.IsDigit(style[^1])
            ? style[^1] - '0'
            : 0;

        if (level is >= 1 and <= 6)
        {
            html.Append("<h").Append(level + 1).Append(alignment).Append('>')
                .Append(string.IsNullOrWhiteSpace(text) ? "&nbsp;" : text)
                .Append("</h").Append(level + 1).Append('>');
            return;
        }

        html.Append("<p").Append(alignment).Append('>')
            .Append(string.IsNullOrWhiteSpace(text) ? "&nbsp;" : text).Append("</p>");
    }

    /// <summary>
    /// Runs within one paragraph, with bold and italic preserved. Every text node
    /// is HTML-encoded here; the only tags emitted are the ones written literally
    /// in this method.
    /// </summary>
    private static string ParagraphHtml(XElement paragraph, IReadOnlyDictionary<string, string> images)
    {
        var html = new StringBuilder();

        foreach (var run in paragraph.Descendants(W + "r"))
        {
            // An inline image is a run with a drawing rather than text.
            foreach (var blip in run.Descendants(A + "blip"))
            {
                var id = blip.Attribute(R + "embed")?.Value;
                if (id is null || !images.TryGetValue(id, out var dataUri)) continue;

                html.Append("<img src=\"").Append(dataUri).Append("\" alt=\"Figure from the document\" />");
            }

            var properties = run.Element(W + "rPr");
            var bold = properties?.Element(W + "b") is not null;
            var italic = properties?.Element(W + "i") is not null;
            var underline = properties?.Element(W + "u") is not null;

            var text = new StringBuilder();
            foreach (var child in run.Elements())
            {
                if (child.Name == W + "t") text.Append(child.Value);
                else if (child.Name == W + "tab") text.Append('\t');
                else if (child.Name == W + "br") text.Append('\n');
            }

            if (text.Length == 0) continue;

            var encoded = WebUtility.HtmlEncode(text.ToString()).Replace("\n", "<br>").Replace("\t", "&emsp;");
            if (bold) html.Append("<strong>");
            if (italic) html.Append("<em>");
            if (underline) html.Append("<u>");
            html.Append(encoded);
            if (underline) html.Append("</u>");
            if (italic) html.Append("</em>");
            if (bold) html.Append("</strong>");
        }

        return html.ToString();
    }

    /// <summary>
    /// A Word table, preserving the two structural features that change meaning
    /// when dropped: merged cells and column spans.
    /// <para>
    /// A batch record's table where "Verified by" spans three columns reads as
    /// three separate empty fields once the merge is lost, which is worse than
    /// unformatted - it is wrong. gridSpan is the horizontal merge; vMerge is the
    /// vertical one, and a continuation cell is skipped rather than rendered empty.
    /// </para>
    /// </summary>
    private static void AppendTable(StringBuilder html, XElement table, IReadOnlyDictionary<string, string> images)
    {
        html.Append("<div class=\"dms-preview-tablewrap\"><table class=\"dms-preview-table\">");

        foreach (var row in table.Elements(W + "tr"))
        {
            html.Append("<tr>");

            foreach (var cell in row.Elements(W + "tc"))
            {
                var properties = cell.Element(W + "tcPr");

                // A vMerge without val="restart" is the continuation of a cell that
                // began in an earlier row. Rendering it would add a phantom column.
                var vMerge = properties?.Element(W + "vMerge");
                if (vMerge is not null &&
                    !string.Equals(vMerge.Attribute(W + "val")?.Value, "restart", StringComparison.OrdinalIgnoreCase))
                    continue;

                var span = properties?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value;
                var colspan = int.TryParse(span, out var columns) && columns > 1
                    ? $" colspan=\"{columns}\""
                    : string.Empty;

                var content = new StringBuilder();
                foreach (var paragraph in cell.Elements(W + "p"))
                {
                    var text = ParagraphHtml(paragraph, images);
                    if (text.Length > 0) content.Append("<p>").Append(text).Append("</p>");
                }

                html.Append("<td").Append(colspan).Append('>')
                    .Append(content.Length == 0 ? "&nbsp;" : content.ToString())
                    .Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</table></div>");
    }

    // -------------------------------------------------------------------------
    // Images
    // -------------------------------------------------------------------------

    /// <summary>
    /// Maps relationship ids to <c>data:</c> URIs for every image in the package.
    /// <para>
    /// Bounded deliberately: a document with fifty photographs would otherwise
    /// produce a response tens of megabytes wide, and base64 inflates by a third.
    /// Past the cap the remaining images are omitted rather than the whole
    /// rendering failing - the reader still gets the text.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadImageMap(ZipArchive archive, string relsPath,
        string partPrefix)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var relsEntry = archive.GetEntry(relsPath);
        if (relsEntry is null) return map;

        var rels = LoadXml(relsEntry);
        if (rels?.Root is null) return map;

        const long budget = 12L * 1024 * 1024;
        long used = 0;

        foreach (var relationship in rels.Root.Elements(REL + "Relationship"))
        {
            var type = relationship.Attribute("Type")?.Value ?? string.Empty;
            if (!type.EndsWith("/image", StringComparison.OrdinalIgnoreCase)) continue;

            var id = relationship.Attribute("Id")?.Value;
            var target = relationship.Attribute("Target")?.Value;
            if (id is null || string.IsNullOrWhiteSpace(target)) continue;

            // External images are a URL, not a part in the package. They are skipped:
            // fetching one would make the server retrieve an arbitrary address on a
            // reader's behalf, and the address is controlled by whoever authored the
            // document.
            if (relationship.Attribute("TargetMode")?.Value is "External") continue;

            var entry = ResolvePart(archive, partPrefix, target);
            if (entry is null || entry.Length <= 0) continue;
            if (used + entry.Length > budget) continue;

            var mime = ImageMime(entry.Name);
            if (mime is null) continue;

            try
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);

                map[id] = $"data:{mime};base64,{Convert.ToBase64String(buffer.ToArray())}";
                used += entry.Length;
            }
            catch (Exception)
            {
                // One unreadable image must not cost the reader the document.
            }
        }

        return map;
    }

    /// <summary>
    /// Resolves a relationship target, which is relative to the part's own folder
    /// and may climb out of it ("../media/image1.png").
    /// </summary>
    private static ZipArchiveEntry? ResolvePart(ZipArchive archive, string partPrefix, string target)
    {
        var combined = target.StartsWith('/')
            ? target.TrimStart('/')
            : $"{partPrefix}/{target}";

        var segments = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        return archive.GetEntry(string.Join('/', segments));
    }

    /// <summary>
    /// Only raster formats a browser renders natively. EMF and WMF - which Word
    /// embeds for pasted charts - are deliberately absent: a browser cannot display
    /// them, and emitting an img that silently fails is worse than omitting it.
    /// </summary>
    private static string? ImageMime(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        _ => null
    };

    // -------------------------------------------------------------------------
    // PresentationML
    // -------------------------------------------------------------------------

    /// <summary>
    /// Renders each slide's text as a titled block.
    /// <para>
    /// Slides are ordered by the numeric part of their part name. Sorting the
    /// strings instead would put slide10 before slide2, which for a numbered SOP
    /// annexe is not a cosmetic problem - a reviewer reading steps out of order is
    /// reviewing a different procedure.
    /// </para>
    /// </summary>
    private static string? RenderPresentation(ZipArchive archive, out int slideCount)
    {
        slideCount = 0;

        var slides = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Entry: e, Number: SlideNumber(e.Name)))
            .OrderBy(x => x.Number)
            .ToList();

        if (slides.Count == 0) return null;

        var html = new StringBuilder("<div class=\"dms-preview-doc\">");

        foreach (var (entry, number) in slides)
        {
            var document = LoadXml(entry);
            if (document?.Root is null) continue;

            slideCount++;
            html.Append("<section class=\"dms-preview-slide\"><h3>Slide ").Append(number).Append("</h3>");

            // Every a:p on the slide, wherever it sits.
            //
            // Walking p:sp shapes only would have missed two things that matter in a
            // GMP deck: text inside a table, which lives under p:graphicFrame rather
            // than p:sp, and text in a grouped shape. A reviewer silently shown a
            // slide with its table missing is worse off than one shown no slide.
            foreach (var paragraph in document.Root.Descendants(A + "p"))
            {
                var text = string.Concat(paragraph.Descendants(A + "t").Select(t => t.Value));
                if (string.IsNullOrWhiteSpace(text)) continue;

                html.Append("<p>").Append(WebUtility.HtmlEncode(text)).Append("</p>");
            }

            // Speaker notes are part of the controlled content when a presentation is
            // a training deck, so they are shown rather than silently dropped.
            var notes = archive.GetEntry($"ppt/notesSlides/notesSlide{number}.xml");
            if (notes is not null)
            {
                var notesDocument = LoadXml(notes);
                var noteText = notesDocument?.Root is null
                    ? string.Empty
                    : string.Join(" ", notesDocument.Root.Descendants(A + "t").Select(t => t.Value));

                // The notes part repeats the slide-number placeholder; a numeric-only
                // note is that artefact, not content.
                if (!string.IsNullOrWhiteSpace(noteText) && !noteText.Trim().All(char.IsDigit))
                    html.Append("<p class=\"dms-preview-notes\"><strong>Notes:</strong> ")
                        .Append(WebUtility.HtmlEncode(noteText.Trim())).Append("</p>");
            }

            html.Append("</section>");
        }

        html.Append("</div>");
        return slideCount == 0 ? null : html.ToString();
    }

    private static int SlideNumber(string partName)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(partName).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }

    // -------------------------------------------------------------------------
    // SpreadsheetML
    // -------------------------------------------------------------------------

    /// <summary>
    /// A workbook, one table per sheet.
    /// <para>
    /// Sheet NAMES come from workbook.xml rather than the part file name, because
    /// "Sheet1" tells a reviewer nothing and "Batch Reconciliation" tells them
    /// which table they are looking at. Merged ranges are honoured for the same
    /// reason they are in Word: a merged header rendered as separate cells changes
    /// what the table says.
    /// </para>
    /// </summary>
    private static string? RenderSpreadsheet(ZipArchive archive)
    {
        var sharedStrings = LoadSharedStrings(archive);
        var sheetNames = LoadSheetNames(archive);

        var sheets = archive.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Entry: e, Number: SheetNumber(e.Name)))
            .OrderBy(x => x.Number)
            .ToList();

        if (sheets.Count == 0) return null;

        var html = new StringBuilder("<div class=\"dms-preview-doc\">");

        foreach (var (entry, number) in sheets)
        {
            var document = LoadXml(entry);
            var data = document?.Root?.Element(S + "sheetData");
            if (data is null) continue;

            var name = sheetNames.TryGetValue(number, out var friendly) && !string.IsNullOrWhiteSpace(friendly)
                ? friendly
                : Path.GetFileNameWithoutExtension(entry.Name);

            html.Append("<h3>").Append(WebUtility.HtmlEncode(name)).Append("</h3>");

            var merges = LoadMergedCells(document!);

            html.Append("<div class=\"dms-preview-tablewrap\"><table class=\"dms-preview-table\">");

            foreach (var row in data.Elements(S + "row"))
            {
                html.Append("<tr>");

                foreach (var cell in row.Elements(S + "c"))
                {
                    var reference = cell.Attribute("r")?.Value;

                    // A cell swallowed by a merge is skipped; the anchor carries the
                    // span. Rendering both would shift every later column right.
                    if (reference is not null && merges.Swallowed.Contains(reference)) continue;

                    var span = string.Empty;
                    if (reference is not null && merges.Anchors.TryGetValue(reference, out var extent))
                    {
                        if (extent.Columns > 1) span += $" colspan=\"{extent.Columns}\"";
                        if (extent.Rows > 1) span += $" rowspan=\"{extent.Rows}\"";
                    }

                    html.Append("<td").Append(span).Append('>')
                        .Append(WebUtility.HtmlEncode(CellText(cell, sharedStrings)))
                        .Append("</td>");
                }

                html.Append("</tr>");
            }

            html.Append("</table></div>");
        }

        html.Append("</div>");
        return html.ToString();
    }

    /// <summary>Sheet index to display name, from workbook.xml.</summary>
    private static IReadOnlyDictionary<int, string> LoadSheetNames(ZipArchive archive)
    {
        var names = new Dictionary<int, string>();

        var entry = archive.GetEntry("xl/workbook.xml");
        if (entry is null) return names;

        var document = LoadXml(entry);
        var sheets = document?.Root?.Element(S + "sheets");
        if (sheets is null) return names;

        var index = 1;
        foreach (var sheet in sheets.Elements(S + "sheet"))
        {
            var name = sheet.Attribute("name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) names[index] = name;
            index++;
        }

        return names;
    }

    /// <summary>
    /// Merged ranges, split into the anchor cell (which gets the span) and the
    /// cells it swallows (which are skipped).
    /// </summary>
    private static (Dictionary<string, (int Columns, int Rows)> Anchors, HashSet<string> Swallowed)
        LoadMergedCells(XDocument sheet)
    {
        var anchors = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var swallowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var merge in sheet.Root?.Element(S + "mergeCells")?.Elements(S + "mergeCell")
                              ?? Enumerable.Empty<XElement>())
        {
            var reference = merge.Attribute("ref")?.Value;
            if (string.IsNullOrWhiteSpace(reference)) continue;

            var parts = reference.Split(':');
            if (parts.Length != 2) continue;

            var (fromColumn, fromRow) = SplitReference(parts[0]);
            var (toColumn, toRow) = SplitReference(parts[1]);
            if (fromColumn == 0 || fromRow == 0 || toColumn == 0 || toRow == 0) continue;

            anchors[parts[0]] = (toColumn - fromColumn + 1, toRow - fromRow + 1);

            for (var column = fromColumn; column <= toColumn; column++)
            for (var row = fromRow; row <= toRow; row++)
            {
                var cell = $"{ColumnName(column)}{row}";
                if (!string.Equals(cell, parts[0], StringComparison.OrdinalIgnoreCase)) swallowed.Add(cell);
            }
        }

        return (anchors, swallowed);
    }

    /// <summary>"BC12" becomes (column 55, row 12).</summary>
    private static (int Column, int Row) SplitReference(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
        var digits = new string(reference.SkipWhile(char.IsLetter).ToArray());

        if (letters.Length == 0 || !int.TryParse(digits, out var row)) return (0, 0);

        var column = letters.ToUpperInvariant()
            .Aggregate(0, (running, letter) => running * 26 + (letter - 'A' + 1));

        return (column, row);
    }

    /// <summary>The inverse: column 55 becomes "BC".</summary>
    private static string ColumnName(int column)
    {
        var name = new StringBuilder();

        while (column > 0)
        {
            var remainder = (column - 1) % 26;
            name.Insert(0, (char)('A' + remainder));
            column = (column - 1) / 26;
        }

        return name.ToString();
    }

    private static int SheetNumber(string partName) => SlideNumber(partName);

    private static string CellText(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;

        // t="s" means the value is an index into the shared-string table; t="inlineStr"
        // carries the text in the cell itself.
        if (type == "s")
        {
            var raw = cell.Element(S + "v")?.Value;
            return int.TryParse(raw, out var index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : string.Empty;
        }

        if (type == "inlineStr")
            return string.Concat(cell.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? []);

        return cell.Element(S + "v")?.Value ?? string.Empty;
    }

    private static IReadOnlyList<string> LoadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return Array.Empty<string>();

        var document = LoadXml(entry);
        if (document?.Root is null) return Array.Empty<string>();

        return document.Root.Elements(S + "si")
            .Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value)))
            .ToList();
    }

    /// <summary>
    /// Loads a part with DTD processing disabled and no resolver, so a document
    /// cannot pull in an external entity or expand one recursively.
    /// </summary>
    private static XDocument? LoadXml(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = false
            });

            return XDocument.Load(reader);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Registered when preview rendering is switched off.</summary>
public sealed class NullDocumentPreviewRenderer : IDocumentPreviewRenderer
{
    public bool CanRender(string mimeType, string fileName) => false;

    public Task<DocumentPreviewDto> RenderAsync(string relativePath, string fileName, string mimeType,
        CancellationToken ct = default)
        => Task.FromResult(new DocumentPreviewDto
        {
            Kind = "NONE",
            UnavailableReason = "The read-only document viewer is switched off on this server."
        });
}
