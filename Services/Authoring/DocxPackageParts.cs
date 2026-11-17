//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Globalization;
using System.Xml.Linq;

namespace DMS.Web.Services.Authoring;

/// <summary>
/// The parts of a WordprocessingML package that are structure rather than
/// content: content types, relationships, the style sheet, the numbering
/// definitions, the document settings and the file properties.
///
/// <para><b>Why these live apart from the writer</b></para>
/// <see cref="HtmlDocxWriter"/> is about one question - how does this piece of
/// HTML become that piece of WordprocessingML. Everything here answers a
/// different one: what does a valid .docx have to contain regardless of what is
/// in it. Mixing the two produced a single file where the interesting mapping
/// logic was buried among four hundred lines of boilerplate XML, and the
/// boilerplate is where the defects that make Word say "unreadable content"
/// hide.
///
/// <para><b>Style names matter</b></para>
/// The <c>w:styleId</c> values below are the ones Word itself uses -
/// <c>Heading1</c>, <c>ListParagraph</c>, <c>Caption</c>, <c>Hyperlink</c>. That
/// is deliberate: a document opened in Word shows its styles in the styles
/// gallery under the names a user recognises, and applying "Heading 2" in Word
/// modifies the same style the application wrote. A private naming scheme would
/// produce a document that technically carries its formatting but that nobody
/// can restyle.
/// </summary>
internal static class DocxPackageParts
{
    internal const string WordMain = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal const string DrawingMain = "http://schemas.openxmlformats.org/drawingml/2006/main";
    internal const string DrawingWp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    internal const string DrawingPic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    internal static readonly XNamespace W = WordMain;
    internal static readonly XNamespace R = Relationships;
    internal static readonly XNamespace A = DrawingMain;
    internal static readonly XNamespace Wp = DrawingWp;
    internal static readonly XNamespace Pic = DrawingPic;

    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n";

    internal static string Serialize(XElement element)
        => Declaration + element.ToString(SaveOptions.DisableFormatting);

    private static string Twips(int value) => value.ToString(CultureInfo.InvariantCulture);

    // -------------------------------------------------------------------------
    // Style sets
    // -------------------------------------------------------------------------

    /// <summary>
    /// A named pairing of body font, heading font and heading colour.
    ///
    /// <para>
    /// Offered as a small closed set rather than as free choice of every font on
    /// the server, for a reason specific to controlled documents: a font that is
    /// not installed on the machine that opens the file is silently substituted,
    /// and a substituted font reflows the text. A procedure whose page breaks
    /// move depending on who opens it is not a controlled procedure. Every font
    /// named here ships with Windows, macOS and every mainstream Linux
    /// distribution's core font packages.
    /// </para>
    /// </summary>
    internal sealed record StyleSet(string BodyFont, string HeadingFont, string HeadingColour, int BodyHalfPoints)
    {
        internal static StyleSet For(string? name) => (name ?? string.Empty).ToUpperInvariant() switch
        {
            "CLASSIC" => new StyleSet("Cambria", "Cambria", "1F3864", 22),
            "COMPACT" => new StyleSet("Arial", "Arial", "1F3864", 20),
            "PLAIN" => new StyleSet("Calibri", "Calibri", "000000", 22),
            _ => new StyleSet("Calibri", "Calibri", "1F3864", 22)
        };
    }

    // -------------------------------------------------------------------------
    // [Content_Types].xml
    // -------------------------------------------------------------------------

    /// <summary>
    /// One header or footer part in the package.
    ///
    /// <para>
    /// Word does not have "a header": it has up to three per section - the
    /// default one, the first page's, and the even (left-hand) pages' - each a
    /// separate part with its own relationship and its own
    /// <c>headerReference</c> naming which of the three it is. Modelling that
    /// as a list rather than as a pair of booleans is what lets "different
    /// first page" and "different odd &amp; even pages" both work, and work
    /// together, without the package assembly growing a special case per
    /// combination.
    /// </para>
    /// </summary>
    /// <param name="Kind">"hdr" or "ftr" - the part's root element.</param>
    /// <param name="Variant">"default", "first" or "even", as w:type in the reference.</param>
    /// <param name="FileName">The part name inside word/, e.g. "header3.xml".</param>
    /// <param name="RelationshipId">Its id in word/_rels/document.xml.rels.</param>
    internal sealed record RunningPart(string Kind, string Variant, string FileName, string RelationshipId)
    {
        public bool IsHeader => Kind == "hdr";
        public string ReferenceElement => IsHeader ? "headerReference" : "footerReference";
    }

