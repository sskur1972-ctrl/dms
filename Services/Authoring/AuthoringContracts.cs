//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* In-application authoring - contracts and data shapes, ported unchanged from Bcl.Dms
 * (Application/DTOs/ServiceDtos.cs, Interfaces/Infrastructure/IInfrastructureServices.cs).
 */
namespace DMS.Web.Services.Authoring;


/// <summary>
/// A read-only view of a document's content, produced for the protected viewer
/// (URS-7.16). It is deliberately NOT a file: the caller receives markup to
/// display, never bytes to save.
/// </summary>
public sealed class DocumentPreviewDto
{
    /// <summary>HTML | PDF | NONE.</summary>
    public string Kind { get; set; } = "NONE";

    /// <summary>
    /// Sanitised markup, when <see cref="Kind"/> is HTML. Built by the renderer
    /// from escaped text and a fixed tag whitelist, so it never carries markup
    /// that came out of the source document.
    /// </summary>
    public string? Html { get; set; }

    /// <summary>Storage path of the PDF to stream, when <see cref="Kind"/> is PDF.</summary>
    public string? PdfRelativePath { get; set; }

    /// <summary>
    /// True when the rendering is text-only and drops the source document's
    /// layout. Shown to the reader, because a reviewer must know they are not
    /// looking at the paginated document.
    /// </summary>
    public bool IsTextOnly { get; set; }

    /// <summary>
    /// True when what is being shown is the watermarked PDF rendition - the
    /// controlled representation, carrying the state watermark (URS-7.20) and the
    /// signature manifestations (URS-7.9).
    /// <para>
    /// False when it is the uploaded PDF shown directly, or a read-only rendering.
    /// Those are full-fidelity and text-faithful respectively, but neither is the
    /// controlled copy, and the viewer says so rather than letting a reader assume
    /// the watermark is simply absent.
    /// </para>
    /// </summary>
    public bool IsControlledRendition { get; set; }

    /// <summary>True when figures from the document were inlined into the rendering.</summary>
    public bool HasImages { get; set; }

    /// <summary>Why no content could be shown, when Kind is NONE.</summary>
    public string? UnavailableReason { get; set; }

    public int PageCount { get; set; }
    public string? SourceFormat { get; set; }
}


/// <summary>
/// Ceilings applied to a body posted by the editor. They come from
/// SYSTEM_CONFIG (authoring.*), never from constants, so QA can change what the
/// system will accept without a deployment.
/// </summary>
public sealed class EditorSanitizeLimits
{
    public long MaxContentBytes { get; set; } = 4L * 1024 * 1024;
    public long MaxImageBytes { get; set; } = 2L * 1024 * 1024;
    public long MaxTotalImageBytes { get; set; } = 12L * 1024 * 1024;

    public static EditorSanitizeLimits Default => new();
}


/// <summary>
/// The outcome of sanitising one body. It reports what was removed as well as
/// what survived: an author who pastes from a web page and silently loses their
/// figures should be told, not left to discover it at review.
/// </summary>
public sealed class EditorHtmlSanitizeResult
{
    public string Html { get; set; } = string.Empty;

    /// <summary>True when the body was refused outright; Html is then meaningless.</summary>
    public bool Rejected { get; set; }

    public string? RejectionReason { get; set; }

    /// <summary>Elements dropped or unwrapped because they are not permitted in a document body.</summary>
    public int RemovedElements { get; set; }

    /// <summary>Images referenced by URL rather than embedded, which are never fetched.</summary>
    public int RemovedExternalImages { get; set; }

    /// <summary>Images refused for exceeding authoring.maxImageBytes.</summary>
    public int OversizedImages { get; set; }

    /// <summary>Running total of decoded image bytes, kept by the decoder itself.</summary>
    public long ImageBytesSeen { get; set; }

    public long TotalImageBytes { get; set; }

    /// <summary>A short, plain sentence for the author, or null when nothing was changed.</summary>
    public string? ChangeNotice
    {
        get
        {
            var parts = new List<string>();
            if (RemovedElements > 0) parts.Add($"{RemovedElements} unsupported element(s) were removed");
            if (RemovedExternalImages > 0)
                parts.Add($"{RemovedExternalImages} image(s) linked to an external address were removed - " +
                          "paste or insert the image itself so it is stored with the document");
            if (OversizedImages > 0) parts.Add($"{OversizedImages} image(s) were too large and were removed");

            return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
        }
    }
}


