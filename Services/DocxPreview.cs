//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 3a - read-only HTML preview of a .docx (text, headings, bold / italic / underline, lists, tables).
 * Until the PDF rendition service arrives (Phase 4) this is how a Word source is read on screen.
 * Pure Open XML reading (System.IO.Compression + XML) - no Office, no external converter.
 * Every piece of text is HTML-encoded; nothing from the file is emitted as markup.
 */
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace DMS.Web.Services
{
    public static class DocxPreview
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const int MaxParagraphs = 20000;

        public static string ToHtml(Stream docx)
        {
            using var zip = new ZipArchive(docx, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry("word/document.xml")
                        ?? throw new FileStoreException("The file is not a Word document (word/document.xml is missing).");
            XDocument xml;
            using (var s = entry.Open()) xml = XDocument.Load(s);

            var body = xml.Root?.Element(W + "body");
            if (body == null) return "<p class=\"text-muted\">The document is empty.</p>";

            var html = new StringBuilder();
            var count = 0;
            foreach (var block in body.Elements())
            {
                if (++count > MaxParagraphs) { html.Append("<p class=\"text-muted\">… (preview truncated)</p>"); break; }
                if (block.Name == W + "p") html.Append(Paragraph(block));
                else if (block.Name == W + "tbl") html.Append(Table(block));
                else if (block.Name == W + "sdt")
                    foreach (var p in block.Descendants(W + "p")) html.Append(Paragraph(p));
            }
            return html.ToString();
        }

        private static string Paragraph(XElement p)
        {
            var style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
            var isList = p.Element(W + "pPr")?.Element(W + "numPr") != null;
            var tag = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style.AsSpan(7), out var level)
                ? $"h{Math.Clamp(level + 2, 3, 6)}"
                : style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? "h2" : "p";

            var inner = new StringBuilder();
            foreach (var node in p.Elements())
            {
                if (node.Name == W + "r") inner.Append(Run(node));
                else if (node.Name == W + "hyperlink" || node.Name == W + "ins" || node.Name == W + "smartTag")
                    foreach (var r in node.Elements(W + "r")) inner.Append(Run(r));
            }
            var content = inner.Length == 0 ? "&nbsp;" : inner.ToString();
            return isList ? $"<p class=\"ms-4\">&bull; {content}</p>" : $"<{tag}>{content}</{tag}>";
        }

        private static string Run(XElement r)
        {
            var props = r.Element(W + "rPr");
            bool On(string name) => props?.Element(W + name) is { } e && e.Attribute(W + "val")?.Value is not ("0" or "false");
            var text = new StringBuilder();
            foreach (var n in r.Elements())
            {
                if (n.Name == W + "t") text.Append(WebUtility.HtmlEncode(n.Value));
                else if (n.Name == W + "tab") text.Append("&emsp;");
                else if (n.Name == W + "br" || n.Name == W + "cr") text.Append("<br />");
            }
            var s = text.ToString();
            if (s.Length == 0) return s;
            if (On("b")) s = $"<strong>{s}</strong>";
            if (On("i")) s = $"<em>{s}</em>";
            if (props?.Element(W + "u") is { } u && u.Attribute(W + "val")?.Value != "none") s = $"<u>{s}</u>";
            if (On("strike")) s = $"<s>{s}</s>";
            return s;
        }

        private static string Table(XElement tbl)
        {
            var html = new StringBuilder("<table class=\"table table-bordered table-sm\">");
            foreach (var tr in tbl.Elements(W + "tr"))
            {
                html.Append("<tr>");
                foreach (var tc in tr.Elements(W + "tc"))
                {
                    var span = tc.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value;
                    html.Append(span is { } sp && int.TryParse(sp, out var n) && n > 1 ? $"<td colspan=\"{n}\">" : "<td>");
                    foreach (var child in tc.Elements())
                    {
                        if (child.Name == W + "p") html.Append(Paragraph(child));
                        else if (child.Name == W + "tbl") html.Append(Table(child));
                    }
                    html.Append("</td>");
                }
                html.Append("</tr>");
            }
            return html.Append("</table>").ToString();
        }
    }
}