    internal static string ContentTypes(IReadOnlyCollection<string> imageExtensions,
        IReadOnlyCollection<RunningPart> running)
    {
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";

        var root = new XElement(ct + "Types",
            Default(ct, "rels", "application/vnd.openxmlformats-package.relationships+xml"),
            Default(ct, "xml", "application/xml"));

        foreach (var extension in imageExtensions.Distinct(StringComparer.OrdinalIgnoreCase))
            root.Add(Default(ct, extension, "image/" + extension));

        void Override(string part, string contentType) =>
            root.Add(new XElement(ct + "Override",
                new XAttribute("PartName", part),
                new XAttribute("ContentType", contentType)));

        const string prefix = "application/vnd.openxmlformats-officedocument.wordprocessingml.";

        Override("/word/document.xml", prefix + "document.main+xml");
        Override("/word/styles.xml", prefix + "styles+xml");
        Override("/word/numbering.xml", prefix + "numbering+xml");
        Override("/word/settings.xml", prefix + "settings+xml");

        foreach (var part in running)
            Override("/word/" + part.FileName, prefix + (part.IsHeader ? "header+xml" : "footer+xml"));

        Override("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        Override("/docProps/app.xml",
            "application/vnd.openxmlformats-officedocument.extended-properties+xml");

        return Serialize(root);
    }

    private static XElement Default(XNamespace ct, string extension, string contentType)
        => new(ct + "Default",
            new XAttribute("Extension", extension),
            new XAttribute("ContentType", contentType));

    // -------------------------------------------------------------------------
    // Relationships
    // -------------------------------------------------------------------------

    internal static string PackageRels()
    {
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";

        return Serialize(new XElement(pr + "Relationships",
            Rel(pr, "rId1", Relationships + "/officeDocument", "word/document.xml"),
            Rel(pr, "rId2",
                "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties",
                "docProps/core.xml"),
            Rel(pr, "rId3", Relationships + "/extended-properties", "docProps/app.xml")));
    }

    /// <summary>
    /// Document relationships: the fixed parts, then one per header/footer, then
    /// one per image, then one per external hyperlink.
    /// <para>
    /// A hyperlink relationship carries <c>TargetMode="External"</c>. Omitting it
    /// makes Word treat the URL as a path inside the package and refuse to open
    /// the file - a mistake that produces a corrupt-document dialog rather than a
    /// broken link, so it is worth naming here.
    /// </para>
    /// </summary>
    internal static string DocumentRels(IReadOnlyCollection<(string Id, string FileName)> images,
        IReadOnlyCollection<(string Id, string Url)> hyperlinks,
        IReadOnlyCollection<RunningPart> running)
    {
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";

        var root = new XElement(pr + "Relationships",
            Rel(pr, "rIdStyles", Relationships + "/styles", "styles.xml"),
            Rel(pr, "rIdNumbering", Relationships + "/numbering", "numbering.xml"),
            Rel(pr, "rIdSettings", Relationships + "/settings", "settings.xml"));

        foreach (var part in running)
        {
            root.Add(Rel(pr, part.RelationshipId,
                Relationships + (part.IsHeader ? "/header" : "/footer"), part.FileName));
        }

        foreach (var (id, fileName) in images)
            root.Add(Rel(pr, id, Relationships + "/image", "media/" + fileName));

        foreach (var (id, url) in hyperlinks)
        {
            var rel = Rel(pr, id, Relationships + "/hyperlink", url);
            rel.Add(new XAttribute("TargetMode", "External"));
            root.Add(rel);
        }

        return Serialize(root);
    }