/// <summary>
/// The page geometry of an authored document: what the editor draws and what
/// the .docx writer puts in <c>w:sectPr</c>.
///
/// <para><b>Everything is in twips</b></para>
/// A twip is one twentieth of a point, so an inch is 1440 and a millimetre is
/// about 56.7. OOXML measures page geometry in twips, so storing anything else
/// would mean converting at the boundary and rounding twice. The editor converts
/// to CSS pixels for display; the file gets the exact number the author chose.
///
/// <para><b>One shape, three places</b></para>
/// The same object is stored on the document, on the template it came from, and
/// as the site default in SYSTEM_CONFIG. A single schema is what lets a document
/// inherit its template's page setup without a translation step where the two
/// could drift.
/// </para>
/// </summary>
public sealed class DocumentPageSetupDto
{
    /// <summary>A4 | A3 | LETTER | LEGAL. Anything else falls back to A4.</summary>
    public string PageSize { get; set; } = "A4";

    /// <summary>portrait | landscape.</summary>
    public string Orientation { get; set; } = "portrait";

    public int MarginTopTwips { get; set; } = 1440;
    public int MarginRightTwips { get; set; } = 1440;
    public int MarginBottomTwips { get; set; } = 1440;
    public int MarginLeftTwips { get; set; } = 1440;

    /// <summary>How far the header sits from the top edge of the paper.</summary>
    public int HeaderDistanceTwips { get; set; } = 708;

    public int FooterDistanceTwips { get; set; } = 708;

    /// <summary>Binding allowance added to the inside margin. 0 for most documents.</summary>
    public int GutterTwips { get; set; }

    /// <summary>1, 2 or 3. More than three is not useful on A4 and is not offered.</summary>
    public int Columns { get; set; } = 1;

    /// <summary>Sanitised HTML for the running header, or null for none.</summary>
    public string? HeaderHtml { get; set; }

    public string? FooterHtml { get; set; }

    /// <summary>
    /// A title page usually carries no running header. When true the first page
    /// gets its own header and footer, exactly as Word's "Different first page"
    /// does - <see cref="FirstHeaderHtml"/> and <see cref="FirstFooterHtml"/>,
    /// which are normally empty and may be given their own content.
    /// </summary>
    public bool DifferentFirstPage { get; set; }

    /// <summary>The first page's own header when <see cref="DifferentFirstPage"/> is set.</summary>
    public string? FirstHeaderHtml { get; set; }

    public string? FirstFooterHtml { get; set; }

    /// <summary>
    /// Word's "Different odd &amp; even pages". A bound document reads as a
    /// spread, so the document number goes on the outside edge of each leaf -
    /// which means the even (left-hand) pages need their own header and footer.
    /// </summary>
    public bool DifferentOddEven { get; set; }

    /// <summary>The even (left-hand) pages' header when <see cref="DifferentOddEven"/> is set.</summary>
    public string? EvenHeaderHtml { get; set; }

    public string? EvenFooterHtml { get; set; }

    public bool ShowPageNumbers { get; set; } = true;

    /// <summary>PAGE_X_OF_Y | PAGE_X | PLAIN_X.</summary>
    public string PageNumberFormat { get; set; } = "PAGE_X_OF_Y";

    /// <summary>FOOTER_CENTER | FOOTER_RIGHT | FOOTER_LEFT | HEADER_RIGHT.</summary>
    public string PageNumberPosition { get; set; } = "FOOTER_CENTER";

    /// <summary>
    /// Body font and heading colour, as a named set. Kept as a name rather than
    /// as loose values so that a site's documentation standard is one choice in
    /// one place, and so a document can be re-themed without touching its text.
    /// </summary>
    public string StyleSet { get; set; } = "DEFAULT";

    /// <summary>
    /// Whether headings carry automatic section numbers (1, 1.1, 1.1.1).
    ///
    /// <para>
    /// Stored with the document rather than inferred from its text, because the
    /// numbers themselves are written into the headings: an author who turns the
    /// feature off must get the numbers removed, and that is only possible if
    /// the system knows they were generated rather than typed. A number a person
    /// typed is part of the controlled text and is never touched.
    /// </para>
    /// </summary>
    public bool AutoNumberHeadings { get; set; }

    // -------------------------------------------------------------------------

    /// <summary>Paper width in twips, after orientation is applied.</summary>
    public int PageWidthTwips
    {
        get
        {
            var (w, h) = PaperTwips(PageSize);
            return IsLandscape ? h : w;
        }
    }

