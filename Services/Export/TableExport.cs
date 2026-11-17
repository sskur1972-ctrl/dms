//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5b - report and audit-trail extracts as CSV (UTF-8 with BOM, opens in Excel) or a real .xlsx workbook.
 * The .xlsx is written by hand (Office Open XML SpreadsheetML in a ZIP) - no third-party library.
 * Every extract carries a heading block: what it is, the filter, who produced it and when.
 */
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace DMS.Web.Services.Export
{
    public sealed record ExportFile(byte[] Bytes, string ContentType, string FileName, string Sha256);

    public sealed class TableData
    {
        public string Title { get; init; } = "";
        /// <summary>Heading lines above the table (filter, produced by, produced at).</summary>
        public List<string> Heading { get; init; } = new();
        public List<string> Columns { get; init; } = new();
        public List<object?[]> Rows { get; init; } = new();
    }

    public static class TableExport
    {
        public static ExportFile Create(TableData data, string format, string baseName)
        {
            var xlsx = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase);
            var bytes = xlsx ? Xlsx(data) : Csv(data);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new ExportFile(bytes, xlsx ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv; charset=utf-8",
                                  $"{baseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{(xlsx ? "xlsx" : "csv")}", sha);
        }

        public static string Text(object? value) => value switch
        {
            null => "",
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) : d.ToString("dd-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture),
            bool b => b ? "Yes" : "No",
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            double f => f.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };

        // ------------------------------------------------------------------ CSV
        private static byte[] Csv(TableData data)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Cell(data.Title));
            foreach (var h in data.Heading) sb.AppendLine(Cell(h));
            sb.AppendLine();
            sb.AppendLine(string.Join(',', data.Columns.Select(Cell)));
            foreach (var row in data.Rows) sb.AppendLine(string.Join(',', row.Select(v => Cell(Text(v)))));
            var preamble = Encoding.UTF8.GetPreamble();
            var body = Encoding.UTF8.GetBytes(sb.ToString());
            return preamble.Concat(body).ToArray();
        }

        private static string Cell(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var v = value.Replace("\r", " ").Replace("\n", " ");
            // a cell starting with = + - @ would be run as a formula by Excel (CSV injection)
            if ("=+-@".IndexOf(v[0]) >= 0) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        // ------------------------------------------------------------------ XLSX
        private static byte[] Xlsx(TableData data)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                void Put(string name, string xml)
                {
                    var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                    w.Write(xml);
                }
                Put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    "</Types>");
                Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                Put("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"" + Xml(SheetName(data.Title)) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Put("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>");
                // style 0 normal, 1 bold (title / header), 2 header with fill
                Put("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<fonts count=\"2\"><font><sz val=\"10\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><name val=\"Calibri\"/></font></fonts>" +
                    "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDCEFEA\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                    "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                    "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                    "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/></cellXfs>" +
                    "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                    "</styleSheet>");

                var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                var headerRow = data.Heading.Count + 3;   // title, heading lines, blank, header
                sheet.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"").Append(headerRow)
                     .Append("\" topLeftCell=\"A").Append(headerRow + 1).Append("\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                sheet.Append("<cols>");
                for (var c = 0; c < data.Columns.Count; c++)
                {
                    var width = Math.Clamp(Math.Max(data.Columns[c].Length, data.Rows.Take(200).Select(r => Text(r[c]).Length).DefaultIfEmpty(0).Max()) + 2, 8, 60);
                    sheet.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1).Append("\" width=\"").Append(width).Append("\" customWidth=\"1\"/>");
                }
                sheet.Append("</cols><sheetData>");
                var r = 0;
                void Row(IEnumerable<(object? Value, int Style)> cells)
                {
                    r++;
                    sheet.Append("<row r=\"").Append(r).Append("\">");
                    var c = 0;
                    foreach (var (value, style) in cells)
                    {
                        var reference = ColumnName(c++) + r;
                        if (value is null) continue;
                        var isNumber = value is int or long or short or decimal or double or float;
                        if (isNumber)
                            sheet.Append("<c r=\"").Append(reference).Append("\" s=\"").Append(style).Append("\"><v>")
                                 .Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("</v></c>");
                        else
                            sheet.Append("<c r=\"").Append(reference).Append("\" s=\"").Append(style).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                                 .Append(Xml(Text(value))).Append("</t></is></c>");
                    }
                    sheet.Append("</row>");
                }
                Row(new[] { ((object?)data.Title, 1) });
                foreach (var h in data.Heading) Row(new[] { ((object?)h, 0) });
                r++;   // blank line
                Row(data.Columns.Select(c => ((object?)c, 2)));
                foreach (var row in data.Rows) Row(row.Select(v => (v, 0)));
                sheet.Append("</sheetData>");
                if (data.Columns.Count > 0)
                    sheet.Append("<autoFilter ref=\"A").Append(headerRow).Append(':').Append(ColumnName(data.Columns.Count - 1)).Append(Math.Max(r, headerRow)).Append("\"/>");
                sheet.Append("</worksheet>");
                Put("xl/worksheets/sheet1.xml", sheet.ToString());
            }
            return ms.ToArray();
        }

        private static string ColumnName(int index)
        {
            var name = "";
            for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
            return name;
        }

        private static string SheetName(string title)
        {
            var clean = new string((title ?? "Report").Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray()).Trim();
            return clean.Length == 0 ? "Report" : clean.Length > 31 ? clean[..31] : clean;
        }

        // XML 1.0 forbids most control characters even when escaped
        private static string Xml(string value)
            => SecurityElement.Escape(new string(value.Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ').ToArray())) ?? "";
    }
}
