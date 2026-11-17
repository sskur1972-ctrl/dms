//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 4 - stamps a controlled copy (URS-7.18, 7.20): the state watermark across every page, a footer line
 * (document, revision, state, page x of y) and a signature page with the manifestation of every electronic
 * signature (21 CFR 11.50). Replaces PdfSharpWatermarkService of the old DMS; no third-party library.
 *
 * The original page content is wrapped in q ... Q, so nothing it leaves on the graphics stack can move the stamp.
 * Text uses the standard Helvetica fonts (no embedding) in WinAnsiEncoding.
 */
using System.Globalization;
using System.Text;

namespace DMS.Web.Services.Pdf
{
    public sealed class PdfStampOptions
    {
        /// <summary>Across every page, e.g. "MASTER COPY". Empty = no watermark.</summary>
        public string? Watermark { get; set; }
        /// <summary>Bottom line of every page. {page} and {pages} are replaced.</summary>
        public string? Footer { get; set; }
        /// <summary>Top line of every page, right-aligned and bold (e.g. the controlled-copy number). {page} and {pages} are replaced.</summary>
        public string? Header { get; set; }
        /// <summary>Header lines of the signature page (document, title, revision).</summary>
        public List<string> SignatureHeader { get; set; } = new();
        /// <summary>One entry per signature (the manifestation text). Empty = no signature page.</summary>
        public List<string> Signatures { get; set; } = new();
        public string SignatureTitle { get; set; } = "Electronic signatures";
        public string? SignatureNote { get; set; }
    }

    public static class PdfStamper
    {
        public static byte[] Stamp(byte[] pdf, PdfStampOptions options)
        {
            var reader = new PdfReader(pdf);
            var writer = new PdfIncrementalWriter(reader);

            if (reader.Resolve(reader.Trailer["Root"]) is not PdfDictionary catalog || catalog["Pages"] is not PdfReference pagesRef)
                throw new PdfException("The PDF has no page tree.");

            var pages = new List<PageInfo>();
            CollectPages(reader, pagesRef, null, null, null, 0, pages, new HashSet<int>(), 0);
            if (pages.Count == 0) throw new PdfException("The PDF has no pages.");

            var font = writer.Add(Font("Helvetica"));
            var fontBold = writer.Add(Font("Helvetica-Bold"));
            var faint = writer.Add(GraphicsState(0.16));
            var save = writer.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("q\n")));

            var withSignaturePage = options.Signatures.Count > 0;
            var total = pages.Count + (withSignaturePage ? 1 : 0);

            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                var (w, h, matrix) = Geometry(page.Box, page.Rotate);
                var content = new StringBuilder("Q\n");
                content.Append("q ").Append(Matrix(matrix)).Append(" cm\n");
                AppendWatermark(content, options.Watermark, w, h);
                AppendFooter(content, options.Footer, i + 1, total, w);
                AppendHeader(content, options.Header, i + 1, total, w, h);
                content.Append("Q\n");
                var stamp = writer.Add(Compressed(content.ToString()));

