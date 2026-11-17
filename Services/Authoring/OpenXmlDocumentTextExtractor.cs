//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace DMS.Web.Services.Authoring;

/// <summary>
/// Reads the text of a stored <c>.docx</c> so the audit trail can report what
/// changed in a document, not merely that it changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>No new dependency.</b> A <c>.docx</c> is a zip of XML parts, and the one
/// part that holds the body is <c>word/document.xml</c>. Everything needed to
/// get at it is in the framework - <see cref="ZipArchive"/> and
/// <c>System.Xml.Linq</c> - which is the same reasoning that already governs
/// <c>OpenXmlPreviewRenderer</c> and <c>HtmlDocxWriter</c>. A document-parsing
/// library here would be a third-party component in a validated system, to do
/// something two dozen lines already do.
/// </para>
/// <para>
/// <b>All it does is get the XML out.</b> Every judgement about what the XML
/// SAYS is in <see cref="DocxTextRules"/>, which is pure and covered by tests
/// pinned against WordprocessingML a real writer produced. This class holds the
/// file handling, and nothing else, because file handling is the part that
/// cannot be unit tested here.
/// </para>
/// <para>
/// <b>It cannot fail a save.</b> Every failure returns null. The caller then
/// records the SHA-256 and the size exactly as it did before any of this
/// existed, so the worst outcome of a damaged package or an unreadable format
/// is the audit row people had last month.
/// </para>
/// </remarks>
public sealed class OpenXmlDocumentTextExtractor : IDocumentTextExtractor
{
    /// <summary>
    /// The biggest <c>word/document.xml</c> that will be read into memory.
    /// </summary>
    /// <remarks>
    /// A zip entry declares its own uncompressed length and a hostile or
    /// corrupt one can declare a great deal ("zip bomb"). The read is capped
    /// rather than trusted: past this, the file is treated as unreadable and the
    /// save proceeds with fingerprints only.
    /// </remarks>
    private const int MaxDocumentXmlBytes = 16 * 1024 * 1024;

    private const string BodyPart = "word/document.xml";

    private readonly IFileStorageService _storage;
    private readonly ILogger<OpenXmlDocumentTextExtractor> _logger;

