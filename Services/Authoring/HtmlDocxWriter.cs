//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DMS.Web.Services.Authoring;

/// <summary>
/// Turns a sanitised document body into a real Open XML WordprocessingML
/// package (URS-7.1).
///
/// <para><b>Why hand-rolled rather than a library</b></para>
/// The controlled artefact of an in-application document has to be a .docx:
/// everything downstream - the file store, the SHA-256 on the version row, the
/// converter, the watermarked PDF master, the controlled copy, the source an
/// inspector may ask for - already works on .docx and must keep working
/// unchanged. The package is written here with <see cref="ZipArchive"/> and XML
/// this assembly emits itself, which adds no third-party component for the
/// validation package to qualify and requires neither Microsoft Office nor
/// LibreOffice on the server.
///
/// <para><b>The rule this class exists to keep</b></para>
/// <b>What the author sees in the editor is what the exported file contains.</b>
/// Every mapping below has a counterpart in <c>dms-editor.css</c>, and where the
/// two could disagree the disagreement is a defect, not a tolerance. The
/// customer found one such defect - Word's clipboard bullet exporting as seven
/// empty boxes - and it is the reason the sanitiser now strips symbol fonts and
/// the reason this class refuses to write them.
///
/// <para><b>What it maps</b></para>
/// Page geometry, orientation, margins, gutter and columns; running headers and
/// footers with live page-number fields; named styles including Title, Subtitle,
/// Heading 1-6, Quote, Caption and No Spacing; paragraph alignment, indentation,
/// line spacing, space before and after, borders and shading, keep-with-next and
/// page-break-before; bold, italic, underline, strikethrough, superscript,
/// subscript, small capitals, font family, point size, colour and highlight;
/// bulleted, numbered and legal multilevel lists; real hyperlinks; tables with
/// column widths, column and row spans, per-cell shading, borders and vertical
/// alignment, and repeating header rows; inline images at their true size; page
/// and section breaks; and a table-of-contents field.
///
/// <para><b>What it does not attempt, and why</b></para>
/// Footnotes, cross-reference fields, tracked changes and text boxes. Each is a
/// feature whose value depends on being complete - a half-implemented tracked
/// change is a compliance finding, not a missing nicety - and each is recorded
/// in <c>docs/LIMITATIONS.md §1b</c> rather than left for someone to discover.
/// </summary>
public sealed class HtmlDocxWriter : IHtmlDocxWriter
{
    private static readonly XNamespace W = DocxPackageParts.W;
    private static readonly XNamespace R = DocxPackageParts.R;
    private static readonly XNamespace A = DocxPackageParts.A;
    private static readonly XNamespace Wp = DocxPackageParts.Wp;
    private static readonly XNamespace Pic = DocxPackageParts.Pic;

    /// <summary>English Metric Units per pixel at 96 DPI - the OOXML convention.</summary>
    private const int EmuPerPixel = 9525;

    /// <summary>EMU per twip: 914400 per inch divided by 1440 twips per inch.</summary>
    private const int EmuPerTwip = 635;

    public Stream Write(string sanitizedHtml, DocxWriteMetadata metadata)
    {
        var page = (metadata.PageSetup ?? DocumentPageSetupDto.Default).Normalised();
        var styleSet = DocxPackageParts.StyleSet.For(page.StyleSet);
        var context = new WriteContext(page);

        var body = BuildBody(sanitizedHtml, context);

        // Headers and footers are built after the body so that any images they
        // contain join the same relationship table.
        var running = BuildRunningParts(page, context);

        body.Add(DocxPackageParts.SectionProperties(page,
            running.Select(r => r.Part).ToList()));

        var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var extensions = context.Images.Select(i => i.Extension).ToList();
            var parts = running.Select(r => r.Part).ToList();

            WriteEntry(zip, "[Content_Types].xml", DocxPackageParts.ContentTypes(extensions, parts));

            WriteEntry(zip, "_rels/.rels", DocxPackageParts.PackageRels());
            WriteEntry(zip, "word/document.xml", DocxPackageParts.Serialize(Document(body)));

            WriteEntry(zip, "word/_rels/document.xml.rels", DocxPackageParts.DocumentRels(
                context.Images.Select(i => (i.RelationshipId, i.FileName)).ToList(),
                context.Hyperlinks.Select(h => (h.Id, h.Url)).ToList(),
                parts));

            WriteEntry(zip, "word/styles.xml", DocxPackageParts.Styles(styleSet));
            WriteEntry(zip, "word/numbering.xml", DocxPackageParts.Numbering());

            // Fields are only worth writing if something recalculates them.
            WriteEntry(zip, "word/settings.xml", DocxPackageParts.Settings(
                context.HasFields || page.ShowPageNumbers,
                page.DifferentOddEven && (page.HasHeader || page.HasFooter)));

            // Relationship ids are scoped to the part that uses them, so a
            // picture in a running header needs its id declared in the header's
            // OWN rels part. See DocxPackageParts.PartRels.
            var partRels = DocxPackageParts.PartRels(
                context.Images.Select(i => (i.RelationshipId, i.FileName)).ToList(),
                context.Hyperlinks.Select(h => (h.Id, h.Url)).ToList());

            foreach (var (part, blocks) in running)
            {
                WriteEntry(zip, "word/" + part.FileName,
                    DocxPackageParts.HeaderFooterPart(part.Kind, blocks));

                WriteEntry(zip, "word/_rels/" + part.FileName + ".rels", partRels);
            }

            WriteEntry(zip, "docProps/core.xml", DocxPackageParts.CoreProperties(metadata));
            WriteEntry(zip, "docProps/app.xml", DocxPackageParts.AppProperties());

            foreach (var image in context.Images)
            {
                var entry = zip.CreateEntry($"word/media/{image.FileName}", CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(image.Bytes, 0, image.Bytes.Length);
            }
        }

        stream.Position = 0;
        return stream;
    }

    // -------------------------------------------------------------------------
    // Body
    // -------------------------------------------------------------------------

    /// <summary>
    /// The input is the sanitiser's output, which is balanced and entity-clean by
    /// construction, so it parses as XML. It is still read with DTD processing
    /// prohibited and no resolver: "it came from our own sanitiser" is an
    /// assumption, and an XML parser pointed at an external entity is not a
    /// mistake worth risking on an assumption.
    /// </summary>
    private static XElement Document(IEnumerable<XElement> blocks)
    {
        var body = new XElement(W + "body");
        foreach (var block in blocks) body.Add(block);

        return new XElement(W + "document",
            new XAttribute(XNamespace.Xmlns + "w", DocxPackageParts.WordMain),
            new XAttribute(XNamespace.Xmlns + "r", DocxPackageParts.Relationships),
            new XAttribute(XNamespace.Xmlns + "a", DocxPackageParts.DrawingMain),
            new XAttribute(XNamespace.Xmlns + "wp", DocxPackageParts.DrawingWp),
            new XAttribute(XNamespace.Xmlns + "pic", DocxPackageParts.DrawingPic),
            body);
    }

    private static List<XElement> BuildBody(string sanitizedHtml, WriteContext context)
    {
        var blocks = ParseAndMap(sanitizedHtml, context);

        if (blocks.Count == 0) blocks.Add(EmptyParagraph());

        // Word requires a paragraph after a table at body level; without one the
        // document opens with a repair prompt.
        if (blocks[^1].Name == W + "tbl") blocks.Add(EmptyParagraph());

        return blocks;
    }

    private static List<XElement> ParseAndMap(string? sanitizedHtml, WriteContext context)
    {
        var blocks = new List<XElement>();

        XElement root;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                CloseInput = true
            };

            using var reader = XmlReader.Create(
                new StringReader("<dmsbody>" + (sanitizedHtml ?? string.Empty) + "</dmsbody>"), settings);