    /// <summary>
    /// Relationships for a header or footer part.
    ///
    /// <para><b>Why this part has to exist at all</b></para>
    /// Relationship ids are scoped to the part that uses them. A picture in a
    /// running header - a site logo, which is what a header picture almost
    /// always is - is referenced from <c>header1.xml</c>, so the id has to be
    /// declared in <c>_rels/header1.xml.rels</c>. Declaring it only in the
    /// document's own rels leaves a dangling reference, and Word reports that as
    /// unreadable content rather than as a missing image.
    /// </para>
    /// <para>
    /// Every image is declared in every part that might use one. An unused
    /// relationship is permitted by the packaging specification and costs a line
    /// of XML; working out exactly which picture appears in which part, and
    /// getting it wrong, costs a corrupt document.
    /// </para>
    /// </summary>
    internal static string PartRels(IReadOnlyCollection<(string Id, string FileName)> images,
        IReadOnlyCollection<(string Id, string Url)> hyperlinks)
    {
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        var root = new XElement(pr + "Relationships");

        foreach (var (id, fileName) in images)
            root.Add(Rel(pr, id, Relationships + "/image", "media/" + fileName));

        foreach (var (id, url) in hyperlinks)
        {
            var rel = Rel(pr, id, Relationships + "/hyperlink", url);
            rel.Add(new XAttribute("TargetMode", "External"));
            root.Add(rel);
        }

        return Serialize(root);
    }

    private static XElement Rel(XNamespace pr, string id, string type, string target)
        => new(pr + "Relationship",
            new XAttribute("Id", id),
            new XAttribute("Type", type),
            new XAttribute("Target", target));

    // -------------------------------------------------------------------------
    // settings.xml
    // -------------------------------------------------------------------------

    /// <summary>
    /// <c>w:updateFields</c> is the reason this part exists.
    ///
    /// <para>
    /// A table of contents and a "Page 3 of 11" footer are FIELDS: the file
    /// stores the instruction and a cached result, and something has to compute
    /// the real value. This application does not paginate the .docx - Word does,
    /// when it opens it. Setting updateFields tells Word to recalculate on open,
    /// so the reader sees the right page count and a populated contents page
    /// instead of the placeholder text the file was written with.
    /// </para>
    /// </summary>
    internal static string Settings(bool updateFields, bool evenAndOddHeaders = false)
    {
        var root = new XElement(W + "settings",
            new XAttribute(XNamespace.Xmlns + "w", WordMain),
            new XElement(W + "zoom", new XAttribute(W + "percent", "100")),
            new XElement(W + "defaultTabStop", new XAttribute(W + "val", "720")),
            new XElement(W + "characterSpacingControl", new XAttribute(W + "val", "doNotCompress")));

        // A document-wide switch, not a section one: Word honours the even-page
        // header reference only when this is present.
        if (evenAndOddHeaders) root.Add(new XElement(W + "evenAndOddHeaders"));

        if (updateFields)
            root.Add(new XElement(W + "updateFields", new XAttribute(W + "val", "true")));

        return Serialize(root);
    }

    // -------------------------------------------------------------------------
    // styles.xml
    // -------------------------------------------------------------------------

    internal static string Styles(StyleSet set)
    {
        var root = new XElement(W + "styles",
            new XAttribute(XNamespace.Xmlns + "w", WordMain),
            DocDefaults(set));

        root.Add(ParagraphStyle("Normal", "Normal", isDefault: true, basedOn: null, next: null, qFormat: true));

        root.Add(NoSpacing());
        root.Add(Title(set));
        root.Add(Subtitle(set));

        for (var level = 1; level <= 6; level++) root.Add(Heading(level, set));

        root.Add(ListParagraph());
        root.Add(Quote(set));
        root.Add(Caption());

        root.Add(TocHeading(set));
        for (var level = 1; level <= 4; level++) root.Add(TocEntry(level));

        root.Add(Preformatted());
        root.Add(HeaderFooterStyle("Header", "header"));
        root.Add(HeaderFooterStyle("Footer", "footer"));
        root.Add(HyperlinkStyle());
        root.Add(TableGrid());

        return Serialize(root);
    }