    public OpenXmlDocumentTextExtractor(IFileStorageService storage,
        ILogger<OpenXmlDocumentTextExtractor> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// Modern Word and Excel packages with readable text.
    /// </summary>
    /// <remarks>
    /// Excel values include the worksheet name and cell address. Binary Office
    /// files, presentations and PDFs are not read by this package extractor.
    /// </remarks>
    public bool CanExtract(string? fileName, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(fileName) &&
            (fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return string.Equals(mimeType,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            StringComparison.OrdinalIgnoreCase) || string.Equals(mimeType,"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> ExtractHtmlAsync(string? relativePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        try
        {
            if (!await _storage.ExistsAsync(relativePath, ct).ConfigureAwait(false)) return null;

            await using var file = await _storage.OpenReadAsync(relativePath, ct).ConfigureAwait(false);

            // The zip is read from a seekable copy. A storage stream is not
            // required to be seekable and ZipArchive needs to seek to the
            // central directory; buffering here is what makes this work against
            // any IFileStorageService rather than only the local one.
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct).ConfigureAwait(false);
            buffer.Position = 0;

            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);

            if(archive.GetEntry("xl/workbook.xml") != null) return await ReadWorkbookAsync(archive,ct);

            var entry = archive.GetEntry(BodyPart);
            if (entry is null) return null;

            if (entry.Length > MaxDocumentXmlBytes)
            {
                _logger.LogWarning(
                    "The body part of {Path} declares {Bytes} bytes, past the {Cap} byte cap, so its text was not " +
                    "read. The change will be audited by its fingerprints only.",
                    relativePath, entry.Length, MaxDocumentXmlBytes);

                return null;
            }

            await using var part = entry.Open();

            // Capped again on the way out: the declared length above is the
            // zip's claim about itself, and a stream that keeps producing bytes
            // past it must be stopped rather than believed.
            using var reader = new StreamReader(part, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var xml = await ReadCappedAsync(reader, MaxDocumentXmlBytes, ct).ConfigureAwait(false);

            if (xml is null)
            {
                _logger.LogWarning("The body part of {Path} exceeded the read cap; its text was not read.",
                    relativePath);
                return null;
            }

            // Do not silently treat a capped extraction as the complete previous/new value.
            using(var check=System.Xml.XmlReader.Create(new StringReader(xml),new System.Xml.XmlReaderSettings { DtdProcessing=System.Xml.DtdProcessing.Prohibit }))
            {
                int paragraphs=0;
                while(check.Read())if(check.NodeType==System.Xml.XmlNodeType.Element && check.LocalName=="p" && check.NamespaceURI=="http://schemas.openxmlformats.org/wordprocessingml/2006/main" && ++paragraphs>DocxTextRules.MaxParagraphs)
                {
                    _logger.LogWarning("Document text exceeds the audit extraction paragraph limit. Full source-file evidence is retained; readable values are unavailable.");
                    return null;
                }
            }
            var html = DocxTextRules.AsHtml(xml);
            return string.IsNullOrEmpty(html) ? null : html;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Broad on purpose. A damaged package, a file that is not a zip at
            // all, a storage fault - all have the same consequence here, and
            // none of them may take down the save that triggered this.
            _logger.LogWarning(ex,
                "The text of {Path} could not be read. The change will be audited by its fingerprints only.",
                relativePath);

            return null;
        }
    }

    private static async Task<string?> ReadWorkbookAsync(ZipArchive archive,CancellationToken ct)
    {
        XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        long budget=MaxDocumentXmlBytes;
        async Task<XDocument?> Read(string path) {
            var e=archive.GetEntry(path); if(e==null)return null;
            budget-=e.Length; if(budget<0)throw new InvalidDataException("Workbook exceeds audit extraction limit.");
            using var reader=new StreamReader(e.Open());
            var xml=await ReadCappedAsync(reader,MaxDocumentXmlBytes,ct);
            return xml==null?throw new InvalidDataException("Workbook exceeds audit extraction limit."):XDocument.Parse(xml);
        }
        var workbook=await Read("xl/workbook.xml");
        var relations=await Read("xl/_rels/workbook.xml.rels");
        var shared=(await Read("xl/sharedStrings.xml"))?.Descendants(ns+"si").Select(si=>string.Concat(si.Descendants(ns+"t").Select(t=>t.Value))).ToArray() ?? [];
        var html=new StringBuilder();
        foreach(var sheet in workbook!.Descendants(ns+"sheet")) {
            var target=relations?.Root?.Elements().FirstOrDefault(e=>(string?)e.Attribute("Id")== (string?)sheet.Attribute(rel+"id"))?.Attribute("Target")?.Value;
            if(target==null)continue;
            var path=target.StartsWith('/')?target.TrimStart('/'):"xl/"+target.Replace("\\","/");
            var content=await Read(path); if(content==null)continue;
            var name=(string?)sheet.Attribute("name")??"Worksheet";
            foreach(var cell in content.Descendants(ns+"c")) {
                var type=(string?)cell.Attribute("t"); var value=cell.Element(ns+"v")?.Value??"";
                if(type=="s" && int.TryParse(value,out var i) && i>=0 && i<shared.Length)value=shared[i];
                else if(type=="inlineStr")value=string.Concat(cell.Descendants(ns+"t").Select(t=>t.Value));
                else if(type=="b")value=value=="1"?"Yes":"No";
                var formula=cell.Element(ns+"f")?.Value;
                if(formula!=null)value=$"Formula: ={formula}; value: {value}";
                if(value.Length>0)html.Append("<p>").Append(System.Net.WebUtility.HtmlEncode($"{name}, cell {(string?)cell.Attribute("r")}: {value}")).Append("</p>");
            }
        }
        return html.ToString();
    }

    private static async Task<string?> ReadCappedAsync(StreamReader reader, int cap, CancellationToken ct)
    {
        var text = new StringBuilder();
        var chunk = new char[8192];

        while (true)
        {
            var read = await reader.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;

            if (text.Length + read > cap) return null;

            text.Append(chunk, 0, read);
        }

        return text.ToString();
    }
}

/// <summary>
/// Used where no text extraction is wanted at all.
/// </summary>
/// <remarks>
/// Registered so every caller can depend on <see cref="IDocumentTextExtractor"/>
/// unconditionally. With this in place the system behaves exactly as it did
/// before: a document change is audited by its SHA-256 and its size.
/// </remarks>
public sealed class NoDocumentTextExtractor : IDocumentTextExtractor
{
    public bool CanExtract(string? fileName, string? mimeType) => false;

    public Task<string?> ExtractHtmlAsync(string? relativePath, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