                var dict = page.Dictionary.Clone();
                var contents = new PdfArray();
                contents.Items.Add(save);
                switch (reader.Resolve(dict["Contents"]))
                {
                    case PdfArray existing: contents.Items.AddRange(existing.Items); break;
                    case PdfStream when dict["Contents"] is PdfReference r: contents.Items.Add(r); break;
                }
                contents.Items.Add(stamp);
                dict["Contents"] = contents;
                dict["Resources"] = MergeResources(reader, page.Resources, font, fontBold, faint);
                writer.Replace(page.Reference, dict);
            }

            if (withSignaturePage)
            {
                const double w = 595.28, h = 841.89;
                var content = new StringBuilder("q\n");
                AppendWatermark(content, options.Watermark, w, h);
                AppendSignatureText(content, options, w, h);
                AppendFooter(content, options.Footer, total, total, w);
                AppendHeader(content, options.Header, total, total, w, h);
                content.Append("Q\n");
                var stream = writer.Add(Compressed(content.ToString()));

                var sigPage = new PdfDictionary();
                sigPage["Type"] = new PdfName("Page");
                sigPage["Parent"] = pagesRef;
                sigPage["MediaBox"] = new PdfArray(new PdfObj[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(w, false), new PdfNumber(h, false) });
                sigPage["Rotate"] = new PdfNumber(0);
                sigPage["Resources"] = MergeResources(reader, null, font, fontBold, faint);
                sigPage["Contents"] = stream;
                var sigRef = writer.Add(sigPage);

                var root = ((PdfDictionary)reader.Resolve(pagesRef)!).Clone();
                var kids = reader.Resolve(root["Kids"]) is PdfArray k ? new PdfArray(k.Items) : new PdfArray();
                kids.Items.Add(sigRef);
                root["Kids"] = kids;
                root["Count"] = new PdfNumber(((reader.Resolve(root["Count"]) as PdfNumber)?.AsLong ?? pages.Count) + 1);
                writer.Replace(pagesRef, root);
            }

            return writer.Save();
        }

        /// <summary>Number of pages, for a quick check of an uploaded PDF.</summary>
        public static int CountPages(byte[] pdf)
        {
            var reader = new PdfReader(pdf);
            if (reader.Resolve(reader.Trailer["Root"]) is not PdfDictionary catalog || catalog["Pages"] is not PdfReference pagesRef)
                throw new PdfException("The PDF has no page tree.");
            var pages = new List<PageInfo>();
            CollectPages(reader, pagesRef, null, null, null, 0, pages, new HashSet<int>(), 0);
            return pages.Count;
        }

        // ------------------------------------------------------------------ page tree

        private sealed record PageInfo(PdfReference Reference, PdfDictionary Dictionary, PdfDictionary? Resources, double[] Box, int Rotate);

        private static void CollectPages(PdfReader reader, PdfReference nodeRef, PdfDictionary? resources, double[]? mediaBox,
                                         double[]? cropBox, int rotate, List<PageInfo> pages, HashSet<int> seen, int depth)
        {
            if (depth > 64 || !seen.Add(nodeRef.Number)) return;
            if (reader.Resolve(nodeRef) is not PdfDictionary node) return;
            if (reader.Resolve(node["Resources"]) is PdfDictionary res) resources = res;
            mediaBox = Box(reader, node["MediaBox"]) ?? mediaBox;
            cropBox = Box(reader, node["CropBox"]) ?? cropBox;
            if (reader.Resolve(node["Rotate"]) is PdfNumber rot) rotate = (int)rot.AsLong;

            var type = (reader.Resolve(node["Type"]) as PdfName)?.Value;
            if (type == "Pages" || (type == null && node["Kids"] != null))
            {
                if (reader.Resolve(node["Kids"]) is PdfArray kids)
                    foreach (var kid in kids.Items)
                        if (kid is PdfReference kr) CollectPages(reader, kr, resources, mediaBox, cropBox, rotate, pages, seen, depth + 1);
                return;
            }
            var box = cropBox ?? mediaBox ?? new[] { 0d, 0d, 612d, 792d };
            if (mediaBox != null && cropBox != null)   // the crop box is clipped to the media box
                box = new[] { Math.Max(box[0], mediaBox[0]), Math.Max(box[1], mediaBox[1]), Math.Min(box[2], mediaBox[2]), Math.Min(box[3], mediaBox[3]) };
            pages.Add(new PageInfo(nodeRef, node, resources, box, ((rotate % 360) + 360) % 360));
        }

        private static double[]? Box(PdfReader reader, PdfObj? obj)
        {
            if (reader.Resolve(obj) is not PdfArray a || a.Items.Count < 4) return null;
            var v = a.Items.Take(4).Select(i => (reader.Resolve(i) as PdfNumber)?.Value ?? 0).ToArray();
            return new[] { Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]) };
        }

        /// <summary>The page as the reader sees it (after /Rotate): width, height, and the matrix from that view to user space.</summary>
        private static (double W, double H, double[] M) Geometry(double[] box, int rotate)
        {
            double x0 = box[0], y0 = box[1], x1 = box[2], y1 = box[3], bw = x1 - x0, bh = y1 - y0;
            return rotate switch
            {
                90 => (bh, bw, new[] { 0d, 1, -1, 0, x1, y0 }),
                180 => (bw, bh, new[] { -1d, 0, 0, -1, x1, y1 }),
                270 => (bh, bw, new[] { 0d, -1, 1, 0, x0, y1 }),
                _ => (bw, bh, new[] { 1d, 0, 0, 1, x0, y0 })
            };
        }

        private static PdfDictionary MergeResources(PdfReader reader, PdfDictionary? existing, PdfReference font, PdfReference fontBold,
                                                    PdfReference faint)
        {
            var res = existing?.Clone() ?? new PdfDictionary();
            var fonts = reader.Resolve(res["Font"]) is PdfDictionary f ? f.Clone() : new PdfDictionary();
            fonts["DmsF1"] = font;
            fonts["DmsF2"] = fontBold;
            res["Font"] = fonts;
            var states = reader.Resolve(res["ExtGState"]) is PdfDictionary g ? g.Clone() : new PdfDictionary();
            states["DmsGs"] = faint;
            res["ExtGState"] = states;
            if (res["ProcSet"] == null)
                res["ProcSet"] = new PdfArray(new PdfObj[] { new PdfName("PDF"), new PdfName("Text") });
            return res;
        }

        private static PdfDictionary Font(string baseFont)
        {
            var d = new PdfDictionary();
            d["Type"] = new PdfName("Font");
            d["Subtype"] = new PdfName("Type1");
            d["BaseFont"] = new PdfName(baseFont);
            d["Encoding"] = new PdfName("WinAnsiEncoding");
            return d;
        }

        private static PdfDictionary GraphicsState(double alpha)
        {
            var d = new PdfDictionary();
            d["Type"] = new PdfName("ExtGState");
            d["ca"] = new PdfNumber(alpha, false);
            d["CA"] = new PdfNumber(alpha, false);
            return d;
        }

        private static PdfStream Compressed(string content)
        {
            var dict = new PdfDictionary();
            dict["Filter"] = new PdfName("FlateDecode");
            return new PdfStream(dict, PdfFilters.Deflate(Encoding.Latin1.GetBytes(content)));
        }

        // ------------------------------------------------------------------ drawing

        private static void AppendWatermark(StringBuilder c, string? text, double w, double h)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim().ToUpperInvariant();
            var angle = Math.Atan2(h, w);
            var diagonal = Math.Sqrt(w * w + h * h);
            var units = TextWidth(text, bold: true);
            var size = Math.Clamp(diagonal * 0.72 / Math.Max(units, 1) * 1000, 18, 120);
            var tw = units * size / 1000;
            double cos = Math.Cos(angle), sin = Math.Sin(angle), cap = 0.72 * size;
            var x = w / 2 - tw / 2 * cos + cap / 2 * sin;
            var y = h / 2 - tw / 2 * sin - cap / 2 * cos;
            c.Append("q /DmsGs gs 0.45 0.45 0.45 rg BT /DmsF2 ").Append(N(size)).Append(" Tf ")
             .Append(N(cos)).Append(' ').Append(N(sin)).Append(' ').Append(N(-sin)).Append(' ').Append(N(cos)).Append(' ')
             .Append(N(x)).Append(' ').Append(N(y)).Append(" Tm ").Append(Literal(text)).Append(" Tj ET Q\n");
        }

        private static void AppendFooter(StringBuilder c, string? footer, int page, int pages, double w)
        {
            if (string.IsNullOrWhiteSpace(footer)) return;
            var text = footer.Replace("{page}", page.ToString(CultureInfo.InvariantCulture))
                             .Replace("{pages}", pages.ToString(CultureInfo.InvariantCulture));
            const double size = 7;
            var max = w - 36;
            while (text.Length > 10 && TextWidth(text, false) * size / 1000 > max) text = text[..^2] + "…";
            c.Append("q 0.2 0.2 0.2 rg BT /DmsF1 ").Append(N(size)).Append(" Tf 1 0 0 1 18 11 Tm ")
             .Append(Literal(text)).Append(" Tj ET Q\n");
        }

        private static void AppendHeader(StringBuilder c, string? header, int page, int pages, double w, double h)
        {
            if (string.IsNullOrWhiteSpace(header)) return;
            var text = header.Replace("{page}", page.ToString(CultureInfo.InvariantCulture))
                             .Replace("{pages}", pages.ToString(CultureInfo.InvariantCulture));
            const double size = 8;
            var max = w - 36;
            while (text.Length > 10 && TextWidth(text, true) * size / 1000 > max) text = text[..^2] + "…";
            var x = Math.Max(18, w - 18 - TextWidth(text, true) * size / 1000);
            c.Append("q 0.6 0 0 rg BT /DmsF2 ").Append(N(size)).Append(" Tf 1 0 0 1 ").Append(N(x)).Append(' ').Append(N(h - 16))
             .Append(" Tm ").Append(Literal(text)).Append(" Tj ET Q\n");
        }

        private static void AppendSignatureText(StringBuilder c, PdfStampOptions o, double w, double h)
        {
            var y = h - 72;
            void Line(string text, double size, bool bold, double gap)
            {
                if (y < 60) return;
                c.Append("BT ").Append(bold ? "/DmsF2 " : "/DmsF1 ").Append(N(size)).Append(" Tf 1 0 0 1 56 ").Append(N(y))
                 .Append(" Tm ").Append(Literal(text)).Append(" Tj ET\n");
                y -= gap;
            }
            c.Append("0 0 0 rg\n");
            Line(o.SignatureTitle, 15, true, 26);
            foreach (var header in o.SignatureHeader) Line(header, 10, false, 15);
            y -= 10;
            c.Append("0.6 0.6 0.6 RG 0.5 w 56 ").Append(N(y + 8)).Append(" m ").Append(N(w - 56)).Append(' ').Append(N(y + 8)).Append(" l S\n");
            y -= 8;
            var n = 0;
            foreach (var signature in o.Signatures)
            {
                n++;
                var wrapped = Wrap(signature, 9, w - 112 - 18);
                for (var i = 0; i < wrapped.Count; i++)
                    Line((i == 0 ? n + ".  " : "     ") + wrapped[i], 9, false, 12.5);
                y -= 6;
                if (y < 60) { Line("... (further signatures are recorded in the DMS)", 9, false, 12); break; }
            }
            if (!string.IsNullOrWhiteSpace(o.SignatureNote))
            {
                y = Math.Min(y - 10, 90);
                foreach (var line in Wrap(o.SignatureNote, 8, w - 112)) Line(line, 8, false, 11);
            }
        }

        private static List<string> Wrap(string text, double size, double width)
        {
            var lines = new List<string>();
            var current = new StringBuilder();
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && TextWidth(candidate, false) * size / 1000 > width)
                {
                    lines.Add(current.ToString());
                    current.Clear().Append(word);
                }
                else { current.Clear().Append(candidate); }
            }
            if (current.Length > 0) lines.Add(current.ToString());
            return lines;
        }

        private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static string Matrix(double[] m) => string.Join(' ', m.Select(N));

        /// <summary>A PDF literal string in WinAnsiEncoding (characters outside it become '?').</summary>
        private static string Literal(string text)
        {
            var sb = new StringBuilder("(");
            foreach (var ch in text)
            {
                var b = WinAnsi(ch);
                if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
                else if (b < 32 || b > 126) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                else sb.Append((char)b);
            }
            return sb.Append(')').ToString();
        }

        private static byte WinAnsi(char ch) => ch switch
        {
            >= ' ' and <= '~' => (byte)ch,
            >= ' ' and <= 'ÿ' => (byte)ch,
            '–' => 0x96, '—' => 0x97, '‘' => 0x91, '’' => 0x92, '“' => 0x93, '”' => 0x94,
            '•' => 0x95, '…' => 0x85, '€' => 0x80, '→' => (byte)'>',
            _ => (byte)'?'
        };

        // Helvetica / Helvetica-Bold advance widths (1/1000 em) for ASCII 32..126 (Adobe standard AFM)
        private static readonly short[] Regular =
        {
            278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,
            1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,
            333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584
        };
        private static readonly short[] Bold =
        {
            278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,
            975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,
            333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,611,611,389,556,333,611,556,778,556,556,500,389,280,389,584
        };

        private static double TextWidth(string text, bool bold)
        {
            var table = bold ? Bold : Regular;
            double total = 0;
            foreach (var ch in text) total += ch is >= ' ' and <= '~' ? table[ch - 32] : 556;
            return total;
        }
    }
}