    private static XElement DocDefaults(StyleSet set) => new(W + "docDefaults",
        new XElement(W + "rPrDefault",
            new XElement(W + "rPr",
                Fonts(set.BodyFont),
                new XElement(W + "sz", new XAttribute(W + "val", Twips(set.BodyHalfPoints))),
                new XElement(W + "szCs", new XAttribute(W + "val", Twips(set.BodyHalfPoints))),
                new XElement(W + "lang", new XAttribute(W + "val", "en-GB")))),
        new XElement(W + "pPrDefault",
            new XElement(W + "pPr",
                new XElement(W + "spacing",
                    new XAttribute(W + "after", "120"),
                    new XAttribute(W + "line", "259"),
                    new XAttribute(W + "lineRule", "auto")))));

    private static XElement Fonts(string family) => new(W + "rFonts",
        new XAttribute(W + "ascii", family),
        new XAttribute(W + "hAnsi", family),
        new XAttribute(W + "cs", family));

    private static XElement ParagraphStyle(string id, string name, bool isDefault, string? basedOn,
        string? next, bool qFormat)
    {
        var style = new XElement(W + "style",
            new XAttribute(W + "type", "paragraph"),
            new XAttribute(W + "styleId", id),
            new XElement(W + "name", new XAttribute(W + "val", name)));

        if (isDefault) style.SetAttributeValue(W + "default", "1");
        if (basedOn is not null) style.Add(new XElement(W + "basedOn", new XAttribute(W + "val", basedOn)));
        if (next is not null) style.Add(new XElement(W + "next", new XAttribute(W + "val", next)));
        if (qFormat) style.Add(new XElement(W + "qFormat"));

        return style;
    }