            root = XElement.Load(reader);
        }
        catch (XmlException)
        {
            // A body that will not parse is written out as plain text rather than
            // failing the save. Losing the author's formatting is bad; losing
            // their afternoon's work because of one malformed fragment is worse.
            foreach (var line in StripTags(sanitizedHtml).Split('\n'))
                blocks.Add(Paragraph(new ParagraphFormat(), new[] { TextRun(line.Trim(), new RunFormat()) }));

            return blocks;
        }

        foreach (var node in root.Nodes())
            AppendBlock(node, blocks, new RunFormat(), new ParagraphFormat(), context, depth: 0);

        return blocks;
    }

    /// <summary>
    /// Every header and footer part the page setup calls for, in the order Word
    /// writes them.
    ///
    /// <para>
    /// Word models a running header as up to three parts per section - default,
    /// first page, even pages - and the page setup can ask for any combination.
    /// Building the list here, rather than testing "different first page" at
    /// four separate points in the package assembly, is what keeps the
    /// combinations from multiplying: <c>[Content_Types]</c>, the document
    /// relationships, the section properties and the parts themselves all read
    /// from this one list.
    /// </para>
    ///
    /// <para>
    /// A variant that the author has left empty is still written. An empty
    /// first-page header is the whole point of "different first page" - it is
    /// how a title page carries no running header - and Word needs a part to
    /// point the reference at.
    /// </para>
    /// </summary>
    private static List<(DocxPackageParts.RunningPart Part, List<XElement> Blocks)> BuildRunningParts(
        DocumentPageSetupDto page, WriteContext context)
    {
        var parts = new List<(DocxPackageParts.RunningPart, List<XElement>)>();
        var headers = 0;
        var footers = 0;

        void Add(bool isHeader, string variant, string? html, bool withPageNumber)
        {
            // Numbered per kind, so the parts are header1/2/3 and footer1/2/3.
            // A package with a footer2.xml and no footer1.xml is legal and
            // deeply confusing to anybody who ever unzips one.
            var index = isHeader ? ++headers : ++footers;
            var stem = isHeader ? "header" : "footer";

            parts.Add((new DocxPackageParts.RunningPart(
                    isHeader ? "hdr" : "ftr", variant,
                    stem + index + ".xml",
                    "rId" + char.ToUpperInvariant(stem[0]) + stem[1..] + index),
                BuildHeaderOrFooter(html, page, context, isHeader, withPageNumber)));
        }

        if (page.HasHeader)
        {
            Add(true, "default", page.HeaderHtml, page.PageNumberIsInHeader);

            if (page.DifferentFirstPage)
            {
                // The page number is suppressed on a title page along with the
                // header, which is what "different first page" is for.
                Add(true, "first", page.FirstHeaderHtml, false);
            }

            if (page.DifferentOddEven)
                Add(true, "even", page.EvenHeaderHtml, page.PageNumberIsInHeader);
        }

        if (page.HasFooter)
        {
            Add(false, "default", page.FooterHtml, page.PageNumberIsInFooter);

            if (page.DifferentFirstPage)
                Add(false, "first", page.FirstFooterHtml, false);

            if (page.DifferentOddEven)
                Add(false, "even", page.EvenFooterHtml, page.PageNumberIsInFooter);
        }

        return parts;
    }

    /// <summary>
    /// Builds a header or footer part: the author's own content, then the page
    /// number field when the page setup puts it here.
    /// </summary>
    private static List<XElement> BuildHeaderOrFooter(string? html, DocumentPageSetupDto page,
        WriteContext context, bool isHeader, bool withPageNumber)
    {
        var styleId = isHeader ? "Header" : "Footer";
        var blocks = ParseAndMap(html, context);

        foreach (var block in blocks)
        {
            if (block.Name != W + "p") continue;

            var properties = block.Element(W + "pPr");
            if (properties is null)
            {
                properties = new XElement(W + "pPr");
                block.AddFirst(properties);
            }

            if (properties.Element(W + "pStyle") is null)
                properties.AddFirst(new XElement(W + "pStyle", new XAttribute(W + "val", styleId)));
        }

        if (withPageNumber) blocks.Add(PageNumberParagraph(page, styleId, context));

        if (blocks.Count == 0) blocks.Add(StyledEmptyParagraph(styleId));
        return blocks;
    }

    /// <summary>
    /// "Page 3 of 11", written as live fields.
    ///
    /// <para>
    /// PAGE and NUMPAGES are instructions, not text: the file carries the
    /// instruction plus a cached value, and Word computes the real number when it
    /// paginates. Writing a literal number instead would produce a document whose
    /// footer said "Page 1 of 1" on every page - which is exactly the kind of
    /// quietly wrong controlled copy that makes a missing page undetectable.
    /// </para>
    /// </summary>
    private static XElement PageNumberParagraph(DocumentPageSetupDto page, string styleId, WriteContext context)
    {
        context.HasFields = true;

        var alignment = page.PageNumberPosition.EndsWith("LEFT", StringComparison.OrdinalIgnoreCase) ? "left"
            : page.PageNumberPosition.EndsWith("RIGHT", StringComparison.OrdinalIgnoreCase) ? "right"
            : "center";

        var paragraph = new XElement(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "pStyle", new XAttribute(W + "val", styleId)),
                new XElement(W + "jc", new XAttribute(W + "val", alignment))));

        var format = page.PageNumberFormat.ToUpperInvariant();

        if (format != "PLAIN_X") paragraph.Add(TextRun("Page ", new RunFormat()));
        paragraph.Add(Field(" PAGE ", "1"));

        if (format == "PAGE_X_OF_Y")
        {
            paragraph.Add(TextRun(" of ", new RunFormat()));
            paragraph.Add(Field(" NUMPAGES ", "1"));
        }

        return paragraph;
    }

    private static XElement Field(string instruction, string cachedResult) => new(W + "fldSimple",
        new XAttribute(W + "instr", instruction),
        new XElement(W + "r", new XElement(W + "t", cachedResult)));

    // -------------------------------------------------------------------------
    // Block mapping
    // -------------------------------------------------------------------------

    /// <summary>Elements that cannot live inside a WordprocessingML paragraph.</summary>
    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "p", "div", "ul", "ol", "table", "h1", "h2", "h3", "h4", "h5", "h6",
        "blockquote", "pre", "hr", "figure"
    };

    private static void AppendBlock(XNode node, List<XElement> blocks, RunFormat run, ParagraphFormat para,
        WriteContext context, int depth)
    {
        if (depth > 32) return;

        if (node is XText text)
        {
            var value = text.Value.Trim();
            if (value.Length > 0) blocks.Add(Paragraph(para, new[] { TextRun(value, run) }));
            return;
        }

        if (node is not XElement element) return;

        var name = element.Name.LocalName.ToLowerInvariant();
        var style = ParseStyle(element.Attribute("style")?.Value);
        var classes = ClassesOf(element);

        var elementRun = run.With(name, style, classes);
        var elementPara = para.With(name, style, classes);

        switch (name)
        {
            case "p":
            case "div":
                elementPara.StyleId = StyleIdForClasses(classes) ?? elementPara.StyleId;
                if (classes.Contains("dms-doc-toc")) { blocks.AddRange(TableOfContents(element, context)); return; }
                AppendMixed(element, blocks, elementRun, elementPara, context, depth);
                return;

            case "blockquote":
                elementPara.StyleId = "Quote";
                AppendMixed(element, blocks, elementRun, elementPara, context, depth);
                return;

            case "figcaption":
            case "caption":
                elementPara.StyleId = "Caption";
                AppendMixed(element, blocks, elementRun, elementPara, context, depth);
                return;

            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                elementPara.StyleId = "Heading" + name[1];
                AppendMixed(element, blocks, elementRun, elementPara, context, depth);
                return;

            case "pre":
                elementPara.StyleId = "DmsPreformatted";
                AppendMixed(element, blocks, elementRun, elementPara, context, depth);
                return;

            case "ul":
            case "ol":
                AppendList(element, blocks, elementRun, elementPara, context,
                    ordered: name == "ol", level: 0, numberingId: NumberingIdFor(element, name == "ol"));
                return;

            case "table":
                blocks.Add(BuildTable(element, elementRun, context));
                blocks.Add(EmptyParagraph());
                return;

            case "hr":
                blocks.Add(IsPageBreak(classes, style) ? PageBreakParagraph() : HorizontalRuleParagraph());
                return;

            case "br":
                blocks.Add(EmptyParagraph());
                return;

            case "figure":
            case "colgroup":
            case "col":
                foreach (var child in element.Nodes())
                    AppendBlock(child, blocks, elementRun, elementPara, context, depth + 1);
                return;

            default:
                blocks.Add(BuildParagraph(element, elementRun, elementPara, context));
                return;
        }
    }

    /// <summary>
    /// A document class on a paragraph selects a named Word style, so "Title" in
    /// the editor's style gallery is the Title style in the exported file rather
    /// than a paragraph that merely looks like one.
    /// </summary>
    private static string? StyleIdForClasses(IReadOnlyCollection<string> classes)
    {
        if (classes.Contains("dms-doc-title")) return "Title";
        if (classes.Contains("dms-doc-subtitle")) return "Subtitle";
        if (classes.Contains("dms-doc-caption")) return "Caption";
        if (classes.Contains("dms-doc-nospacing")) return "NoSpacing";
        if (classes.Contains("dms-doc-quote")) return "Quote";
        return null;
    }

    /// <summary>
    /// Bullets are numId 1, plain numbers numId 2, and legal multilevel numbering
    /// (1, 1.1, 1.1.1) numId 3. The third is selected by a class the editor puts
    /// on the list, because it is how pharmaceutical procedures number their
    /// clauses and a flat list cannot express it.
    /// </summary>
    private static int NumberingIdFor(XElement list, bool ordered)
    {
        if (ClassesOf(list).Contains("dms-doc-legal")) return 3;
        return ordered ? 2 : 1;
    }

    /// <summary>
    /// The document's index (URS-7.36.2), as a Word TOC field whose CACHED
    /// RESULT is the index the author saw and the reviewer approved.
    ///
    /// <para><b>Why the cached result matters</b></para>
    /// A bare TOC field is empty until somebody opens the file in Word and
    /// updates it. For a controlled document that is unacceptable twice over:
    /// the reviewer approves a document whose index they cannot see, and the
    /// PDF master is rendered by a converter, not by Word, so the printed
    /// controlled copy would carry a blank contents page. Writing the entries
    /// into the field's result means the index is in the file - it renders
    /// everywhere - while the field is still a field, so a later edit in Word
    /// can refresh it.
    ///
    /// <para>
    /// A complex field is used rather than <c>fldSimple</c> because the result
    /// spans several paragraphs, one per entry, and a simple field cannot: the
    /// field begins in the first paragraph and ends in one of its own.
    /// </para>
    /// </summary>
    private static List<XElement> TableOfContents(XElement block, WriteContext context)
    {
        context.HasFields = true;

        var paragraphs = new List<XElement>();

        // The heading above the entries, if the editor generated one.
        var title = block.Descendants()
            .FirstOrDefault(e => ClassesOf(e).Contains("dms-doc-toc-title"));

        if (title is not null)
        {
            var titleText = CollapseRuns(title.Value).Trim();

            if (titleText.Length > 0)
            {
                paragraphs.Add(new XElement(W + "p",
                    new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", "TOCHeading"))),
                    TextRun(titleText, new RunFormat())));
            }
        }

        var entries = block.Descendants()
            .Where(e => ClassesOf(e).Contains("dms-doc-toc-entry"))
            .ToList();

        // The right-aligned, dot-leadered tab the page number sits on, at the
        // edge of the text area - the same width the rest of the document uses.
        var tab = new XElement(W + "tabs",
            new XElement(W + "tab",
                new XAttribute(W + "val", "right"),
                new XAttribute(W + "leader", "dot"),
                new XAttribute(W + "pos", Int(context.Page.ContentWidthTwips))));

        var started = false;

        foreach (var entry in entries)
        {
            var level = LevelOfIndexEntry(entry);

            var paragraph = new XElement(W + "p",
                new XElement(W + "pPr",
                    new XElement(W + "pStyle", new XAttribute(W + "val", "TOC" + level)),
                    new XElement(tab)));

            if (!started)
            {
                paragraph.Add(FieldChar("begin", dirty: false));
                paragraph.Add(InstructionText(" TOC \\o \"1-4\" \\h \\z \\u "));
                paragraph.Add(FieldChar("separate", dirty: false));
                started = true;
            }

            var text = TextOfIndexPart(entry, "dms-doc-toc-text");
            var page = TextOfIndexPart(entry, "dms-doc-toc-page");

            if (text.Length == 0) text = CollapseRuns(entry.Value).Trim();

            paragraph.Add(TextRun(text, new RunFormat()));

            if (page.Length > 0)
            {
                paragraph.Add(new XElement(W + "r", new XElement(W + "tab")));
                paragraph.Add(TextRun(page, new RunFormat()));
            }

            paragraphs.Add(paragraph);
        }

        if (!started)
        {
            // An index with no headings under it is still a field, so that an
            // author who adds headings later gets the index by updating it.
            paragraphs.Add(new XElement(W + "p",
                new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", "TOC1"))),
                FieldChar("begin", dirty: true),
                InstructionText(" TOC \\o \"1-4\" \\h \\z \\u "),
                FieldChar("separate", dirty: false),
                FieldChar("end", dirty: false)));

            return paragraphs;
        }

        paragraphs.Add(new XElement(W + "p", FieldChar("end", dirty: false)));
        return paragraphs;
    }

    private static int LevelOfIndexEntry(XElement entry)
    {
        var classes = ClassesOf(entry);

        for (var level = 4; level >= 1; level--)
            if (classes.Contains("dms-doc-toc-l" + level)) return level;

        return 1;
    }

    private static string TextOfIndexPart(XElement entry, string className)
    {
        var part = entry.Descendants().FirstOrDefault(e => ClassesOf(e).Contains(className));
        return part is null ? string.Empty : CollapseRuns(part.Value).Trim();
    }

    private static XElement FieldChar(string type, bool dirty)
    {
        var character = new XElement(W + "fldChar", new XAttribute(W + "fldCharType", type));
        if (dirty) character.Add(new XAttribute(W + "dirty", "true"));
        return new XElement(W + "r", character);
    }

    private static XElement InstructionText(string instruction) => new(W + "r",
        new XElement(W + "instrText", new XAttribute(XNamespace.Xml + "space", "preserve"), instruction));

    private static bool IsPageBreak(IReadOnlyCollection<string> classes, IReadOnlyDictionary<string, string> style)
        => classes.Contains("dms-doc-pagebreak")
           || (style.TryGetValue("page-break-after", out var after)
               && after.Contains("always", StringComparison.OrdinalIgnoreCase))
           || (style.TryGetValue("page-break-before", out var before)
               && before.Contains("always", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Handles a paragraph that contains block-level children.
    ///
    /// <para>
    /// Browsers produce this constantly: <c>execCommand('insertUnorderedList')</c>
    /// inside a paragraph yields <c>&lt;p&gt;&lt;ul&gt;…&lt;/ul&gt;&lt;/p&gt;</c>,
    /// which is invalid nesting but renders perfectly, so the author has no way
    /// to know. Treating the whole thing as one paragraph would flatten the list
    /// into a run of text - the bullets would vanish on save and not be missed
    /// until the controlled PDF came out wrong.
    /// </para>
    /// </summary>
    private static void AppendMixed(XElement element, List<XElement> blocks, RunFormat run,
        ParagraphFormat para, WriteContext context, int depth)
    {
        var hasBlockChild = element.Elements()
            .Any(child => BlockElements.Contains(child.Name.LocalName.ToLowerInvariant()));

        if (!hasBlockChild)
        {
            blocks.Add(BuildParagraph(element, run, para, context));
            return;
        }

        var pending = new List<XElement>();

        void Flush()
        {
            if (pending.Count == 0) return;
            blocks.Add(Paragraph(para, pending));
            pending = new List<XElement>();
        }

        foreach (var node in element.Nodes())
        {
            if (node is XElement child && BlockElements.Contains(child.Name.LocalName.ToLowerInvariant()))
            {
                Flush();
                AppendBlock(child, blocks, run, para, context, depth + 1);
                continue;
            }

            if (node is XText text)
            {
                if (text.Value.Trim().Length > 0) pending.Add(TextRun(text.Value, run));
                continue;
            }

            if (node is XElement inline)
                AppendInline(new XElement("wrap", inline), pending, run, context, 0);
        }

        Flush();
    }

    private static XElement BuildParagraph(XElement element, RunFormat run, ParagraphFormat para,
        WriteContext context)
    {
        var runs = new List<XElement>();
        AppendInline(element, runs, run, context, depth: 0);

        if (runs.Count == 0) runs.Add(TextRun(string.Empty, run));
        return Paragraph(para, runs);
    }

    private static void AppendInline(XElement element, List<XElement> runs, RunFormat run,
        WriteContext context, int depth)
    {
        if (depth > 32) return;

        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                if (text.Value.Length > 0) runs.Add(TextRun(text.Value, run));
                continue;
            }

            if (node is not XElement child) continue;

            var name = child.Name.LocalName.ToLowerInvariant();
            var style = ParseStyle(child.Attribute("style")?.Value);
            var childRun = run.With(name, style, ClassesOf(child));

            switch (name)
            {
                case "br":
                    runs.Add(new XElement(W + "r", new XElement(W + "br")));
                    break;

                case "img":
                    var drawing = BuildImageRun(child, context);
                    if (drawing is not null) runs.Add(drawing);
                    break;

                case "hr":
                    runs.Add(new XElement(W + "r",
                        new XElement(W + "br", new XAttribute(W + "type", "page"))));
                    break;

                case "a":
                    AppendHyperlink(child, runs, childRun, context, depth);
                    break;

                default:
                    AppendInline(child, runs, childRun, context, depth + 1);
                    break;
            }
        }
    }

    /// <summary>
    /// A real <c>w:hyperlink</c> with an external relationship, so the link is
    /// clickable in Word rather than being the address printed in brackets.
    /// </summary>
    private static void AppendHyperlink(XElement anchor, List<XElement> runs, RunFormat run,
        WriteContext context, int depth)
    {
        var href = anchor.Attribute("href")?.Value;

        if (string.IsNullOrWhiteSpace(href))
        {
            AppendInline(anchor, runs, run, context, depth + 1);
            return;
        }

        var linkRun = run;
        linkRun.StyleId = "Hyperlink";

        var inner = new List<XElement>();
        AppendInline(anchor, inner, linkRun, context, depth + 1);

        if (inner.Count == 0) inner.Add(TextRun(href, linkRun));

        // An in-application link ("/Documents/Details/12") is not something Word
        // can follow from a file on disk, so it stays as styled text. Only
        // absolute addresses become relationships.
        if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            !href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            runs.AddRange(inner);
            return;
        }

        var id = context.AddHyperlink(href);
        var hyperlink = new XElement(W + "hyperlink", new XAttribute(R + "id", id));
        foreach (var element in inner) hyperlink.Add(element);

        runs.Add(hyperlink);
    }

    private static void AppendList(XElement list, List<XElement> blocks, RunFormat run, ParagraphFormat para,
        WriteContext context, bool ordered, int level, int numberingId)
    {
        if (level > 8) return;

        foreach (var item in list.Elements())
        {
            var name = item.Name.LocalName.ToLowerInvariant();

            if (name is "ul" or "ol")
            {
                AppendList(item, blocks, run, para, context, name == "ol", level + 1,
                    NumberingIdFor(item, name == "ol"));
                continue;
            }

            if (name != "li") continue;

            var itemStyle = ParseStyle(item.Attribute("style")?.Value);
            var itemClasses = ClassesOf(item);
            var itemRun = run.With(name, itemStyle, itemClasses);
            var itemPara = para.With(name, itemStyle, itemClasses);

            itemPara.StyleId = "ListParagraph";
            itemPara.NumberingId = numberingId;
            itemPara.NumberingLevel = level;

            // The list brings its own indent from the numbering definition;
            // keeping an inherited one as well indents the item twice.
            itemPara.IndentTwips = 0;

            var inlineRuns = new List<XElement>();
            var nested = new List<XElement>();

            foreach (var node in item.Nodes())
            {
                if (node is XElement child &&
                    child.Name.LocalName.ToLowerInvariant() is "ul" or "ol" or "table" or "p" or "div")
                {
                    nested.Add(child);
                    continue;
                }

                if (node is XText text)
                {
                    if (text.Value.Length > 0) inlineRuns.Add(TextRun(text.Value, itemRun));
                    continue;
                }

                if (node is XElement inline)
                    AppendInline(new XElement("wrap", inline), inlineRuns, itemRun, context, 0);
            }

            if (inlineRuns.Count == 0) inlineRuns.Add(TextRun(string.Empty, itemRun));
            blocks.Add(Paragraph(itemPara, inlineRuns));

            foreach (var child in nested)
            {
                var childName = child.Name.LocalName.ToLowerInvariant();

                if (childName is "ul" or "ol")
                {
                    AppendList(child, blocks, run, para, context, childName == "ol", level + 1,
                        NumberingIdFor(child, childName == "ol"));
                }
                else if (childName == "table")
                {
                    blocks.Add(BuildTable(child, run, context));
                    blocks.Add(EmptyParagraph());
                }
                else
                {
                    // A paragraph inside a list item continues the item rather
                    // than starting a new bullet, which is what Word does.
                    var continuation = para.With(childName, ParseStyle(child.Attribute("style")?.Value),
                        ClassesOf(child));
                    continuation.StyleId = "ListParagraph";
                    continuation.IndentTwips = 720 * (level + 1);

                    AppendMixed(child, blocks, run, continuation, context, 1);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Tables
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a table, resolving row and column spans into the grid model OOXML
    /// requires.
    ///
    /// <para><b>Why a grid model is unavoidable</b></para>
    /// HTML expresses a merged cell by OMITTING the cells it covers.
    /// WordprocessingML expresses it by INCLUDING them, each marked as a
    /// continuation of the merge. So the two cannot be translated cell by cell:
    /// the covered positions have to be worked out first, which means building
    /// an occupancy grid and then emitting every row across its full width.
    /// Getting this wrong does not produce a slightly wrong table - it produces
    /// rows with different cell counts, which Word reports as unreadable content.
    /// </para>
    /// </summary>
    private static XElement BuildTable(XElement table, RunFormat run, WriteContext context)
    {
        var grid = TableGrid.Build(table);
        var widths = ColumnWidths(table, grid.Columns, context.Page.ContentWidthTwips);
        var tableStyle = ParseStyle(table.Attribute("style")?.Value);
        var tableClasses = ClassesOf(table);

        var tblPr = new XElement(W + "tblPr",
            new XElement(W + "tblStyle", new XAttribute(W + "val", "TableGrid")),
            new XElement(W + "tblW",
                new XAttribute(W + "w", Int(widths.Sum())),
                new XAttribute(W + "type", "dxa")),
            new XElement(W + "tblLayout", new XAttribute(W + "type", "fixed")),
            new XElement(W + "tblLook",
                new XAttribute(W + "val", "04A0"),
                new XAttribute(W + "firstRow", grid.HasHeaderRow ? "1" : "0"),
                new XAttribute(W + "lastRow", "0"),
                new XAttribute(W + "firstColumn", "0"),
                new XAttribute(W + "lastColumn", "0"),
                new XAttribute(W + "noHBand", "0"),
                new XAttribute(W + "noVBand", "1")));

        if (!tableClasses.Contains("dms-doc-borderless"))
        {
            tblPr.AddFirst(new XElement(W + "tblBorders",
                DocxPackageParts.Border("top"), DocxPackageParts.Border("left"),
                DocxPackageParts.Border("bottom"), DocxPackageParts.Border("right"),
                DocxPackageParts.Border("insideH"), DocxPackageParts.Border("insideV")));
        }

        var tbl = new XElement(W + "tbl", tblPr,
            new XElement(W + "tblGrid",
                widths.Select(width => new XElement(W + "gridCol", new XAttribute(W + "w", Int(width))))));

        for (var rowIndex = 0; rowIndex < grid.Rows.Count; rowIndex++)
        {
            var tr = new XElement(W + "tr");
            var rowElement = grid.RowElements[rowIndex];
            var rowStyle = ParseStyle(rowElement.Attribute("style")?.Value);

            var trPr = new XElement(W + "trPr");

            // A header row repeats at the top of every page the table spans. On a
            // multi-page batch record that is the difference between a usable
            // form and an unreadable one.
            if (grid.IsHeaderRow(rowIndex)) trPr.Add(new XElement(W + "tblHeader"));

            if (rowStyle.TryGetValue("height", out var height))
            {
                var twips = ParseLengthToTwips(height);
                if (twips > 0)
                    trPr.Add(new XElement(W + "trHeight",
                        new XAttribute(W + "val", Int(twips)),
                        new XAttribute(W + "hRule", "atLeast")));
            }

            if (trPr.HasElements) tr.Add(trPr);

            for (var column = 0; column < grid.Columns;)
            {
                var slot = grid.Rows[rowIndex][column];

                if (slot is null)
                {
                    // A ragged row: HTML allows a row with too few cells, OOXML
                    // does not. Fill it rather than emit a short row.
                    tr.Add(FillerCell(widths[column]));
                    column++;
                    continue;
                }

                var span = Math.Min(slot.ColSpan, grid.Columns - column);
                var width = widths.Skip(column).Take(span).Sum();

                tr.Add(slot.IsOrigin
                    ? BuildCell(slot, width, span, run, context)
                    : ContinuationCell(slot, width, span));

                column += span;
            }

            tbl.Add(tr);
        }

        if (!tbl.Elements(W + "tr").Any())
            tbl.Add(new XElement(W + "tr", FillerCell(widths.Sum())));

        return tbl;
    }

    private static XElement BuildCell(TableGrid.Slot slot, int width, int span, RunFormat run,
        WriteContext context)
    {
        var cell = slot.Element!;
        var cellName = cell.Name.LocalName.ToLowerInvariant();
        var cellStyle = ParseStyle(cell.Attribute("style")?.Value);
        var cellClasses = ClassesOf(cell);

        var cellRun = run.With(cellName, cellStyle, cellClasses);
        if (slot.IsHeader) cellRun.Bold = true;

        var cellPara = new ParagraphFormat().With(cellName, cellStyle, cellClasses);

        var tcPr = new XElement(W + "tcPr",
            new XElement(W + "tcW",
                new XAttribute(W + "w", Int(width)),
                new XAttribute(W + "type", "dxa")));

        if (span > 1) tcPr.Add(new XElement(W + "gridSpan", new XAttribute(W + "val", Int(span))));

        if (slot.RowSpan > 1)
            tcPr.Add(new XElement(W + "vMerge", new XAttribute(W + "val", "restart")));

        if (cellStyle.TryGetValue("background-color", out var fill) && TryColour(fill, out var hex))
            tcPr.Add(Shading(hex));
        else if (slot.IsHeader)
            tcPr.Add(Shading("EEF2F7"));

        if (cellStyle.TryGetValue("vertical-align", out var valign))
        {
            var value = valign.Trim().ToLowerInvariant() switch
            {
                "middle" => "center", "bottom" => "bottom", "top" => "top", _ => null
            };

            if (value is not null)
                tcPr.Add(new XElement(W + "vAlign", new XAttribute(W + "val", value)));
        }

        var borders = CellBorders(cellStyle);
        if (borders is not null) tcPr.Add(borders);

        var tc = new XElement(W + "tc", tcPr);

        var cellBlocks = new List<XElement>();
        foreach (var node in cell.Nodes())
            AppendBlock(node, cellBlocks, cellRun, cellPara, context, depth: 1);

        if (cellBlocks.Count == 0) cellBlocks.Add(EmptyParagraph());
        if (cellBlocks[^1].Name == W + "tbl") cellBlocks.Add(EmptyParagraph());

        foreach (var block in cellBlocks) tc.Add(block);
        return tc;
    }

    /// <summary>
    /// A cell covered by a vertical merge. It carries no content of its own -
    /// <c>w:vMerge</c> with no value means "continue the merge above" - but it
    /// must exist, and it must still contain a paragraph.
    /// </summary>
    private static XElement ContinuationCell(TableGrid.Slot slot, int width, int span)
    {
        var tcPr = new XElement(W + "tcPr",
            new XElement(W + "tcW",
                new XAttribute(W + "w", Int(width)),
                new XAttribute(W + "type", "dxa")));

        if (span > 1) tcPr.Add(new XElement(W + "gridSpan", new XAttribute(W + "val", Int(span))));
        tcPr.Add(new XElement(W + "vMerge"));

        return new XElement(W + "tc", tcPr, EmptyParagraph());
    }

    private static XElement FillerCell(int width) => new(W + "tc",
        new XElement(W + "tcPr",
            new XElement(W + "tcW",
                new XAttribute(W + "w", Int(width)),
                new XAttribute(W + "type", "dxa"))),
        EmptyParagraph());

    private static XElement Shading(string hex) => new(W + "shd",
        new XAttribute(W + "val", "clear"),
        new XAttribute(W + "color", "auto"),
        new XAttribute(W + "fill", hex));

    private static XElement? CellBorders(IReadOnlyDictionary<string, string> style)
    {
        if (!style.TryGetValue("border", out var border)) return null;

        // "none" and "0" both mean the author removed the border on this cell.
        var lower = border.Trim().ToLowerInvariant();
        if (!lower.Contains("none", StringComparison.Ordinal) && !lower.StartsWith("0", StringComparison.Ordinal))
            return null;

        XElement None(string side) => new(W + side, new XAttribute(W + "val", "nil"));

        return new XElement(W + "tcBorders",
            None("top"), None("left"), None("bottom"), None("right"));
    }

    /// <summary>
    /// Column widths in twips, from the author's own <c>&lt;col&gt;</c> widths
    /// where they exist and an equal division of the text width where they do
    /// not.
    ///
    /// <para>
    /// Scaled to the page's real content width, which is why page setup had to
    /// come before this: a table sized for A4 portrait is the wrong width the
    /// moment somebody turns the page landscape, and a table wider than its page
    /// is the single most common way an exported document looks broken.
    /// </para>
    /// </summary>
    private static int[] ColumnWidths(XElement table, int columns, int contentWidthTwips)
    {
        if (columns <= 0) return new[] { contentWidthTwips };

        var declared = new int[columns];
        var any = false;
        var index = 0;

        foreach (var col in table.Descendants().Where(e =>
                     e.Name.LocalName.Equals("col", StringComparison.OrdinalIgnoreCase)))
        {
            if (index >= columns) break;

            var span = Math.Max(1, ParseInt(col.Attribute("span")?.Value, 1));
            var style = ParseStyle(col.Attribute("style")?.Value);

            if (style.TryGetValue("width", out var width))
            {
                var twips = ParseWidthToTwips(width, contentWidthTwips);
                for (var i = 0; i < span && index < columns; i++, index++)
                {
                    declared[index] = twips;
                    if (twips > 0) any = true;
                }
            }
            else
            {
                index += span;
            }
        }

        if (!any)
        {
            var even = contentWidthTwips / columns;
            var widths = Enumerable.Repeat(even, columns).ToArray();
            widths[^1] += contentWidthTwips - (even * columns);
            return widths;
        }

        // Fill any column the author did not size with an equal share of what is
        // left, then scale the whole row to the page so it cannot overflow.
        var used = declared.Sum();
        var unsized = declared.Count(w => w <= 0);

        if (unsized > 0)
        {
            var remaining = Math.Max(columns * 360, contentWidthTwips - used);
            var share = remaining / unsized;
            for (var i = 0; i < columns; i++) if (declared[i] <= 0) declared[i] = share;
        }

        var total = declared.Sum();
        if (total > contentWidthTwips && total > 0)
        {
            var scale = contentWidthTwips / (double)total;
            for (var i = 0; i < columns; i++) declared[i] = Math.Max(288, (int)(declared[i] * scale));
        }

        return declared;
    }

    private static int ParseWidthToTwips(string value, int contentWidthTwips)
    {
        var v = value.Trim();

        if (v.EndsWith("%", StringComparison.Ordinal) &&
            double.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return (int)Math.Round(contentWidthTwips * Math.Clamp(percent, 1, 100) / 100.0);

        return ParseLengthToTwips(v);
    }

    /// <summary>
    /// The occupancy grid of an HTML table.
    ///
    /// <para>
    /// Every position in the table gets a slot naming the cell that occupies it
    /// and whether this is the position the cell was declared at. That is exactly
    /// the information OOXML needs and HTML does not carry.
    /// </para>
    /// </summary>
    private sealed class TableGrid
    {
        internal sealed class Slot
        {
            public XElement? Element { get; init; }
            public bool IsOrigin { get; init; }
            public bool IsHeader { get; init; }
            public int ColSpan { get; init; } = 1;
            public int RowSpan { get; init; } = 1;
        }

        public List<Slot?[]> Rows { get; } = new();
        public List<XElement> RowElements { get; } = new();
        public int Columns { get; private set; } = 1;
        public bool HasHeaderRow { get; private set; }

        private readonly HashSet<int> _headerRows = new();

        public bool IsHeaderRow(int index) => _headerRows.Contains(index);

        public static TableGrid Build(XElement table)
        {
            var grid = new TableGrid();

            var rowElements = table.Descendants()
                .Where(e => e.Name.LocalName.Equals("tr", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (rowElements.Count == 0) return grid;

            // First pass: how wide is the widest row, counting spans and anything
            // carried down from a rowspan above.
            var pending = new Dictionary<int, (int Rows, int Span)>();
            var width = 0;

            foreach (var row in rowElements)
            {
                var column = 0;
                var occupied = new HashSet<int>();

                foreach (var key in pending.Keys.ToList())
                {
                    var (rows, span) = pending[key];
                    if (rows <= 0) { pending.Remove(key); continue; }
                    for (var i = 0; i < span; i++) occupied.Add(key + i);
                    pending[key] = (rows - 1, span);
                }

                foreach (var cell in row.Elements().Where(IsCell))
                {
                    while (occupied.Contains(column)) column++;

                    var colSpan = Math.Max(1, ParseInt(cell.Attribute("colspan")?.Value, 1));
                    var rowSpan = Math.Max(1, ParseInt(cell.Attribute("rowspan")?.Value, 1));

                    if (rowSpan > 1) pending[column] = (rowSpan - 1, colSpan);

                    column += colSpan;
                }

                width = Math.Max(width, Math.Max(column, occupied.Count == 0 ? 0 : occupied.Max() + 1));
            }

            grid.Columns = Math.Max(1, Math.Min(width, 63));   // Word's own column limit

            // Second pass: place every cell, and carry vertical merges down.
            var carried = new Slot?[grid.Columns];
            var carriedRemaining = new int[grid.Columns];

            for (var rowIndex = 0; rowIndex < rowElements.Count; rowIndex++)
            {
                var row = rowElements[rowIndex];
                var slots = new Slot?[grid.Columns];

                for (var c = 0; c < grid.Columns; c++)
                {
                    if (carriedRemaining[c] <= 0 || carried[c] is null) continue;

                    slots[c] = new Slot
                    {
                        Element = carried[c]!.Element,
                        IsOrigin = false,
                        IsHeader = carried[c]!.IsHeader,
                        ColSpan = carried[c]!.ColSpan,
                        RowSpan = carried[c]!.RowSpan
                    };

                    carriedRemaining[c]--;
                }

                var column = 0;
                var isHeaderRow = row.Ancestors()
                    .Any(a => a.Name.LocalName.Equals("thead", StringComparison.OrdinalIgnoreCase));

                foreach (var cell in row.Elements().Where(IsCell))
                {
                    while (column < grid.Columns && slots[column] is not null) column++;
                    if (column >= grid.Columns) break;

                    var colSpan = Math.Max(1, Math.Min(ParseInt(cell.Attribute("colspan")?.Value, 1),
                        grid.Columns - column));
                    var rowSpan = Math.Max(1, ParseInt(cell.Attribute("rowspan")?.Value, 1));
                    var isHeaderCell = cell.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase);

                    var slot = new Slot
                    {
                        Element = cell,
                        IsOrigin = true,
                        IsHeader = isHeaderCell || isHeaderRow,
                        ColSpan = colSpan,
                        RowSpan = rowSpan
                    };

                    slots[column] = slot;

                    if (rowSpan > 1)
                    {
                        carried[column] = slot;
                        carriedRemaining[column] = rowSpan - 1;
                    }

                    if (slot.IsHeader) grid.HasHeaderRow = true;
                    column += colSpan;
                }

                if (isHeaderRow || (rowIndex == 0 && row.Elements().Where(IsCell)
                        .All(c => c.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase))
                    && row.Elements().Any(IsCell)))
                {
                    grid._headerRows.Add(rowIndex);
                    grid.HasHeaderRow = true;
                }

                grid.Rows.Add(slots);
                grid.RowElements.Add(row);
            }

            return grid;
        }

        private static bool IsCell(XElement element)
            => element.Name.LocalName.Equals("td", StringComparison.OrdinalIgnoreCase)
               || element.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Images
    // -------------------------------------------------------------------------

    private static XElement? BuildImageRun(XElement img, WriteContext context)
    {
        var src = img.Attribute("src")?.Value;
        if (string.IsNullOrWhiteSpace(src)) return null;

        var comma = src.IndexOf(',');
        if (comma < 0 || !src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

        var mediaType = src[5..comma];
        var semi = mediaType.IndexOf(';');
        if (semi > 0) mediaType = mediaType[..semi];

        byte[] bytes;
        try { bytes = Convert.FromBase64String(src[(comma + 1)..]); }
        catch (FormatException) { return null; }

        if (bytes.Length == 0) return null;

        var extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpeg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            _ => null
        };

        if (extension is null) return null;

        var image = context.AddImage(extension, bytes);
        var (pixelWidth, pixelHeight) = ImageDimensions.Measure(bytes, extension);

        var attrWidth = ParseInt(img.Attribute("width")?.Value, 0);
        var attrHeight = ParseInt(img.Attribute("height")?.Value, 0);
        if (attrWidth > 0) pixelWidth = attrWidth;
        if (attrHeight > 0) pixelHeight = attrHeight;

        if (pixelWidth <= 0) pixelWidth = 480;
        if (pixelHeight <= 0) pixelHeight = 320;

        var cx = (long)pixelWidth * EmuPerPixel;
        var cy = (long)pixelHeight * EmuPerPixel;

        // Scale to the page's real text width, preserving aspect ratio. A photo
        // from a phone is 4000 px wide and would otherwise run off the paper.
        var maxWidth = (long)context.Page.ContentWidthTwips * EmuPerTwip;

        if (cx > maxWidth && cx > 0)
        {
            cy = (long)(cy * (maxWidth / (double)cx));
            cx = maxWidth;
        }

        if (cy <= 0) cy = 1;

        var alt = img.Attribute("alt")?.Value ?? "Figure";
        var index = context.Images.Count;

        return new XElement(W + "r",
            new XElement(W + "drawing",
                new XElement(Wp + "inline",
                    new XAttribute("distT", "0"), new XAttribute("distB", "0"),
                    new XAttribute("distL", "0"), new XAttribute("distR", "0"),
                    new XElement(Wp + "extent", new XAttribute("cx", Long(cx)), new XAttribute("cy", Long(cy))),
                    new XElement(Wp + "docPr",
                        new XAttribute("id", Int(index)),
                        new XAttribute("name", $"Picture {index}"),
                        new XAttribute("descr", alt)),
                    new XElement(A + "graphic",
                        new XElement(A + "graphicData",
                            new XAttribute("uri", DocxPackageParts.DrawingPic),
                            new XElement(Pic + "pic",
                                new XElement(Pic + "nvPicPr",
                                    new XElement(Pic + "cNvPr",
                                        new XAttribute("id", Int(index)),
                                        new XAttribute("name", image.FileName),
                                        new XAttribute("descr", alt)),
                                    new XElement(Pic + "cNvPicPr")),
                                new XElement(Pic + "blipFill",
                                    new XElement(A + "blip", new XAttribute(R + "embed", image.RelationshipId)),
                                    new XElement(A + "stretch", new XElement(A + "fillRect"))),
                                new XElement(Pic + "spPr",
                                    new XElement(A + "xfrm",
                                        new XElement(A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
                                        new XElement(A + "ext",
                                            new XAttribute("cx", Long(cx)),
                                            new XAttribute("cy", Long(cy)))),
                                    new XElement(A + "prstGeom",
                                        new XAttribute("prst", "rect"),
                                        new XElement(A + "avLst")))))))));
    }

    // -------------------------------------------------------------------------
    // Paragraphs and runs
    // -------------------------------------------------------------------------

    private static XElement Paragraph(ParagraphFormat format, IEnumerable<XElement> runs)
    {
        var paragraph = new XElement(W + "p");
        var properties = new XElement(W + "pPr");

        if (!string.IsNullOrEmpty(format.StyleId))
            properties.Add(new XElement(W + "pStyle", new XAttribute(W + "val", format.StyleId)));

        if (format.KeepNext) properties.Add(new XElement(W + "keepNext"));

        if (format.NumberingId > 0)
            properties.Add(new XElement(W + "numPr",
                new XElement(W + "ilvl", new XAttribute(W + "val", Int(format.NumberingLevel))),
                new XElement(W + "numId", new XAttribute(W + "val", Int(format.NumberingId)))));

        if (format.PageBreakBefore) properties.Add(new XElement(W + "pageBreakBefore"));

        if (format.Borders is not null) properties.Add(format.Borders);

        if (format.Shading is not null) properties.Add(Shading(format.Shading));

        var spacing = SpacingElement(format);
        if (spacing is not null) properties.Add(spacing);

        var indent = IndentElement(format);
        if (indent is not null) properties.Add(indent);

        if (!string.IsNullOrEmpty(format.Alignment))
            properties.Add(new XElement(W + "jc", new XAttribute(W + "val", format.Alignment)));

        if (properties.HasElements) paragraph.Add(properties);

        foreach (var run in runs) paragraph.Add(run);

        // Preformatted text is the one place where the spacing IS the content.
        if (format.StyleId != "DmsPreformatted") CollapseWhitespace(paragraph);

        return paragraph;
    }

    /// <summary>
    /// Applies HTML's whitespace rules to a finished paragraph.
    ///
    /// <para><b>Why this is necessary, and why it belongs here</b></para>
    /// HTML collapses any run of spaces, tabs and newlines to a single space, and
    /// ignores whitespace at the start and end of a block. WordprocessingML does
    /// not: every space in a <c>w:t</c> is a space on the page. So source HTML
    /// that is merely INDENTED - a template body, a document derived from an
    /// uploaded file, anything a human formatted for readability - exports with
    /// the indentation baked into the text. The author sees one space in the
    /// editor and finds sixteen in the Word file.
    ///
    /// <para>
    /// This is the same family of defect as the seven boxes after a pasted
    /// bullet, and the customer reported it in the same sentence: "unexpected
    /// square/blank characters <b>or additional spacing</b>". Both come from
    /// treating HTML text as if it were literal text.
    /// </para>
    /// <para>
    /// It runs across the whole paragraph rather than per run, because the
    /// boundary between two runs is exactly where a double space hides: "the
    /// <c>&lt;b&gt;hot&lt;/b&gt;</c> water" is three runs, and each one's
    /// whitespace is only meaningful relative to its neighbours.
    /// </para>
    /// </summary>
    private static void CollapseWhitespace(XElement paragraph)
    {
        var texts = paragraph.Descendants(W + "t").ToList();
        if (texts.Count == 0) return;

        var previousEndedWithSpace = true;   // start of a block: leading space is dropped

        foreach (var text in texts)
        {
            var collapsed = CollapseRuns(text.Value);

            if (previousEndedWithSpace && collapsed.StartsWith(' ')) collapsed = collapsed[1..];

            previousEndedWithSpace = collapsed.EndsWith(' ');
            text.Value = collapsed;
        }

        // Trailing whitespace at the end of a paragraph is invisible in HTML and
        // is a stray space in Word.
        for (var i = texts.Count - 1; i >= 0; i--)
        {
            var trimmed = texts[i].Value.TrimEnd(' ');

            if (trimmed.Length == texts[i].Value.Length) break;

            texts[i].Value = trimmed;
            if (trimmed.Length > 0) break;
        }
    }

    /// <summary>
    /// Any run of ASCII whitespace becomes one space. A non-breaking space is
    /// deliberately NOT whitespace here: it is the character an author uses to
    /// stop "10 mg" breaking across a line, and collapsing it would undo that.
    /// </summary>
    private static string CollapseRuns(string value)
    {
        if (value.Length == 0) return value;

        var sb = new StringBuilder(value.Length);
        var inWhitespace = false;

        foreach (var c in value)
        {
            var isSpace = c is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';

            if (isSpace)
            {
                if (!inWhitespace) sb.Append(' ');
                inWhitespace = true;
                continue;
            }

            inWhitespace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static XElement? SpacingElement(ParagraphFormat format)
    {
        if (format.SpaceBeforeTwips < 0 && format.SpaceAfterTwips < 0 && format.LineSpacing <= 0) return null;

        var spacing = new XElement(W + "spacing");

        if (format.SpaceBeforeTwips >= 0)
            spacing.Add(new XAttribute(W + "before", Int(format.SpaceBeforeTwips)));

        if (format.SpaceAfterTwips >= 0)
            spacing.Add(new XAttribute(W + "after", Int(format.SpaceAfterTwips)));

        if (format.LineSpacing > 0)
        {
            // OOXML's "auto" line rule measures in 240ths of a line, so single
            // spacing is 240 and one-and-a-half is 360.
            spacing.Add(new XAttribute(W + "line", Int((int)Math.Round(format.LineSpacing * 240))));
            spacing.Add(new XAttribute(W + "lineRule", "auto"));
        }

        return spacing;
    }

    private static XElement? IndentElement(ParagraphFormat format)
    {
        if (format.IndentTwips <= 0 && format.RightIndentTwips <= 0 &&
            format.FirstLineIndentTwips <= 0 && format.HangingIndentTwips <= 0) return null;

        var indent = new XElement(W + "ind");

        if (format.IndentTwips > 0) indent.Add(new XAttribute(W + "left", Int(format.IndentTwips)));
        if (format.RightIndentTwips > 0) indent.Add(new XAttribute(W + "right", Int(format.RightIndentTwips)));

        if (format.HangingIndentTwips > 0)
            indent.Add(new XAttribute(W + "hanging", Int(format.HangingIndentTwips)));
        else if (format.FirstLineIndentTwips > 0)
            indent.Add(new XAttribute(W + "firstLine", Int(format.FirstLineIndentTwips)));

        return indent;
    }

    private static XElement EmptyParagraph() => new(W + "p");

    private static XElement StyledEmptyParagraph(string styleId) => new(W + "p",
        new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", styleId))));

    private static XElement PageBreakParagraph()
        => new(W + "p", new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page"))));

    private static XElement HorizontalRuleParagraph()
        => new(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "pBdr",
                    new XElement(W + "bottom",
                        new XAttribute(W + "val", "single"),
                        new XAttribute(W + "sz", "6"),
                        new XAttribute(W + "space", "1"),
                        new XAttribute(W + "color", "808080")))));

    private static XElement TextRun(string text, RunFormat format)
    {
        var run = new XElement(W + "r");
        var properties = new XElement(W + "rPr");

        if (!string.IsNullOrEmpty(format.StyleId))
            properties.Add(new XElement(W + "rStyle", new XAttribute(W + "val", format.StyleId)));

        if (!string.IsNullOrEmpty(format.FontFamily))
            properties.Add(new XElement(W + "rFonts",
                new XAttribute(W + "ascii", format.FontFamily),
                new XAttribute(W + "hAnsi", format.FontFamily),
                new XAttribute(W + "cs", format.FontFamily)));

        if (format.Bold) properties.Add(new XElement(W + "b"));
        if (format.Italic) properties.Add(new XElement(W + "i"));

        if (format.Underline is not null)
            properties.Add(new XElement(W + "u", new XAttribute(W + "val", format.Underline)));

        if (format.Strike) properties.Add(new XElement(W + "strike"));
        if (format.SmallCaps) properties.Add(new XElement(W + "smallCaps"));
        if (format.AllCaps) properties.Add(new XElement(W + "caps"));

        if (format.VerticalAlign is not null)
            properties.Add(new XElement(W + "vertAlign", new XAttribute(W + "val", format.VerticalAlign)));

        if (!string.IsNullOrEmpty(format.Colour))
            properties.Add(new XElement(W + "color", new XAttribute(W + "val", format.Colour)));

        if (!string.IsNullOrEmpty(format.Highlight)) properties.Add(Shading(format.Highlight));

        if (format.FontSize > 0)
        {
            // OOXML measures font size in half-points.
            var halfPoints = Int((int)Math.Round(format.FontSize * 2));
            properties.Add(new XElement(W + "sz", new XAttribute(W + "val", halfPoints)));
            properties.Add(new XElement(W + "szCs", new XAttribute(W + "val", halfPoints)));
        }

        if (properties.HasElements) run.Add(properties);

        // xml:space="preserve" or Word collapses the spaces that separate one
        // formatted run from the next, and "the <b>hot</b> water" comes out as
        // "thehotwater".
        run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
        return run;
    }

    // -------------------------------------------------------------------------
    // Style parsing
    // -------------------------------------------------------------------------

    private static IReadOnlyCollection<string> ClassesOf(XElement element)
    {
        var value = element.Attribute("class")?.Value;
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static Dictionary<string, string> ParseStyle(string? style)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(style)) return map;

        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;

            var property = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Trim();
            if (property.Length > 0 && value.Length > 0) map[property] = value;
        }

        return map;
    }

    internal static bool TryColour(string value, out string hex)
    {
        hex = string.Empty;
        var v = value.Trim();

        if (v.StartsWith("#", StringComparison.Ordinal))
        {
            var digits = v[1..];

            if (digits.Length == 3)
                digits = string.Concat(digits[0], digits[0], digits[1], digits[1], digits[2], digits[2]);

            if (digits.Length != 6 || !digits.All(Uri.IsHexDigit)) return false;

            hex = digits.ToUpperInvariant();
            return true;
        }

        if (v.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = v.IndexOf('(');
            var close = v.IndexOf(')');
            if (open < 0 || close <= open) return false;

            var parts = v[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) return false;

            var channels = new int[3];
            for (var i = 0; i < 3; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out channels[i]))
                    return false;
                if (channels[i] < 0 || channels[i] > 255) return false;
            }

            hex = $"{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}";
            return true;
        }

        hex = v.ToLowerInvariant() switch
        {
            "black" => "000000", "white" => "FFFFFF", "red" => "FF0000", "green" => "008000",
            "blue" => "0000FF", "yellow" => "FFFF00", "grey" or "gray" => "808080",
            "orange" => "FFA500", "silver" => "C0C0C0", "navy" => "000080",
            "maroon" => "800000", "teal" => "008080", "purple" => "800080",
            _ => string.Empty
        };

        return hex.Length == 6;
    }

    private static double ParseFontSize(string value)
    {
        var v = value.Trim().ToLowerInvariant();

        if (v.EndsWith("pt", StringComparison.Ordinal) &&
            double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pt))
            return pt;

        if (v.EndsWith("px", StringComparison.Ordinal) &&
            double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            return px * 0.75;   // 96 px per inch, 72 pt per inch

        if (v.EndsWith("em", StringComparison.Ordinal) &&
            double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var em))
            return em * 11.0;

        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare)) return bare;

        // CSS absolute-size keywords.
        //
        // These reach the writer from documents saved by an earlier build of
        // the editor, where choosing a font size emitted
        // `font-size: xxx-large` instead of a point value. Returning 0 for them
        // meant the size was shown on screen and silently dropped from the
        // .docx - the screen and the exported file disagreed, which for a
        // controlled document is the one outcome that must not happen. The
        // values are the browser defaults against a 16 px medium.
        var px96 = v switch
        {
            "xx-small" => 9.0,
            "x-small" => 10.0,
            "small" => 13.0,
            "medium" => 16.0,
            "large" => 18.0,
            "x-large" => 24.0,
            "xx-large" => 32.0,
            "xxx-large" => 48.0,
            _ => 0.0
        };

        return px96 * 0.75;
    }

    /// <summary>A twip is 1/20 pt, so an inch is 1440 and a CSS pixel is 15.</summary>
    internal static int ParseLengthToTwips(string value)
    {
        var v = value.Trim().ToLowerInvariant();

        if (v.EndsWith("px", StringComparison.Ordinal) &&
            double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
            return (int)Math.Round(px * 15);

        if (v.EndsWith("pt", StringComparison.Ordinal) &&
            double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pt))
            return (int)Math.Round(pt * 20);

        if (v.EndsWith("in", StringComparison.Ordinal) &&
            double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var inches))
            return (int)Math.Round(inches * 1440);

        if (v.EndsWith("cm", StringComparison.Ordinal) &&
            double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cm))
            return (int)Math.Round(cm * 567);

        if (v.EndsWith("mm", StringComparison.Ordinal) &&
            double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
            return (int)Math.Round(mm * 56.7);

        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare))
            return (int)Math.Round(bare * 15);

        return 0;
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Long(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var sb = new StringBuilder(html.Length);
        var inTag = false;

        foreach (var c in html)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; sb.Append('\n'); continue; }
            if (!inTag) sb.Append(c);
        }

        return System.Net.WebUtility.HtmlDecode(sb.ToString());
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();

        // No BOM: some OOXML consumers reject a byte-order mark on a package part.
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    // -------------------------------------------------------------------------
    // Mutable state for one write
    // -------------------------------------------------------------------------

    /// <summary>
    /// Everything a single <see cref="Write"/> accumulates: the images and
    /// hyperlinks that need relationships, and whether any field was emitted.
    /// Passed explicitly rather than held on the class so the writer stays
    /// thread-safe as a singleton.
    /// </summary>
    private sealed class WriteContext
    {
        public WriteContext(DocumentPageSetupDto page) => Page = page;

        public DocumentPageSetupDto Page { get; }
        public List<EmbeddedImage> Images { get; } = new();
        public List<(string Id, string Url)> Hyperlinks { get; } = new();
        public bool HasFields { get; set; }

        public EmbeddedImage AddImage(string extension, byte[] bytes)
        {
            var index = Images.Count + 1;

            var image = new EmbeddedImage
            {
                FileName = $"image{index}.{extension}",
                Extension = extension,
                Bytes = bytes,
                RelationshipId = $"rIdImg{index}"
            };

            Images.Add(image);
            return image;
        }

        public string AddHyperlink(string url)
        {
            // The same address used twice gets one relationship, which is what
            // Word does and what keeps the rels part from growing with every
            // repeated cross-reference.
            var existing = Hyperlinks.FindIndex(h =>
                string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0) return Hyperlinks[existing].Id;

            var id = $"rIdLink{Hyperlinks.Count + 1}";
            Hyperlinks.Add((id, url));
            return id;
        }
    }

    private sealed class EmbeddedImage
    {
        public string FileName { get; init; } = string.Empty;
        public string Extension { get; init; } = string.Empty;
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public string RelationshipId { get; init; } = string.Empty;
    }

    /// <summary>
    /// Paragraph formatting accumulated down the element chain. A class rather
    /// than a struct because it is mutated after construction (a heading sets its
    /// own style id), and copied by <see cref="With"/> so a nested element can
    /// never change its parent's format.
    /// </summary>
    private sealed class ParagraphFormat
    {
        public string? StyleId { get; set; }
        public string? Alignment { get; set; }
        public int IndentTwips { get; set; }
        public int RightIndentTwips { get; set; }
        public int FirstLineIndentTwips { get; set; }
        public int HangingIndentTwips { get; set; }
        public int NumberingId { get; set; }
        public int NumberingLevel { get; set; }
        public bool PageBreakBefore { get; set; }
        public bool KeepNext { get; set; }

        /// <summary>-1 means "inherit from the style"; 0 is a real instruction to remove the space.</summary>
        public int SpaceBeforeTwips { get; set; } = -1;

        public int SpaceAfterTwips { get; set; } = -1;

        /// <summary>Multiplier: 1.0 single, 1.5, 2.0. Zero means inherit.</summary>
        public double LineSpacing { get; set; }

        public XElement? Borders { get; set; }
        public string? Shading { get; set; }

        public ParagraphFormat With(string tag, IReadOnlyDictionary<string, string> style,
            IReadOnlyCollection<string> classes)
        {
            var next = new ParagraphFormat
            {
                StyleId = StyleId,
                Alignment = Alignment,
                IndentTwips = IndentTwips,
                RightIndentTwips = RightIndentTwips,
                FirstLineIndentTwips = FirstLineIndentTwips,
                HangingIndentTwips = HangingIndentTwips,
                NumberingId = NumberingId,
                NumberingLevel = NumberingLevel,
                SpaceBeforeTwips = SpaceBeforeTwips,
                SpaceAfterTwips = SpaceAfterTwips,
                LineSpacing = LineSpacing
            };

            if (tag == "blockquote") next.IndentTwips = Math.Max(next.IndentTwips, 720);

            if (style.TryGetValue("text-align", out var align))
            {
                next.Alignment = align.Trim().ToLowerInvariant() switch
                {
                    "center" => "center",
                    "right" => "right",
                    "justify" => "both",
                    "left" => "left",
                    _ => next.Alignment
                };
            }

            if (style.TryGetValue("margin-left", out var marginLeft))
            {
                var twips = ParseLengthToTwips(marginLeft);
                if (twips > 0) next.IndentTwips = twips;
            }
            else if (style.TryGetValue("padding-left", out var paddingLeft))
            {
                var twips = ParseLengthToTwips(paddingLeft);
                if (twips > 0) next.IndentTwips = twips;
            }

            if (style.TryGetValue("margin-right", out var marginRight))
            {
                var twips = ParseLengthToTwips(marginRight);
                if (twips > 0) next.RightIndentTwips = twips;
            }

            if (style.TryGetValue("text-indent", out var textIndent))
            {
                var twips = ParseLengthToTwips(textIndent);
                if (twips > 0) next.FirstLineIndentTwips = twips;
                else if (twips < 0) next.HangingIndentTwips = -twips;
            }

            if (style.TryGetValue("margin-top", out var marginTop))
            {
                var twips = ParseLengthToTwips(marginTop);
                if (twips >= 0) next.SpaceBeforeTwips = twips;
            }

            if (style.TryGetValue("margin-bottom", out var marginBottom))
            {
                var twips = ParseLengthToTwips(marginBottom);
                if (twips >= 0) next.SpaceAfterTwips = twips;
            }

            if (style.TryGetValue("line-height", out var lineHeight))
                next.LineSpacing = ParseLineHeight(lineHeight);

            if (style.TryGetValue("page-break-before", out var pageBreak) &&
                pageBreak.Contains("always", StringComparison.OrdinalIgnoreCase))
                next.PageBreakBefore = true;

            if (style.TryGetValue("background-color", out var background) && TryColour(background, out var fill))
                next.Shading = fill;

            next.Borders = ParagraphBorders(style);

            if (classes.Contains("dms-doc-center")) next.Alignment = "center";
            if (classes.Contains("dms-doc-keepnext")) next.KeepNext = true;

            return next;
        }

        /// <summary>
        /// A line-height of 1.5 and of "150%" mean the same thing; a value in
        /// points has to be divided by the body size to become a multiplier,
        /// because OOXML's auto line rule is proportional, not absolute.
        /// </summary>
        private static double ParseLineHeight(string value)
        {
            var v = value.Trim().ToLowerInvariant();

            if (v.EndsWith("%", StringComparison.Ordinal) &&
                double.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                return Math.Clamp(percent / 100.0, 0.5, 5.0);

            if (v.EndsWith("pt", StringComparison.Ordinal) &&
                double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pt))
                return Math.Clamp(pt / 11.0, 0.5, 5.0);

            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare))
                return Math.Clamp(bare, 0.5, 5.0);

            return 0;
        }

        private static XElement? ParagraphBorders(IReadOnlyDictionary<string, string> style)
        {
            if (!style.TryGetValue("border", out var border)) return null;

            var lower = border.Trim().ToLowerInvariant();
            if (lower.Contains("none", StringComparison.Ordinal) ||
                lower.StartsWith("0", StringComparison.Ordinal)) return null;

            var colour = "808080";
            foreach (var token in lower.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (TryColour(token, out var parsed)) { colour = parsed; break; }

            return new XElement(W + "pBdr",
                DocxPackageParts.Border("top", 4, colour),
                DocxPackageParts.Border("left", 4, colour),
                DocxPackageParts.Border("bottom", 4, colour),
                DocxPackageParts.Border("right", 4, colour));
        }
    }

    /// <summary>Character formatting accumulated down the element chain.</summary>
    private struct RunFormat
    {
        public string? StyleId { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }

        /// <summary>The OOXML underline style: single, double, dotted, wave, none.</summary>
        public string? Underline { get; set; }

        public bool Strike { get; set; }
        public bool SmallCaps { get; set; }
        public bool AllCaps { get; set; }
        public string? VerticalAlign { get; set; }
        public string? Colour { get; set; }
        public string? Highlight { get; set; }
        public string? FontFamily { get; set; }
        public double FontSize { get; set; }

        public RunFormat With(string tag, IReadOnlyDictionary<string, string> style,
            IReadOnlyCollection<string> classes)
        {
            var next = this;

            switch (tag)
            {
                case "strong":
                case "b":
                case "th":
                    next.Bold = true;
                    break;
                case "em":
                case "i":
                    next.Italic = true;
                    break;
                case "u":
                    next.Underline = "single";
                    break;
                case "s":
                case "strike":
                    next.Strike = true;
                    break;
                case "sup":
                    next.VerticalAlign = "superscript";
                    break;
                case "sub":
                    next.VerticalAlign = "subscript";
                    break;
                case "code":
                case "pre":
                    next.FontFamily = "Consolas";
                    break;
            }

            if (style.TryGetValue("font-weight", out var weight))
            {
                var w = weight.Trim().ToLowerInvariant();
                next.Bold = w is "bold" or "bolder" ||
                            (int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
                             && numeric >= 600);
            }

            if (style.TryGetValue("font-style", out var fontStyle))
                next.Italic = fontStyle.Trim().Equals("italic", StringComparison.OrdinalIgnoreCase);

            if (style.TryGetValue("text-decoration", out var decoration) ||
                style.TryGetValue("text-decoration-line", out decoration))
            {
                var d = decoration.ToLowerInvariant();
                if (d.Contains("underline", StringComparison.Ordinal)) next.Underline = "single";
                if (d.Contains("line-through", StringComparison.Ordinal)) next.Strike = true;
                if (d.Contains("none", StringComparison.Ordinal)) { next.Underline = null; next.Strike = false; }
            }

            if (style.TryGetValue("font-variant", out var variant) &&
                variant.Contains("small-caps", StringComparison.OrdinalIgnoreCase))
                next.SmallCaps = true;

            if (style.TryGetValue("text-transform", out var transform) &&
                transform.Trim().Equals("uppercase", StringComparison.OrdinalIgnoreCase))
                next.AllCaps = true;

            if (style.TryGetValue("color", out var colour) && TryColour(colour, out var colourHex))
                next.Colour = colourHex;

            if (style.TryGetValue("background-color", out var background) && TryColour(background, out var backHex))
                next.Highlight = backHex;

            if (style.TryGetValue("font-family", out var family))
            {
                var first = family.Split(',')[0].Trim().Trim('\'', '"');

                // Belt and braces behind the sanitiser: a symbol font is never
                // written into the .docx. It is what turned a pasted Word bullet's
                // non-breaking spaces into empty boxes, and a body stored before
                // the sanitiser learned this rule must not still be able to do it.
                if (first.Length is > 0 and <= 64 && !IsSymbolFont(first)) next.FontFamily = first;
            }

            if (style.TryGetValue("font-size", out var size))
            {
                var pt = ParseFontSize(size);
                if (pt is > 3 and < 200) next.FontSize = pt;
            }

            if (style.TryGetValue("vertical-align", out var verticalAlign))
            {
                next.VerticalAlign = verticalAlign.Trim().ToLowerInvariant() switch
                {
                    "super" => "superscript",
                    "sub" => "subscript",
                    _ => next.VerticalAlign
                };
            }

            if (classes.Contains("dms-doc-smallcaps")) next.SmallCaps = true;

            return next;
        }
    }

    /// <summary>
    /// Fonts whose code points do not mean what they say. Kept as a local list
    /// rather than shared with the sanitiser so the writer has no dependency on
    /// it: the two enforce the same rule for the same reason, and either alone is
    /// enough to stop the defect.
    /// </summary>
    private static bool IsSymbolFont(string family) => family.ToLowerInvariant() switch
    {
        "symbol" or "wingdings" or "wingdings 2" or "wingdings 3" or "webdings"
            or "zapfdingbats" or "zapf dingbats" or "monotype sorts" or "marlett"
            or "bookshelf symbol 7" or "ms outlook" or "mt extra" => true,
        _ => false
    };
}