    public int PageHeightTwips
    {
        get
        {
            var (w, h) = PaperTwips(PageSize);
            return IsLandscape ? w : h;
        }
    }

    public bool IsLandscape =>
        Orientation.Equals("landscape", StringComparison.OrdinalIgnoreCase);

    /// <summary>The width text actually occupies, which is what a table has to fit inside.</summary>
    public int ContentWidthTwips =>
        Math.Max(720, PageWidthTwips - MarginLeftTwips - MarginRightTwips - GutterTwips);

    public int ContentHeightTwips =>
        Math.Max(720, PageHeightTwips - MarginTopTwips - MarginBottomTwips);

    public bool HasHeader =>
        !string.IsNullOrWhiteSpace(HeaderHtml) || PageNumberIsInHeader
        || (DifferentFirstPage && !string.IsNullOrWhiteSpace(FirstHeaderHtml))
        || (DifferentOddEven && !string.IsNullOrWhiteSpace(EvenHeaderHtml));

    public bool HasFooter =>
        !string.IsNullOrWhiteSpace(FooterHtml) || PageNumberIsInFooter
        || (DifferentFirstPage && !string.IsNullOrWhiteSpace(FirstFooterHtml))
        || (DifferentOddEven && !string.IsNullOrWhiteSpace(EvenFooterHtml));

    public bool PageNumberIsInHeader =>
        ShowPageNumbers && PageNumberPosition.StartsWith("HEADER", StringComparison.OrdinalIgnoreCase);

    public bool PageNumberIsInFooter =>
        ShowPageNumbers && PageNumberPosition.StartsWith("FOOTER", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Paper sizes in twips. ISO A sizes are defined in millimetres and converted
    /// at 1440/25.4 twips per mm; US sizes are defined in inches. These are the
    /// values Word itself writes, so a document round-trips without drifting by
    /// a twip each time.
    /// </summary>
    private static (int Width, int Height) PaperTwips(string size) => size.ToUpperInvariant() switch
    {
        "A3" => (16838, 23811),
        "LETTER" => (12240, 15840),
        "LEGAL" => (12240, 20160),
        _ => (11906, 16838)   // A4
    };

    public static DocumentPageSetupDto Default => new();

    /// <summary>
    /// Clamps anything a caller may have got wrong. A negative margin or a page
    /// size of zero would produce a .docx that Word offers to repair, and the
    /// settings arrive as JSON from a browser, so "it cannot happen" is not a
    /// property this object gets to assume.
    /// </summary>
    public DocumentPageSetupDto Normalised()
    {
        var (paperWidth, paperHeight) = PaperTwips(PageSize);
        var width = IsLandscape ? paperHeight : paperWidth;
        var height = IsLandscape ? paperWidth : paperHeight;

        // Margins may not consume the page. Half the paper each way leaves at
        // least half an inch of text, which is the smallest thing worth printing.
        var maxHorizontal = (width / 2) - 360;
        var maxVertical = (height / 2) - 360;

        return new DocumentPageSetupDto
        {
            PageSize = PageSize.ToUpperInvariant() switch
            {
                "A3" => "A3", "LETTER" => "LETTER", "LEGAL" => "LEGAL", _ => "A4"
            },
            Orientation = IsLandscape ? "landscape" : "portrait",
            MarginTopTwips = Clamp(MarginTopTwips, 0, maxVertical),
            MarginBottomTwips = Clamp(MarginBottomTwips, 0, maxVertical),
            MarginLeftTwips = Clamp(MarginLeftTwips, 0, maxHorizontal),
            MarginRightTwips = Clamp(MarginRightTwips, 0, maxHorizontal),
            HeaderDistanceTwips = Clamp(HeaderDistanceTwips, 0, maxVertical),
            FooterDistanceTwips = Clamp(FooterDistanceTwips, 0, maxVertical),
            GutterTwips = Clamp(GutterTwips, 0, 2880),
            Columns = Clamp(Columns, 1, 3),
            HeaderHtml = HeaderHtml,
            FooterHtml = FooterHtml,
            FirstHeaderHtml = FirstHeaderHtml,
            FirstFooterHtml = FirstFooterHtml,
            EvenHeaderHtml = EvenHeaderHtml,
            EvenFooterHtml = EvenFooterHtml,
            DifferentFirstPage = DifferentFirstPage,
            DifferentOddEven = DifferentOddEven,
            ShowPageNumbers = ShowPageNumbers,
            PageNumberFormat = PageNumberFormat.ToUpperInvariant() switch
            {
                "PAGE_X" => "PAGE_X", "PLAIN_X" => "PLAIN_X", _ => "PAGE_X_OF_Y"
            },
            PageNumberPosition = PageNumberPosition.ToUpperInvariant() switch
            {
                "FOOTER_LEFT" => "FOOTER_LEFT", "FOOTER_RIGHT" => "FOOTER_RIGHT",
                "HEADER_RIGHT" => "HEADER_RIGHT", "HEADER_LEFT" => "HEADER_LEFT",
                "HEADER_CENTER" => "HEADER_CENTER", _ => "FOOTER_CENTER"
            },
            StyleSet = string.IsNullOrWhiteSpace(StyleSet) ? "DEFAULT" : StyleSet.ToUpperInvariant(),
            AutoNumberHeadings = AutoNumberHeadings
        };

        static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }
}


/// <summary>
/// Renders an authored document to read-only markup for the protected viewer
/// (URS-7.16), so a reviewer can read a Word or Excel file without the system
/// handing them the editable original.
/// </summary>
public interface IDocumentPreviewRenderer
{
    bool CanRender(string mimeType, string fileName);