    private static XElement Heading(int level, StyleSet set)
    {
        // Word's own heading sizes, so a document opened alongside a Word-authored
        // one looks like the same family of documents.
        var halfPoints = level switch { 1 => 32, 2 => 28, 3 => 26, 4 => 24, _ => 22 };
        var colour = level <= 2 ? set.HeadingColour : "2E4E7E";

        var style = ParagraphStyle("Heading" + level, "heading " + level, false, "Normal", "Normal", true);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "keepNext"),
            new XElement(W + "keepLines"),
            new XElement(W + "spacing",
                new XAttribute(W + "before", level == 1 ? "360" : "280"),
                new XAttribute(W + "after", "120")),
            new XElement(W + "outlineLvl", new XAttribute(W + "val", Twips(level - 1)))));

        style.Add(new XElement(W + "rPr",
            Fonts(set.HeadingFont),
            new XElement(W + "b"),
            new XElement(W + "color", new XAttribute(W + "val", colour)),
            new XElement(W + "sz", new XAttribute(W + "val", Twips(halfPoints))),
            new XElement(W + "szCs", new XAttribute(W + "val", Twips(halfPoints)))));

        return style;
    }

    private static XElement Title(StyleSet set)
    {
        var style = ParagraphStyle("Title", "Title", false, "Normal", "Normal", true);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "spacing", new XAttribute(W + "after", "80")),
            new XElement(W + "contextualSpacing")));

        style.Add(new XElement(W + "rPr",
            Fonts(set.HeadingFont),
            new XElement(W + "b"),
            new XElement(W + "color", new XAttribute(W + "val", set.HeadingColour)),
            new XElement(W + "sz", new XAttribute(W + "val", "56")),
            new XElement(W + "szCs", new XAttribute(W + "val", "56"))));

        return style;
    }

    private static XElement Subtitle(StyleSet set)
    {
        var style = ParagraphStyle("Subtitle", "Subtitle", false, "Normal", "Normal", true);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "spacing", new XAttribute(W + "after", "320")),
            new XElement(W + "contextualSpacing")));

        style.Add(new XElement(W + "rPr",
            Fonts(set.BodyFont),
            new XElement(W + "color", new XAttribute(W + "val", "595959")),
            new XElement(W + "sz", new XAttribute(W + "val", "28")),
            new XElement(W + "szCs", new XAttribute(W + "val", "28"))));

        return style;
    }

    private static XElement NoSpacing()
    {
        var style = ParagraphStyle("NoSpacing", "No Spacing", false, "Normal", "NoSpacing", true);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "spacing",
                new XAttribute(W + "after", "0"),
                new XAttribute(W + "line", "240"),
                new XAttribute(W + "lineRule", "auto"))));

        return style;
    }

    private static XElement ListParagraph()
    {
        var style = ParagraphStyle("ListParagraph", "List Paragraph", false, "Normal", null, true);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "ind", new XAttribute(W + "left", "720")),
            new XElement(W + "contextualSpacing")));

        return style;
    }

    private static XElement Quote(StyleSet set)
    {
        var style = ParagraphStyle("Quote", "Quote", false, "Normal", "Normal", true);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "spacing",
                new XAttribute(W + "before", "200"),
                new XAttribute(W + "after", "200")),
            new XElement(W + "ind",
                new XAttribute(W + "left", "720"),
                new XAttribute(W + "right", "720")),
            new XElement(W + "pBdr",
                new XElement(W + "left",
                    new XAttribute(W + "val", "single"),
                    new XAttribute(W + "sz", "18"),
                    new XAttribute(W + "space", "12"),
                    new XAttribute(W + "color", set.HeadingColour)))));

        style.Add(new XElement(W + "rPr",
            new XElement(W + "i"),
            new XElement(W + "color", new XAttribute(W + "val", "404040"))));

        return style;
    }

    private static XElement Caption()
    {
        var style = ParagraphStyle("Caption", "caption", false, "Normal", "Normal", true);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "keepNext"),
            new XElement(W + "spacing",
                new XAttribute(W + "before", "0"),
                new XAttribute(W + "after", "200"))));

        style.Add(new XElement(W + "rPr",
            new XElement(W + "b"),
            new XElement(W + "color", new XAttribute(W + "val", "1F3864")),
            new XElement(W + "sz", new XAttribute(W + "val", "18")),
            new XElement(W + "szCs", new XAttribute(W + "val", "18"))));

        return style;
    }

    /// <summary>
    /// The heading above a generated index. Word's own name for this style is
    /// "TOC Heading", and using the same id means an author who later restyles
    /// the contents page in Word restyles it the way Word expects to.
    /// </summary>
    private static XElement TocHeading(StyleSet set)
    {
        var style = ParagraphStyle("TOCHeading", "TOC Heading", false, "Heading1", "Normal", true);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "keepNext"),
            new XElement(W + "spacing",
                new XAttribute(W + "before", "240"),
                new XAttribute(W + "after", "120"))));

        style.Add(new XElement(W + "rPr",
            new XElement(W + "b"),
            new XElement(W + "color", new XAttribute(W + "val", set.HeadingColour)),
            new XElement(W + "sz", new XAttribute(W + "val", "28")),
            new XElement(W + "szCs", new XAttribute(W + "val", "28"))));

        return style;
    }

    /// <summary>
    /// One indent level of index entry. The indents are the ones Word uses
    /// (220 twips per level), so a refreshed index in Word lands in the same
    /// place as the one exported from here rather than shifting on first update.
    /// </summary>
    private static XElement TocEntry(int level)
    {
        var style = ParagraphStyle("TOC" + level, "toc " + level, false, "Normal", "Normal", false);

        var properties = new XElement(W + "pPr",
            new XElement(W + "spacing",
                new XAttribute(W + "before", "0"),
                new XAttribute(W + "after", "0")));

        if (level > 1)
        {
            properties.Add(new XElement(W + "ind",
                new XAttribute(W + "left", Twips((level - 1) * 220))));
        }

        style.Add(properties);
        return style;
    }

    private static XElement Preformatted()
    {
        var style = ParagraphStyle("DmsPreformatted", "DMS Preformatted", false, "Normal", "Normal", false);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "spacing",
                new XAttribute(W + "after", "0"),
                new XAttribute(W + "line", "240"),
                new XAttribute(W + "lineRule", "auto")),
            new XElement(W + "shd",
                new XAttribute(W + "val", "clear"),
                new XAttribute(W + "color", "auto"),
                new XAttribute(W + "fill", "F4F6FA"))));

        style.Add(new XElement(W + "rPr",
            Fonts("Consolas"),
            new XElement(W + "sz", new XAttribute(W + "val", "20"))));

        return style;
    }

    /// <summary>
    /// Header and footer paragraphs carry the centre and right tab stops Word
    /// puts there, so a three-part header ("number | title | revision") lines up
    /// when someone edits it in Word afterwards.
    /// </summary>
    private static XElement HeaderFooterStyle(string id, string name)
    {
        var style = ParagraphStyle(id, name, false, "Normal", null, false);

        style.Add(new XElement(W + "pPr",
            new XElement(W + "tabs",
                new XElement(W + "tab",
                    new XAttribute(W + "val", "center"),
                    new XAttribute(W + "pos", "4513")),
                new XElement(W + "tab",
                    new XAttribute(W + "val", "right"),
                    new XAttribute(W + "pos", "9026"))),
            new XElement(W + "spacing",
                new XAttribute(W + "after", "0"),
                new XAttribute(W + "line", "240"),
                new XAttribute(W + "lineRule", "auto"))));

        style.Add(new XElement(W + "rPr",
            new XElement(W + "sz", new XAttribute(W + "val", "18")),
            new XElement(W + "szCs", new XAttribute(W + "val", "18"))));

        return style;
    }

    private static XElement HyperlinkStyle() => new(W + "style",
        new XAttribute(W + "type", "character"),
        new XAttribute(W + "styleId", "Hyperlink"),
        new XElement(W + "name", new XAttribute(W + "val", "Hyperlink")),
        new XElement(W + "uiPriority", new XAttribute(W + "val", "99")),
        new XElement(W + "unhideWhenUsed"),
        new XElement(W + "rPr",
            new XElement(W + "color", new XAttribute(W + "val", "0563C1")),
            new XElement(W + "u", new XAttribute(W + "val", "single"))));

    private static XElement TableGrid() => new(W + "style",
        new XAttribute(W + "type", "table"),
        new XAttribute(W + "styleId", "TableGrid"),
        new XElement(W + "name", new XAttribute(W + "val", "Table Grid")),
        new XElement(W + "basedOn", new XAttribute(W + "val", "TableNormal")),
        new XElement(W + "uiPriority", new XAttribute(W + "val", "39")),
        new XElement(W + "tblPr",
            new XElement(W + "tblBorders",
                Border("top"), Border("left"), Border("bottom"), Border("right"),
                Border("insideH"), Border("insideV")),
            new XElement(W + "tblCellMar",
                CellMargin("top", 72), CellMargin("left", 108),
                CellMargin("bottom", 72), CellMargin("right", 108))));

    internal static XElement Border(string side, int size = 4, string colour = "808080")
        => new(W + side,
            new XAttribute(W + "val", "single"),
            new XAttribute(W + "sz", Twips(size)),
            new XAttribute(W + "space", "0"),
            new XAttribute(W + "color", colour));

    private static XElement CellMargin(string side, int width) => new(W + side,
        new XAttribute(W + "w", Twips(width)),
        new XAttribute(W + "type", "dxa"));

    // -------------------------------------------------------------------------
    // numbering.xml
    // -------------------------------------------------------------------------

    /// <summary>
    /// Three list definitions: bullets (numId 1), simple numbers (numId 2) and
    /// legal multilevel numbering - 1, 1.1, 1.1.1 (numId 3).
    ///
    /// <para>
    /// The third exists because that is how pharmaceutical procedures are
    /// written. An SOP whose clauses are numbered 4.2.1 is referenced that way in
    /// deviations, change controls and training records, and an editor that can
    /// only produce flat bullets cannot express the documents this system is for.
    /// </para>
    /// </summary>
    internal static string Numbering()
    {
        var root = new XElement(W + "numbering", new XAttribute(XNamespace.Xmlns + "w", WordMain));

        root.Add(BulletDefinition());
        root.Add(DecimalDefinition());
        root.Add(LegalDefinition());

        root.Add(Num(1, 0));
        root.Add(Num(2, 1));
        root.Add(Num(3, 2));

        return Serialize(root);
    }

    private static XElement Num(int numId, int abstractId) => new(W + "num",
        new XAttribute(W + "numId", Twips(numId)),
        new XElement(W + "abstractNumId", new XAttribute(W + "val", Twips(abstractId))));

    private static XElement BulletDefinition()
    {
        var abstractNum = new XElement(W + "abstractNum",
            new XAttribute(W + "abstractNumId", "0"),
            new XElement(W + "multiLevelType", new XAttribute(W + "val", "hybridMultilevel")));

        // The glyphs Word itself uses at each depth, written as escapes: they live
        // in the private-use area of Symbol and Wingdings, where a source file
        // passing through the wrong encoding would silently corrupt them.
        var glyphs = new[] { "\uF0B7", "o", "\uF0A7" };
        var fonts = new[] { "Symbol", "Courier New", "Wingdings" };

        for (var level = 0; level < 9; level++)
            abstractNum.Add(Level(level, "bullet", glyphs[level % 3], fonts[level % 3]));

        return abstractNum;
    }

    private static XElement DecimalDefinition()
    {
        var abstractNum = new XElement(W + "abstractNum",
            new XAttribute(W + "abstractNumId", "1"),
            new XElement(W + "multiLevelType", new XAttribute(W + "val", "hybridMultilevel")));

        var formats = new[] { "decimal", "lowerLetter", "lowerRoman" };

        for (var level = 0; level < 9; level++)
            abstractNum.Add(Level(level, formats[level % 3], $"%{level + 1}.", string.Empty));

        return abstractNum;
    }

    /// <summary>
    /// 1. / 1.1 / 1.1.1 - each level's text includes every level above it, which
    /// is what <c>%1.%2</c> means in <c>w:lvlText</c>.
    /// </summary>
    private static XElement LegalDefinition()
    {
        var abstractNum = new XElement(W + "abstractNum",
            new XAttribute(W + "abstractNumId", "2"),
            new XElement(W + "multiLevelType", new XAttribute(W + "val", "multilevel")));

        for (var level = 0; level < 9; level++)
        {
            var text = string.Join('.', Enumerable.Range(1, level + 1).Select(n => $"%{n}"));
            abstractNum.Add(Level(level, "decimal", text, string.Empty, indentStep: 432, hanging: 432));
        }

        return abstractNum;
    }

    private static XElement Level(int level, string format, string text, string font,
        int indentStep = 720, int hanging = 360)
    {
        var lvl = new XElement(W + "lvl",
            new XAttribute(W + "ilvl", Twips(level)),
            new XElement(W + "start", new XAttribute(W + "val", "1")),
            new XElement(W + "numFmt", new XAttribute(W + "val", format)),
            new XElement(W + "lvlText", new XAttribute(W + "val", text)),
            new XElement(W + "lvlJc", new XAttribute(W + "val", "left")),
            new XElement(W + "pPr",
                new XElement(W + "ind",
                    new XAttribute(W + "left", Twips(indentStep * (level + 1))),
                    new XAttribute(W + "hanging", Twips(hanging)))));

        if (font.Length > 0)
            lvl.Add(new XElement(W + "rPr",
                new XElement(W + "rFonts",
                    new XAttribute(W + "ascii", font),
                    new XAttribute(W + "hAnsi", font),
                    new XAttribute(W + "hint", "default"))));

        return lvl;
    }

    // -------------------------------------------------------------------------
    // Section properties, headers and footers
    // -------------------------------------------------------------------------

    internal static XElement SectionProperties(DocumentPageSetupDto page,
        IReadOnlyCollection<RunningPart> running)
    {
        var sectPr = new XElement(W + "sectPr");

        if (page.DifferentFirstPage) sectPr.Add(new XElement(W + "titlePg"));

        // Header and footer references come before pgSz in the schema order, and
        // Word is strict about it. Headers before footers, and within each the
        // order Word writes: default, first, even.
        foreach (var part in running.Where(p => p.IsHeader).OrderBy(VariantOrder))
            sectPr.Add(HeaderFooterReference(part));

        foreach (var part in running.Where(p => !p.IsHeader).OrderBy(VariantOrder))
            sectPr.Add(HeaderFooterReference(part));

        var size = new XElement(W + "pgSz",
            new XAttribute(W + "w", Twips(page.PageWidthTwips)),
            new XAttribute(W + "h", Twips(page.PageHeightTwips)));

        if (page.IsLandscape) size.Add(new XAttribute(W + "orient", "landscape"));
        sectPr.Add(size);

        sectPr.Add(new XElement(W + "pgMar",
            new XAttribute(W + "top", Twips(page.MarginTopTwips)),
            new XAttribute(W + "right", Twips(page.MarginRightTwips)),
            new XAttribute(W + "bottom", Twips(page.MarginBottomTwips)),
            new XAttribute(W + "left", Twips(page.MarginLeftTwips)),
            new XAttribute(W + "header", Twips(page.HeaderDistanceTwips)),
            new XAttribute(W + "footer", Twips(page.FooterDistanceTwips)),
            new XAttribute(W + "gutter", Twips(page.GutterTwips))));

        if (page.Columns > 1)
        {
            sectPr.Add(new XElement(W + "cols",
                new XAttribute(W + "num", Twips(page.Columns)),
                new XAttribute(W + "space", "425")));
        }
        else
        {
            sectPr.Add(new XElement(W + "cols", new XAttribute(W + "space", "425")));
        }

        sectPr.Add(new XElement(W + "docGrid", new XAttribute(W + "linePitch", "360")));
        return sectPr;
    }

    private static int VariantOrder(RunningPart part) => part.Variant switch
    {
        "default" => 0, "first" => 1, _ => 2
    };

    private static XElement HeaderFooterReference(RunningPart part)
        => new(W + part.ReferenceElement,
            new XAttribute(W + "type", part.Variant),
            new XAttribute(R + "id", part.RelationshipId));

    internal static string HeaderFooterPart(string rootName, IEnumerable<XElement> blocks)
    {
        var root = new XElement(W + rootName,
            new XAttribute(XNamespace.Xmlns + "w", WordMain),
            new XAttribute(XNamespace.Xmlns + "r", Relationships));

        var any = false;
        foreach (var block in blocks) { root.Add(block); any = true; }

        // A header part with no paragraph is invalid.
        if (!any) root.Add(new XElement(W + "p"));

        return Serialize(root);
    }

    // -------------------------------------------------------------------------
    // docProps
    // -------------------------------------------------------------------------

    internal static string CoreProperties(DocxWriteMetadata metadata)
    {
        XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace dcterms = "http://purl.org/dc/terms/";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var timestamp = metadata.CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var title = string.IsNullOrWhiteSpace(metadata.DocumentNumber)
            ? metadata.Title
            : $"{metadata.DocumentNumber} - {metadata.Title}";

        return Serialize(new XElement(cp + "coreProperties",
            new XAttribute(XNamespace.Xmlns + "cp", cp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc", dc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dcterms", dcterms.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
            new XElement(dc + "title", title),
            new XElement(dc + "subject", metadata.DocumentNumber),
            new XElement(dc + "creator", metadata.Author),
            new XElement(cp + "lastModifiedBy", metadata.Author),
            new XElement(cp + "revision", string.IsNullOrWhiteSpace(metadata.Revision) ? "1" : metadata.Revision),
            new XElement(dcterms + "created", new XAttribute(xsi + "type", "dcterms:W3CDTF"), timestamp),
            new XElement(dcterms + "modified", new XAttribute(xsi + "type", "dcterms:W3CDTF"), timestamp)));
    }

    internal static string AppProperties()
    {
        XNamespace ep = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

        return Serialize(new XElement(ep + "Properties",
            new XAttribute(XNamespace.Xmlns + "ep", ep.NamespaceName),
            new XElement(ep + "Application", "Blue Cross Laboratories DMS"),
            new XElement(ep + "Company", "Blue Cross Laboratories"),
            new XElement(ep + "AppVersion", "1.0000")));
    }
}
