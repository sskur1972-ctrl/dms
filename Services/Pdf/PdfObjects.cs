//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 4 - a small, dependency-free PDF reader / incremental writer, enough to stamp a controlled copy
 * (watermark, footer, signature page) without a third-party PDF library.
 *
 * Reads: classic cross-reference tables, cross-reference streams (PNG predictors), object streams,
 * incremental updates (/Prev), and falls back to scanning the file when the cross-reference is broken.
 * Writes: an INCREMENTAL UPDATE - the original bytes are kept unchanged and new / replaced objects are
 * appended, so the source PDF stays byte-for-byte inside the stamped one.
 * Not supported (refused with a clear message): encrypted PDFs.
 */
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace DMS.Web.Services.Pdf
{
    public sealed class PdfException : Exception
    {
        public PdfException(string message) : base(message) { }
    }

    // ------------------------------------------------------------------ object model

    public abstract class PdfObj { }

    public sealed class PdfNull : PdfObj { public static readonly PdfNull Instance = new(); }

    public sealed class PdfBool : PdfObj
    {
        public bool Value { get; }
        public PdfBool(bool value) => Value = value;
    }

    public sealed class PdfNumber : PdfObj
    {
        public double Value { get; }
        public bool IsInteger { get; }
        public PdfNumber(double value, bool isInteger) { Value = value; IsInteger = isInteger; }
        public PdfNumber(long value) : this(value, true) { }
        public long AsLong => (long)Math.Round(Value);
    }

    public sealed class PdfString : PdfObj
    {
        public byte[] Value { get; }
        public PdfString(byte[] value) => Value = value;
    }

    public sealed class PdfName : PdfObj
    {
        public string Value { get; }
        public PdfName(string value) => Value = value;
        public override string ToString() => "/" + Value;
    }

    public sealed class PdfArray : PdfObj
    {
        public List<PdfObj> Items { get; } = new();
        public PdfArray() { }
        public PdfArray(IEnumerable<PdfObj> items) => Items.AddRange(items);
    }

    public sealed class PdfDictionary : PdfObj
    {
        public List<KeyValuePair<string, PdfObj>> Entries { get; } = new();

        public PdfObj? this[string key]
        {
            get
            {
                foreach (var e in Entries) if (e.Key == key) return e.Value;
                return null;
            }
            set
            {
                var i = Entries.FindIndex(e => e.Key == key);
                if (value == null) { if (i >= 0) Entries.RemoveAt(i); return; }
                if (i >= 0) Entries[i] = new(key, value); else Entries.Add(new(key, value));
            }
        }

        public PdfDictionary Clone()
        {
            var copy = new PdfDictionary();
            copy.Entries.AddRange(Entries);
            return copy;
        }
    }

    public sealed class PdfReference : PdfObj
    {
        public int Number { get; }
        public int Generation { get; }
        public PdfReference(int number, int generation) { Number = number; Generation = generation; }
    }

    public sealed class PdfStream : PdfObj
    {
        public PdfDictionary Dictionary { get; }
        /// <summary>The bytes exactly as stored (still encoded by /Filter).</summary>
        public byte[] Data { get; }
        public PdfStream(PdfDictionary dictionary, byte[] data) { Dictionary = dictionary; Data = data; }
    }

    // ------------------------------------------------------------------ lexer / parser

    internal sealed class PdfParser
    {
        private readonly byte[] _b;
        private readonly Func<PdfReference, PdfObj?>? _resolve;
        public int Pos;

        public PdfParser(byte[] bytes, int position, Func<PdfReference, PdfObj?>? resolve = null)
        {
            _b = bytes; Pos = position; _resolve = resolve;
        }

        public static bool IsWhite(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;
        public static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                                                   or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        public void SkipWhite()
        {
            while (Pos < _b.Length)
            {
                if (IsWhite(_b[Pos])) { Pos++; continue; }
                if (_b[Pos] == '%') { while (Pos < _b.Length && _b[Pos] != 10 && _b[Pos] != 13) Pos++; continue; }
                break;
            }
        }

        public bool AtKeyword(string word)
        {
            SkipWhite();
            if (Pos + word.Length > _b.Length) return false;
            for (var i = 0; i < word.Length; i++) if (_b[Pos + i] != word[i]) return false;
            var end = Pos + word.Length;
            return end >= _b.Length || IsWhite(_b[end]) || IsDelimiter(_b[end]);
        }

        public string ReadKeyword()
        {
            SkipWhite();
            var start = Pos;
            while (Pos < _b.Length && !IsWhite(_b[Pos]) && !IsDelimiter(_b[Pos])) Pos++;
            return Encoding.ASCII.GetString(_b, start, Pos - start);
        }

        public PdfObj ParseObject()
        {
            SkipWhite();
            if (Pos >= _b.Length) throw new PdfException("Unexpected end of the PDF data.");
            var c = _b[Pos];
            switch (c)
            {
                case (byte)'/': return ParseName();
                case (byte)'(': return ParseLiteralString();
                case (byte)'[':
                {
                    Pos++;
                    var array = new PdfArray();
                    while (true)
                    {
                        SkipWhite();
                        if (Pos >= _b.Length) throw new PdfException("Unterminated array.");
                        if (_b[Pos] == ']') { Pos++; return array; }
                        array.Items.Add(ParseObject());
                    }
                }
                case (byte)'<':
                    if (Pos + 1 < _b.Length && _b[Pos + 1] == '<') return ParseDictionaryOrStream();
                    return ParseHexString();
            }
            if (c is (byte)'+' or (byte)'-' or (byte)'.' || (c >= '0' && c <= '9'))
            {
                var first = ParseNumber();
                // "n g R"
                if (first.IsInteger && first.Value >= 0)
                {
                    var save = Pos;
                    SkipWhite();
                    if (Pos < _b.Length && _b[Pos] >= '0' && _b[Pos] <= '9')
                    {
                        var second = ParseNumber();
                        if (second.IsInteger && AtKeyword("R"))
                        {
                            Pos++;
                            return new PdfReference((int)first.AsLong, (int)second.AsLong);
                        }
                    }
                    Pos = save;
                }
                return first;
            }
            var word = ReadKeyword();
            return word switch
            {
                "true" => new PdfBool(true),
                "false" => new PdfBool(false),
                "null" => PdfNull.Instance,
                "" => throw new PdfException($"Unexpected character '{(char)c}' at offset {Pos}."),
                _ => PdfNull.Instance    // an unknown keyword inside an object: tolerate
            };
        }

        private PdfNumber ParseNumber()
        {
            var start = Pos;
            if (_b[Pos] is (byte)'+' or (byte)'-') Pos++;
            var isInt = true;
            while (Pos < _b.Length && ((_b[Pos] >= '0' && _b[Pos] <= '9') || _b[Pos] == '.'))
            {
                if (_b[Pos] == '.') isInt = false;
                Pos++;
            }
            var text = Encoding.ASCII.GetString(_b, start, Pos - start);
            if (text is "+" or "-" or "." or "") return new PdfNumber(0, true);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? new PdfNumber(v, isInt) : new PdfNumber(0, true);
        }

        private PdfName ParseName()
        {
            Pos++; // '/'
            var sb = new StringBuilder();
            while (Pos < _b.Length && !IsWhite(_b[Pos]) && !IsDelimiter(_b[Pos]))
            {
                if (_b[Pos] == '#' && Pos + 2 < _b.Length && IsHex(_b[Pos + 1]) && IsHex(_b[Pos + 2]))
                {
                    sb.Append((char)Convert.ToByte(Encoding.ASCII.GetString(_b, Pos + 1, 2), 16));
                    Pos += 3;
                }
                else sb.Append((char)_b[Pos++]);
            }
            return new PdfName(sb.ToString());
        }

        private static bool IsHex(byte c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private PdfString ParseLiteralString()
        {
            Pos++; // '('
            var output = new List<byte>();
            var depth = 1;
            while (Pos < _b.Length)
            {
                var c = _b[Pos++];
                if (c == '\\')
                {
                    if (Pos >= _b.Length) break;
                    var e = _b[Pos++];
                    switch (e)
                    {
                        case (byte)'n': output.Add(10); break;
                        case (byte)'r': output.Add(13); break;
                        case (byte)'t': output.Add(9); break;
                        case (byte)'b': output.Add(8); break;
                        case (byte)'f': output.Add(12); break;
                        case 13: if (Pos < _b.Length && _b[Pos] == 10) Pos++; break;
                        case 10: break;
                        default:
                            if (e >= '0' && e <= '7')
                            {
                                var v = e - '0';
                                for (var k = 0; k < 2 && Pos < _b.Length && _b[Pos] >= '0' && _b[Pos] <= '7'; k++) v = v * 8 + (_b[Pos++] - '0');
                                output.Add((byte)v);
                            }
                            else output.Add(e);
                            break;
                    }
                    continue;
                }
                if (c == '(') depth++;
                if (c == ')' && --depth == 0) break;
                output.Add(c);
            }
            return new PdfString(output.ToArray());
        }

        private PdfString ParseHexString()
        {
            Pos++; // '<'
            var digits = new StringBuilder();
            while (Pos < _b.Length && _b[Pos] != '>')
            {
                if (IsHex(_b[Pos])) digits.Append((char)_b[Pos]);
                Pos++;
            }
            Pos++; // '>'
            if (digits.Length % 2 == 1) digits.Append('0');
            var bytes = new byte[digits.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(digits.ToString(i * 2, 2), 16);
            return new PdfString(bytes);
        }

        private PdfObj ParseDictionaryOrStream()
        {
            Pos += 2; // '<<'
            var dict = new PdfDictionary();
            while (true)
            {
                SkipWhite();
                if (Pos >= _b.Length) throw new PdfException("Unterminated dictionary.");
                if (_b[Pos] == '>' && Pos + 1 < _b.Length && _b[Pos + 1] == '>') { Pos += 2; break; }
                if (_b[Pos] != '/') { ParseObject(); continue; }   // junk: skip it
                var key = ParseName().Value;
                SkipWhite();
                if (Pos < _b.Length && _b[Pos] == '>' && Pos + 1 < _b.Length && _b[Pos + 1] == '>') { dict[key] = PdfNull.Instance; continue; }
                dict[key] = ParseObject();
            }

            var afterDict = Pos;
            if (!AtKeyword("stream")) { Pos = afterDict; return dict; }
            Pos += "stream".Length;
            if (Pos < _b.Length && _b[Pos] == 13) Pos++;
            if (Pos < _b.Length && _b[Pos] == 10) Pos++;
            var dataStart = Pos;

            var length = -1L;
            var lengthObj = dict["Length"];
            if (lengthObj is PdfReference r && _resolve != null) lengthObj = _resolve(r);
            if (lengthObj is PdfNumber n) length = n.AsLong;

            if (length >= 0 && dataStart + length <= _b.Length && EndstreamAt((int)(dataStart + length)))
            {
                Pos = (int)(dataStart + length);
            }
            else
            {
                // /Length missing or wrong: find "endstream"
                var idx = IndexOf(_b, "endstream"u8.ToArray(), dataStart);
                if (idx < 0) throw new PdfException("A stream has no end marker.");
                var end = idx;
                if (end > dataStart && _b[end - 1] == 10) end--;
                if (end > dataStart && _b[end - 1] == 13) end--;
                length = end - dataStart;
                Pos = idx;
            }
            var data = new byte[length];
            Buffer.BlockCopy(_b, dataStart, data, 0, (int)length);
            SkipWhite();
            if (AtKeyword("endstream")) Pos += "endstream".Length;
            return new PdfStream(dict, data);
        }

        private bool EndstreamAt(int position)
        {
            var save = Pos;
            Pos = position;
            var ok = AtKeyword("endstream");
            Pos = save;
            return ok;
        }

        public static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            for (var i = Math.Max(start, 0); i <= haystack.Length - needle.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { match = false; break; }
                if (match) return i;
            }
            return -1;
        }

        public static int LastIndexOf(byte[] haystack, byte[] needle, int from)
        {
            for (var i = Math.Min(from, haystack.Length - needle.Length); i >= 0; i--)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { match = false; break; }
                if (match) return i;
            }
            return -1;
        }
    }

    // ------------------------------------------------------------------ filters

    internal static class PdfFilters
    {
        public static byte[] Decode(PdfStream stream, Func<PdfObj?, PdfObj?> resolve)
        {
            var filter = resolve(stream.Dictionary["Filter"]);
            var parms = resolve(stream.Dictionary["DecodeParms"]);
            var filters = filter switch
            {
                null => new List<PdfObj>(),
                PdfArray a => a.Items.Select(i => resolve(i)!).ToList(),
                _ => new List<PdfObj> { filter }
            };
            var parmList = parms switch
            {
                PdfArray a => a.Items.Select(i => resolve(i)).ToList(),
                null => new List<PdfObj?>(),
                _ => new List<PdfObj?> { parms }
            };
            var data = stream.Data;
            for (var i = 0; i < filters.Count; i++)
            {
                var name = (filters[i] as PdfName)?.Value;
                if (name is "FlateDecode" or "Fl")
                {
                    data = Inflate(data);
                    if (i < parmList.Count && parmList[i] is PdfDictionary p) data = Unpredict(data, p, resolve);
                }
                else throw new PdfException($"The PDF uses the /{name} filter for internal structures, which this reader does not support.");
            }
            return data;
        }

        public static byte[] Inflate(byte[] data)
        {
            try
            {
                using var input = new MemoryStream(data);
                using var z = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                z.CopyTo(output);
                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                // some producers write raw deflate without the zlib header
                using var input = new MemoryStream(data);
                using var z = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                z.CopyTo(output);
                return output.ToArray();
            }
        }

        public static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
            return output.ToArray();
        }

        private static byte[] Unpredict(byte[] data, PdfDictionary parms, Func<PdfObj?, PdfObj?> resolve)
        {
            var predictor = (resolve(parms["Predictor"]) as PdfNumber)?.AsLong ?? 1;
            if (predictor < 10) return predictor == 1 ? data : throw new PdfException("TIFF predictors are not supported.");
            var columns = (int)((resolve(parms["Columns"]) as PdfNumber)?.AsLong ?? 1);
            var colors = (int)((resolve(parms["Colors"]) as PdfNumber)?.AsLong ?? 1);
            var bits = (int)((resolve(parms["BitsPerComponent"]) as PdfNumber)?.AsLong ?? 8);
            var bpp = Math.Max(1, colors * bits / 8);
            var rowLength = (columns * colors * bits + 7) / 8;
            var output = new List<byte>(data.Length);
            var previous = new byte[rowLength];
            for (var pos = 0; pos + 1 <= data.Length;)
            {
                var type = data[pos++];
                var row = new byte[rowLength];
                var n = Math.Min(rowLength, data.Length - pos);
                Buffer.BlockCopy(data, pos, row, 0, n);
                pos += rowLength;
                for (var i = 0; i < rowLength; i++)
                {
                    var left = i >= bpp ? row[i - bpp] : 0;
                    var up = previous[i];
                    var upLeft = i >= bpp ? previous[i - bpp] : 0;
                    row[i] = type switch
                    {
                        0 => row[i],
                        1 => (byte)(row[i] + left),
                        2 => (byte)(row[i] + up),
                        3 => (byte)(row[i] + ((left + up) >> 1)),
                        4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                        _ => row[i]
                    };
                }
                output.AddRange(row);
                previous = row;
            }
            return output.ToArray();
        }

        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    // ------------------------------------------------------------------ the document (read side)

    public sealed class PdfReader
    {
        private readonly record struct XrefEntry(int Type, long Offset, int Generation, int StreamNumber, int StreamIndex);

        private readonly Dictionary<int, XrefEntry> _xref = new();
        private readonly Dictionary<int, PdfObj> _cache = new();
        private readonly Dictionary<int, (byte[] Data, int First, int[] Numbers, int[] Offsets)> _objectStreams = new();

        public byte[] Bytes { get; }
        public PdfDictionary Trailer { get; private set; } = new();
        public long LastXrefOffset { get; private set; }
        public bool LastXrefIsStream { get; private set; }
        public bool WasRepaired { get; private set; }
        public int Size { get; private set; }

        public PdfReader(byte[] bytes)
        {
            Bytes = bytes;
            if (PdfParser.IndexOf(bytes, "%PDF-"u8.ToArray(), 0) is < 0 or > 1024)
                throw new PdfException("The file is not a PDF.");
            try
            {
                ReadCrossReference();
                if (Trailer["Root"] == null) throw new PdfException("no /Root");
            }
            catch (Exception ex) when (ex is PdfException or IndexOutOfRangeException or ArgumentException or InvalidDataException)
            {
                Rebuild();
            }
            if (Trailer["Encrypt"] != null)
                throw new PdfException("The PDF is encrypted (password protected). Remove the protection before it is used as a controlled document.");
            Size = (int)Math.Max((Resolve(Trailer["Size"]) as PdfNumber)?.AsLong ?? 0, _xref.Count == 0 ? 1 : _xref.Keys.Max() + 1);
        }

        private void ReadCrossReference()
        {
            var tail = PdfParser.LastIndexOf(Bytes, "startxref"u8.ToArray(), Bytes.Length - 9);
            if (tail < 0) throw new PdfException("no startxref");
            var p = new PdfParser(Bytes, tail + 9);
            var offset = ((PdfNumber)p.ParseObject()).AsLong;
            LastXrefOffset = offset;
            var visited = new HashSet<long>();
            var first = true;
            while (offset > 0 && offset < Bytes.Length && visited.Add(offset))
            {
                var parser = new PdfParser(Bytes, (int)offset);
                PdfDictionary trailer;
                if (parser.AtKeyword("xref"))
                {
                    if (first) LastXrefIsStream = false;
                    trailer = ReadXrefTable(parser);
                    if (Resolve(trailer["XRefStm"]) is PdfNumber stm) ReadXrefStreamAt(stm.AsLong);
                }
                else
                {
                    if (first) LastXrefIsStream = true;
                    trailer = ReadXrefStreamAt(offset);
                }
                if (first) { Trailer = trailer; first = false; }
                offset = (trailer["Prev"] as PdfNumber)?.AsLong ?? 0;
            }
        }

        private PdfDictionary ReadXrefTable(PdfParser p)
        {
            p.ReadKeyword(); // xref
            while (true)
            {
                if (p.AtKeyword("trailer")) { p.ReadKeyword(); break; }
                var start = (int)((PdfNumber)p.ParseObject()).AsLong;
                var count = (int)((PdfNumber)p.ParseObject()).AsLong;
                for (var i = 0; i < count; i++)
                {
                    var off = ((PdfNumber)p.ParseObject()).AsLong;
                    var gen = (int)((PdfNumber)p.ParseObject()).AsLong;
                    var type = p.ReadKeyword();
                    var num = start + i;
                    if (!_xref.ContainsKey(num))
                        _xref[num] = type == "n" ? new XrefEntry(1, off, gen, 0, 0) : new XrefEntry(0, 0, gen, 0, 0);
                }
            }
            return (PdfDictionary)p.ParseObject();
        }

        private PdfDictionary ReadXrefStreamAt(long offset)
        {
            var p = new PdfParser(Bytes, (int)offset, r => Resolve(r));
            p.ParseObject(); p.ParseObject(); p.ReadKeyword(); // n g obj
            if (p.ParseObject() is not PdfStream stream) throw new PdfException("The cross-reference stream is damaged.");
            var dict = stream.Dictionary;
            var data = PdfFilters.Decode(stream, o => Resolve(o));
            var w = ((PdfArray)dict["W"]!).Items.Select(i => (int)((PdfNumber)i).AsLong).ToArray();
            var size = (int)((PdfNumber)dict["Size"]!).AsLong;
            var index = dict["Index"] is PdfArray ix ? ix.Items.Select(i => (int)((PdfNumber)i).AsLong).ToArray() : new[] { 0, size };
            var entryLength = w.Sum();
            var pos = 0;
            for (var k = 0; k + 1 < index.Length; k += 2)
            {
                for (var i = 0; i < index[k + 1] && pos + entryLength <= data.Length; i++)
                {
                    long f1 = w[0] == 0 ? 1 : Field(data, pos, w[0]);
                    var f2 = Field(data, pos + w[0], w[1]);
                    var f3 = Field(data, pos + w[0] + w[1], w[2]);
                    pos += entryLength;
                    var num = index[k] + i;
                    if (_xref.ContainsKey(num)) continue;
                    _xref[num] = f1 switch
                    {
                        1 => new XrefEntry(1, f2, (int)f3, 0, 0),
                        2 => new XrefEntry(2, 0, 0, (int)f2, (int)f3),
                        _ => new XrefEntry(0, 0, 0, 0, 0)
                    };
                }
            }
            return dict;
        }

        private static long Field(byte[] data, int pos, int width)
        {
            long v = 0;
            for (var i = 0; i < width; i++) v = (v << 8) | data[pos + i];
            return v;
        }

        /// <summary>The cross-reference is unusable: find every "n g obj" and the trailer by scanning.</summary>
        private void Rebuild()
        {
            WasRepaired = true;
            _xref.Clear(); _cache.Clear();
            LastXrefIsStream = false;
            PdfDictionary? trailer = null;
            for (var i = 0; i < Bytes.Length - 4; i++)
            {
                if (Bytes[i] != 'o' || Bytes[i + 1] != 'b' || Bytes[i + 2] != 'j') continue;
                // walk back over "num gen "
                var j = i - 1;
                while (j >= 0 && PdfParser.IsWhite(Bytes[j])) j--;
                var genEnd = j;
                while (j >= 0 && Bytes[j] >= '0' && Bytes[j] <= '9') j--;
                if (j == genEnd) continue;
                var gen = int.Parse(Encoding.ASCII.GetString(Bytes, j + 1, genEnd - j));
                while (j >= 0 && PdfParser.IsWhite(Bytes[j])) j--;
                var numEnd = j;
                while (j >= 0 && Bytes[j] >= '0' && Bytes[j] <= '9') j--;
                if (j == numEnd) continue;
                if (!int.TryParse(Encoding.ASCII.GetString(Bytes, j + 1, numEnd - j), out var num)) continue;
                _xref[num] = new XrefEntry(1, j + 1, gen, 0, 0);   // later definitions win
            }
            var t = PdfParser.LastIndexOf(Bytes, "trailer"u8.ToArray(), Bytes.Length - 7);
            if (t >= 0)
            {
                try { trailer = (PdfDictionary)new PdfParser(Bytes, t + 7).ParseObject(); } catch (PdfException) { trailer = null; }
            }
            if (trailer?["Root"] == null)
            {
                // a file with only cross-reference streams: find a catalog
                foreach (var num in _xref.Keys.ToList())
                {
                    if (GetObject(num) is PdfDictionary d && (d["Type"] as PdfName)?.Value == "Catalog")
                    {
                        trailer ??= new PdfDictionary();
                        trailer["Root"] = new PdfReference(num, _xref[num].Generation);
                    }
                }
            }
            if (trailer?["Root"] == null) throw new PdfException("The PDF is damaged: its catalog cannot be found.");
            trailer["Prev"] = null; trailer["XRefStm"] = null;
            Trailer = trailer;
            LastXrefOffset = 0;
        }

        public IEnumerable<int> ObjectNumbers => _xref.Where(e => e.Value.Type != 0).Select(e => e.Key).OrderBy(k => k);

        public int GenerationOf(int number) => _xref.TryGetValue(number, out var e) && e.Type == 1 ? e.Generation : 0;

        public PdfObj? GetObject(int number)
        {
            if (_cache.TryGetValue(number, out var cached)) return cached;
            if (!_xref.TryGetValue(number, out var e) || e.Type == 0) return null;
            PdfObj? obj = null;
            try
            {
                if (e.Type == 1)
                {
                    var p = new PdfParser(Bytes, (int)e.Offset, r => Resolve(r));
                    p.ParseObject(); p.ParseObject();
                    if (p.ReadKeyword() != "obj") return null;
                    obj = p.ParseObject();
                }
                else
                {
                    var os = LoadObjectStream(e.StreamNumber);
                    var idx = Array.IndexOf(os.Numbers, number);
                    if (idx < 0) idx = e.StreamIndex;
                    if (idx < 0 || idx >= os.Offsets.Length) return null;
                    obj = new PdfParser(os.Data, os.First + os.Offsets[idx], r => Resolve(r)).ParseObject();
                }
            }
            catch (PdfException)
            {
                obj = null;
            }
            if (obj != null) _cache[number] = obj;
            return obj;
        }

        private (byte[] Data, int First, int[] Numbers, int[] Offsets) LoadObjectStream(int number)
        {
            if (_objectStreams.TryGetValue(number, out var os)) return os;
            if (GetObject(number) is not PdfStream stream) throw new PdfException("An object stream is missing.");
            var data = PdfFilters.Decode(stream, o => Resolve(o));
            var n = (int)((PdfNumber)Resolve(stream.Dictionary["N"])!).AsLong;
            var first = (int)((PdfNumber)Resolve(stream.Dictionary["First"])!).AsLong;
            var p = new PdfParser(data, 0);
            var numbers = new int[n]; var offsets = new int[n];
            for (var i = 0; i < n; i++)
            {
                numbers[i] = (int)((PdfNumber)p.ParseObject()).AsLong;
                offsets[i] = (int)((PdfNumber)p.ParseObject()).AsLong;
            }
            os = (data, first, numbers, offsets);
            _objectStreams[number] = os;
            return os;
        }

        public PdfObj? Resolve(PdfObj? obj)
        {
            var guard = 0;
            while (obj is PdfReference r && guard++ < 32) obj = GetObject(r.Number);
            return obj is PdfNull ? null : obj;
        }
    }

    // ------------------------------------------------------------------ the incremental writer

    public sealed class PdfIncrementalWriter
    {
        private readonly PdfReader _reader;
        private readonly List<(int Number, int Generation, PdfObj Obj)> _objects = new();
        private int _next;

        public PdfIncrementalWriter(PdfReader reader)
        {
            _reader = reader;
            _next = reader.Size;
        }

        public PdfReference Add(PdfObj obj)
        {
            var reference = new PdfReference(_next++, 0);
            _objects.Add((reference.Number, 0, obj));
            return reference;
        }

        public void Replace(PdfReference reference, PdfObj obj)
        {
            _objects.RemoveAll(o => o.Number == reference.Number);
            _objects.Add((reference.Number, reference.Generation, obj));
        }

        public byte[] Save()
        {
            if (_reader.WasRepaired) return SaveComplete();
            using var output = new MemoryStream();
            output.Write(_reader.Bytes);
            if (_reader.Bytes.Length > 0 && _reader.Bytes[^1] != 10 && _reader.Bytes[^1] != 13) output.WriteByte(10);

            var offsets = new Dictionary<int, (long Offset, int Generation)>();
            foreach (var (number, generation, obj) in _objects.OrderBy(o => o.Number))
            {
                offsets[number] = (output.Position, generation);
                WriteAscii(output, $"{number} {generation} obj\n");
                PdfSerializer.Write(output, obj);
                WriteAscii(output, "\nendobj\n");
            }

            var trailer = new PdfDictionary();
            foreach (var key in new[] { "Root", "Info", "ID" })
                if (_reader.Trailer[key] is { } v) trailer[key] = v;

            if (_reader.LastXrefIsStream && !_reader.WasRepaired)
            {
                var xrefNumber = _next++;
                var xrefOffset = output.Position;
                offsets[xrefNumber] = (xrefOffset, 0);
                var numbers = offsets.Keys.OrderBy(k => k).ToList();
                var data = new MemoryStream();
                foreach (var n in numbers)
                {
                    var (off, gen) = offsets[n];
                    data.WriteByte(1);
                    data.WriteByte((byte)(off >> 24)); data.WriteByte((byte)(off >> 16)); data.WriteByte((byte)(off >> 8)); data.WriteByte((byte)off);
                    data.WriteByte((byte)(gen >> 8)); data.WriteByte((byte)gen);
                }
                var dict = trailer.Clone();
                dict["Type"] = new PdfName("XRef");
                dict["Size"] = new PdfNumber(_next);
                dict["W"] = new PdfArray(new PdfObj[] { new PdfNumber(1), new PdfNumber(4), new PdfNumber(2) });
                dict["Index"] = new PdfArray(Ranges(numbers).SelectMany(r => new PdfObj[] { new PdfNumber(r.Start), new PdfNumber(r.Count) }));
                if (_reader.LastXrefOffset > 0) dict["Prev"] = new PdfNumber(_reader.LastXrefOffset);
                WriteAscii(output, $"{xrefNumber} 0 obj\n");
                PdfSerializer.Write(output, new PdfStream(dict, data.ToArray()));
                WriteAscii(output, "\nendobj\n");
                WriteAscii(output, $"startxref\n{xrefOffset}\n%%EOF\n");
            }
            else
            {
                var xrefOffset = output.Position;
                var sb = new StringBuilder("xref\n");
                var numbers = offsets.Keys.OrderBy(k => k).ToList();
                foreach (var (start, count) in Ranges(numbers))
                {
                    sb.Append(start).Append(' ').Append(count).Append('\n');
                    for (var n = start; n < start + count; n++)
                        sb.Append(offsets[n].Offset.ToString("D10")).Append(' ').Append(offsets[n].Generation.ToString("D5")).Append(" n\r\n");
                }
                WriteAscii(output, sb.ToString());
                trailer["Size"] = new PdfNumber(_next);
                if (_reader.LastXrefOffset > 0) trailer["Prev"] = new PdfNumber(_reader.LastXrefOffset);
                WriteAscii(output, "trailer\n");
                PdfSerializer.Write(output, trailer);
                WriteAscii(output, $"\nstartxref\n{xrefOffset}\n%%EOF\n");
            }
            return output.ToArray();
        }

        /// <summary>A damaged source (no usable cross-reference): every object is written again into a clean file.</summary>
        private byte[] SaveComplete()
        {
            using var output = new MemoryStream();
            output.Write(Encoding.Latin1.GetBytes("%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n"));
            var replaced = _objects.ToDictionary(o => o.Number);
            var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
            var numbers = _reader.ObjectNumbers.Union(replaced.Keys).Distinct().OrderBy(n => n).ToList();
            foreach (var number in numbers)
            {
                PdfObj? obj; int generation;
                if (replaced.TryGetValue(number, out var r)) { obj = r.Obj; generation = r.Generation; }
                else
                {
                    obj = _reader.GetObject(number); generation = _reader.GenerationOf(number);
                    if (obj is PdfStream st && (st.Dictionary["Type"] as PdfName)?.Value is "ObjStm" or "XRef") continue;
                }
                if (obj == null) continue;
                offsets[number] = (output.Position, generation);
                WriteAscii(output, $"{number} {generation} obj\n");
                PdfSerializer.Write(output, obj);
                WriteAscii(output, "\nendobj\n");
            }
            var xrefOffset = output.Position;
            var size = Math.Max(_next, offsets.Count == 0 ? 1 : offsets.Keys.Max() + 1);
            var sb = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f\r\n");
            for (var n = 1; n < size; n++)
                sb.Append(offsets.TryGetValue(n, out var e) ? $"{e.Offset:D10} {e.Generation:D5} n\r\n" : "0000000000 00000 f\r\n");
            WriteAscii(output, sb.ToString());
            var trailer = new PdfDictionary();
            foreach (var key in new[] { "Root", "Info", "ID" })
                if (_reader.Trailer[key] is { } v) trailer[key] = v;
            trailer["Size"] = new PdfNumber(size);
            WriteAscii(output, "trailer\n");
            PdfSerializer.Write(output, trailer);
            WriteAscii(output, $"\nstartxref\n{xrefOffset}\n%%EOF\n");
            return output.ToArray();
        }

        private static IEnumerable<(int Start, int Count)> Ranges(List<int> sorted)
        {
            for (var i = 0; i < sorted.Count;)
            {
                var start = sorted[i];
                var j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                yield return (start, j - i + 1);
                i = j + 1;
            }
        }

        private static void WriteAscii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
    }

    public static class PdfSerializer
    {
        public static void Write(Stream s, PdfObj obj)
        {
            switch (obj)
            {
                case PdfNull: Ascii(s, "null"); break;
                case PdfBool b: Ascii(s, b.Value ? "true" : "false"); break;
                case PdfNumber n:
                    Ascii(s, n.IsInteger ? n.AsLong.ToString(CultureInfo.InvariantCulture)
                                         : n.Value.ToString("0.#####", CultureInfo.InvariantCulture));
                    break;
                case PdfString str:
                    s.WriteByte((byte)'<');
                    Ascii(s, Convert.ToHexString(str.Value));
                    s.WriteByte((byte)'>');
                    break;
                case PdfName name: WriteName(s, name.Value); break;
                case PdfReference r: Ascii(s, $"{r.Number} {r.Generation} R"); break;
                case PdfArray a:
                    s.WriteByte((byte)'[');
                    for (var i = 0; i < a.Items.Count; i++) { if (i > 0) s.WriteByte((byte)' '); Write(s, a.Items[i]); }
                    s.WriteByte((byte)']');
                    break;
                case PdfDictionary d:
                    Ascii(s, "<<");
                    foreach (var (key, value) in d.Entries) { WriteName(s, key); s.WriteByte((byte)' '); Write(s, value); s.WriteByte((byte)'\n'); }
                    Ascii(s, ">>");
                    break;
                case PdfStream st:
                    var dict = st.Dictionary.Clone();
                    dict["Length"] = new PdfNumber(st.Data.Length);
                    Write(s, dict);
                    Ascii(s, "\nstream\n");
                    s.Write(st.Data);
                    Ascii(s, "\nendstream");
                    break;
            }
        }

        private static void WriteName(Stream s, string name)
        {
            s.WriteByte((byte)'/');
            foreach (var ch in name)
            {
                var c = (byte)ch;
                if (c < 0x21 || c > 0x7E || c == '#' || PdfParser.IsDelimiter(c)) Ascii(s, "#" + c.ToString("X2"));
                else s.WriteByte(c);
            }
        }

        private static void Ascii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
    }
}