    Task<DocumentPreviewDto> RenderAsync(string relativePath, string fileName, string mimeType,
        CancellationToken ct = default);
}


/// <summary>
/// Reduces a document body posted by the in-application editor to a whitelist of
/// permitted markup.
///
/// <para>
/// This is a security boundary, not a tidying step. The body it returns is
/// stored, redisplayed to every reviewer and approver, and rendered into the
/// controlled PDF, so anything it lets through executes in the session of the
/// person who approves the document. Implementations must rebuild the output
/// from a parse of the input rather than editing the input in place.
/// </para>
/// </summary>
public interface IEditorHtmlSanitizer
{
    EditorHtmlSanitizeResult Sanitize(string? html, EditorSanitizeLimits limits);
}


/// <summary>
/// Writes an authored body out as a real Open XML .docx package (URS-7.1).
///
/// <para>
/// The editor is the authoring surface; the .docx it produces is the controlled
/// artefact. Writing a genuine OOXML package - rather than, say, storing the
/// HTML and calling it a document - is what keeps everything downstream
/// unchanged: the same file store path, the same SHA-256 on the version row, the
/// same converter producing the same watermarked PDF master, the same controlled
/// copy. An inspector who asks for the authored source gets a Word file, exactly
/// as they would for an uploaded document.
/// </para>
/// </summary>
public interface IHtmlDocxWriter
{
    /// <summary>
    /// Builds a .docx package in memory from sanitised body HTML. The stream is
    /// positioned at 0 and owned by the caller.
    /// </summary>
    Stream Write(string sanitizedHtml, DocxWriteMetadata metadata);
}


/// <summary>Document properties written into docProps so the file identifies itself.</summary>
public sealed class DocxWriteMetadata
{
    public string Title { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Page geometry, running header and footer, and page numbering. Null falls
    /// back to A4 portrait with 25 mm margins - the same default the editor
    /// shows, so a document written before page setup existed exports exactly as
    /// it did then.
    /// </summary>
    public DocumentPageSetupDto? PageSetup { get; set; }
}

public interface IDocumentTextExtractor
{
    bool CanExtract(string? fileName, string? mimeType);

    Task<string?> ExtractHtmlAsync(string? relativePath, CancellationToken ct = default);
}

/// <summary>DMS_V2: the part of the old IFileStorageService the ported readers use, served by FileStore.</summary>
public interface IFileStorageService
{
    Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default);
    string ResolveAbsolutePath(string relativePath);
}

public sealed class FileStoreStorageAdapter : IFileStorageService
{
    private readonly FileStore _store;
    public FileStoreStorageAdapter(FileStore store) => _store = store;
    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default) => Task.FromResult(_store.Exists(relativePath));
    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default) => _store.OpenCachedReadAsync(relativePath,ct);
    public string ResolveAbsolutePath(string relativePath) => _store.ResolveAbsolute(relativePath);
}

/// <summary>Model of Views/Shared/_EditorRibbon.cshtml (as in Bcl.Dms).</summary>
public sealed class EditorRibbonViewModel
{
    public bool IsTemplate { get; set; }
    public long DocumentId { get; set; }
    public bool HeaderFooterEnabled { get; set; } = true;
}
