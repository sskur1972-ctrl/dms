//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Globalization;
using System.Net;
using System.Text;

namespace DMS.Web.Services.Authoring;

/// <summary>
/// Whitelist sanitiser for document bodies posted by the in-application editor
/// (URS-7.1).
///
/// <para><b>Why this class is written the way it is</b></para>
/// The editor is a <c>contenteditable</c> surface in the author's browser. What
/// arrives at the server is whatever that browser - or anything pretending to be
/// it - chose to send, and it is then stored, re-displayed to every reviewer and
/// approver, and rendered into the controlled PDF. That makes it the highest
/// value stored-XSS target in the application: a payload planted by an author
/// would execute in the session of the QA controller who approves the document.
///
/// <para>
/// Three deliberate decisions follow from that:
/// </para>
/// <list type="number">
/// <item>
/// <b>Whitelist, never blacklist.</b> Anything not explicitly permitted is
/// removed. New browser features cannot open a hole in a list that names what
/// is allowed.
/// </item>
/// <item>
/// <b>A real tokenizer, not regular expressions.</b> HTML is not a regular
/// language and every "strip tags with a regex" filter ever written has been
/// bypassed - by malformed tags, by attributes containing <c>&gt;</c>, by
/// entity-encoded angle brackets, by nested constructs. This walks the input
/// character by character with an explicit state machine and re-serialises from
/// its own model, so the output contains only markup this class wrote itself.
/// </item>
/// <item>
/// <b>Text is decoded and re-encoded.</b> Every text node is HTML-decoded and
/// then re-encoded on the way out, so a payload smuggled as an entity cannot
/// survive as markup.
/// </item>
/// </list>
///
/// <para>
/// The result is not "the input with bad bits removed"; it is a fresh document
/// built from the parts of the input that were recognised. That is a stronger
/// property, and it is the one a GxP system needs, because the sanitised body is
/// the thing that becomes the controlled record.
/// </para>
/// </summary>
public sealed class EditorHtmlSanitizer : IEditorHtmlSanitizer
{
    // -------------------------------------------------------------------------
    // The whitelist
    // -------------------------------------------------------------------------

    /// <summary>Elements that survive, with the attributes each may carry.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> AllowedElements =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["p"] = new[] { "style", "class" },
            ["div"] = new[] { "style", "class" },
            ["span"] = new[] { "style", "class" },
            ["br"] = Array.Empty<string>(),
            ["hr"] = new[] { "style", "class" },
            ["h1"] = new[] { "style", "class" },
            ["h2"] = new[] { "style", "class" },
            ["h3"] = new[] { "style", "class" },
            ["h4"] = new[] { "style", "class" },
            ["h5"] = new[] { "style", "class" },
            ["h6"] = new[] { "style", "class" },
            ["strong"] = new[] { "style" },
            ["b"] = new[] { "style" },
            ["em"] = new[] { "style" },
            ["i"] = new[] { "style" },
            ["u"] = new[] { "style" },
            ["s"] = new[] { "style" },
            ["strike"] = Array.Empty<string>(),
            ["sub"] = Array.Empty<string>(),
            ["sup"] = Array.Empty<string>(),
            ["blockquote"] = new[] { "style", "class" },
            ["pre"] = new[] { "style", "class" },
            ["code"] = Array.Empty<string>(),
            ["ul"] = new[] { "style", "class" },
            ["ol"] = new[] { "style", "class", "start" },
            ["li"] = new[] { "style", "class" },
            ["table"] = new[] { "style", "class" },
            ["thead"] = new[] { "style", "class" },
            ["tbody"] = new[] { "style", "class" },
            ["tfoot"] = new[] { "style", "class" },
            ["tr"] = new[] { "style", "class" },
            ["td"] = new[] { "style", "class", "colspan", "rowspan" },
            ["th"] = new[] { "style", "class", "colspan", "rowspan" },
            ["caption"] = new[] { "style", "class" },
            ["colgroup"] = new[] { "style" },
            ["col"] = new[] { "style", "span" },
            ["img"] = new[] { "src", "alt", "style", "class", "width", "height" },
            ["a"] = new[] { "href", "title", "target", "rel" },
            ["figure"] = new[] { "style", "class" },
            ["figcaption"] = new[] { "style", "class" }
        };

    /// <summary>Elements written as self-closing; they never have an end tag.</summary>
    private static readonly HashSet<string> VoidElements =
        new(StringComparer.Ordinal) { "br", "hr", "img", "col" };

    /// <summary>
    /// Elements whose CONTENT is discarded along with the tag. Everything else
    /// that is not on the whitelist is "unwrapped": the tag goes, the text
    /// inside it stays, because a stray &lt;font&gt; from a paste should not
    /// silently delete a paragraph of an SOP.
    /// </summary>
    private static readonly HashSet<string> DropWithContent = new(StringComparer.Ordinal)
    {
        "script", "style", "link", "meta", "base", "title", "head", "noscript",
        "iframe", "frame", "frameset", "object", "embed", "applet", "portal",
        "form", "input", "button", "select", "option", "optgroup", "textarea",
        "fieldset", "legend", "label", "output", "progress", "meter", "datalist",
        "svg", "math", "template", "slot", "canvas", "audio", "video", "source",
        "track", "map", "area", "dialog", "marquee", "xml"
    };

    /// <summary>
    /// CSS properties an author may set. Anything else in a style attribute is
    /// dropped. Note what is absent: position, z-index, transform, content,
    /// behavior, and everything else that could be used to overlay or obscure
    /// the rest of a page the content is displayed on.
    /// </summary>
    private static readonly HashSet<string> AllowedCssProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "text-align", "font-weight", "font-style", "text-decoration", "text-decoration-line",
        "color", "background-color", "font-family", "font-size", "line-height",
        "vertical-align", "text-indent", "margin-left", "margin-right", "margin-top", "margin-bottom",
        "padding", "padding-left", "padding-right", "padding-top", "padding-bottom",
        "width", "height", "min-width", "max-width", "border", "border-top", "border-right",
        "border-bottom", "border-left", "border-collapse", "border-color", "border-style",
        "border-width", "list-style-type", "white-space", "page-break-before", "page-break-after",
        "break-before", "break-after"
    };

    /// <summary>
    /// The only class names that survive. Free-form classes are not permitted:
    /// a class is a hook into the application's own stylesheet, and letting
    /// document content choose application classes lets it borrow application
    /// chrome - a document that renders its own fake "Approved" badge, for
    /// instance.
    /// </summary>
    private static readonly HashSet<string> AllowedClasses = new(StringComparer.Ordinal)
    {
        // Structure
        "dms-doc-table", "dms-doc-borderless", "dms-doc-figure", "dms-doc-note",
        "dms-doc-pagebreak", "dms-doc-toc", "dms-doc-header", "dms-doc-footer",

        // Generated content (URS-7.36.2 auto index, and automatic heading
        // numbering). These carry the index entries and the section numbers the
        // editor computes, and the writer turns them into a real Word TOC field
        // and real numbered heading text. They are whitelisted so that the
        // generated structure survives sanitising - without them the index
        // would arrive as a list of unstyled paragraphs.
        "dms-doc-toc-entry", "dms-doc-toc-text", "dms-doc-toc-page",
        "dms-doc-toc-l1", "dms-doc-toc-l2", "dms-doc-toc-l3", "dms-doc-toc-l4",
        "dms-doc-toc-title", "dms-doc-autonum",

        // Named styles, each of which maps to a real Word style in the export -
        // see HtmlDocxWriter.StyleIdForClasses. They are classes rather than
        // inline formatting precisely so that the exported document carries the
        // STYLE and can be restyled in Word, instead of carrying a paragraph
        // that merely looks like a title.
        "dms-doc-title", "dms-doc-subtitle", "dms-doc-caption", "dms-doc-quote",
        "dms-doc-nospacing", "dms-doc-legal",

        // Paragraph and run switches
        "dms-doc-center", "dms-doc-small", "dms-doc-keepnext", "dms-doc-smallcaps"
    };

    private static readonly HashSet<string> AllowedImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp"
    };

    /// <summary>
    /// Fonts whose characters live in the Unicode private-use area rather than
    /// meaning what their code points say.
    ///
    /// <para><b>Why these are stripped rather than honoured</b></para>
    /// This is the fix for a real, reported defect. Copying a bulleted list out
    /// of Microsoft Word does not put a list on the clipboard: it puts an
    /// indented paragraph whose "bullet" is a literal U+00B7 in the Symbol font,
    /// followed by exactly seven non-breaking spaces. The middle dot has a glyph
    /// in Symbol, so it draws as a bullet; U+00A0 does <b>not</b>, so Word draws
    /// seven empty boxes. A browser silently substitutes a font for the same
    /// characters, which is why the editor looked correct and the exported .docx
    /// did not - the single worst kind of defect, because the author cannot see
    /// it happening.
    /// </para>
    /// <para>
    /// Dropping the font declaration means those characters render as the
    /// characters they actually are, in the document's own font, in the editor
    /// and in the export alike. A run of text genuinely set in Wingdings is not
    /// something a controlled procedure needs, and it is not worth the class of
    /// defect it brings with it.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> SymbolFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "symbol", "wingdings", "wingdings 2", "wingdings 3", "webdings",
        "zapfdingbats", "zapf dingbats", "monotype sorts", "marlett", "bookshelf symbol 7",
        "ms outlook", "mt extra"
    };

    // -------------------------------------------------------------------------

    public EditorHtmlSanitizeResult Sanitize(string? html, EditorSanitizeLimits limits)
    {
        var result = new EditorHtmlSanitizeResult();

        if (string.IsNullOrWhiteSpace(html))
        {
            result.Html = string.Empty;
            return result;
        }

        // Guard the parser itself before it is asked to walk the input: a body
        // far past the configured ceiling is rejected, not trimmed. Trimming
        // half a document into a controlled record is worse than refusing it.
        var inputBytes = Encoding.UTF8.GetByteCount(html);
        if (limits.MaxContentBytes > 0 && inputBytes > limits.MaxContentBytes)
        {
            result.Rejected = true;
            result.RejectionReason =
                $"The document body is {inputBytes / 1024:N0} KB, which is larger than the " +
                $"{limits.MaxContentBytes / 1024:N0} KB limit configured in authoring.maxContentBytes.";
            return result;
        }

        var output = new StringBuilder(html.Length);
        var openTags = new List<string>();
        var writer = new BlockWriter(output);

        var i = 0;
        var length = html.Length;
        var skipDepth = 0;
        string? skipTag = null;

        while (i < length)
        {
            var c = html[i];

            if (c != '<')
            {
                // Text run up to the next '<'.
                var textEnd = html.IndexOf('<', i);
                if (textEnd < 0) textEnd = length;

                if (skipDepth == 0)
                {
                    var text = html[i..textEnd];
                    AppendText(writer, text);
                }

                i = textEnd;
                continue;
            }

            // Comments, doctypes and CDATA are removed outright. They carry no
            // authored content and are a classic parser-confusion vector.
            if (StartsWith(html, i, "<!--"))
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? length : end + 3;
                continue;
            }

            if (StartsWith(html, i, "<!") || StartsWith(html, i, "<?"))
            {
                var end = html.IndexOf('>', i);
                i = end < 0 ? length : end + 1;
                continue;
            }

            var isEnd = StartsWith(html, i, "</");
            var nameStart = i + (isEnd ? 2 : 1);

            if (nameStart >= length || !IsNameStart(html[nameStart]))
            {
                // A '<' that does not begin a tag is literal text. "a < b" must
                // survive as "a &lt; b" rather than swallowing the rest of the
                // paragraph.
                if (skipDepth == 0) AppendText(writer, "<");
                i++;
                continue;
            }

            var nameEnd = nameStart;
            while (nameEnd < length && IsNameChar(html[nameEnd])) nameEnd++;

            var tagName = html[nameStart..nameEnd].ToLowerInvariant();

            // Find the end of the tag, honouring quoted attribute values so that
            // a '>' inside an attribute does not terminate the tag early. This
            // is the single most common tokenizer bug that filters get wrong.
            var (tagEnd, selfClosing) = FindTagEnd(html, nameEnd);
            var attributeText = tagEnd > nameEnd ? html[nameEnd..tagEnd] : string.Empty;
            var next = tagEnd < length ? tagEnd + 1 : length;

            if (skipDepth > 0)
            {
                // Inside a dropped subtree: track nesting of the same element so
                // that "<script>...<script>...</script>...</script>" cannot end
                // the skip early, and ignore everything else.
                if (string.Equals(tagName, skipTag, StringComparison.Ordinal))
                {
                    if (isEnd) skipDepth--;
                    else if (!selfClosing) skipDepth++;
                    if (skipDepth == 0) skipTag = null;
                }

                i = next;
                continue;
            }

            if (DropWithContent.Contains(tagName))
            {
                if (!isEnd && !selfClosing)
                {
                    skipDepth = 1;
                    skipTag = tagName;
                    result.RemovedElements++;
                }
                else
                {
                    result.RemovedElements++;
                }

                i = next;
                continue;
            }

            if (!AllowedElements.ContainsKey(tagName))
            {
                // Unwrap: drop the tag, keep whatever is inside it.
                result.RemovedElements++;
                i = next;
                continue;
            }

            if (isEnd)
            {
                CloseTag(writer, openTags, tagName);
                i = next;
                continue;
            }

            var attributes = ParseAttributes(attributeText);
            var rendered = RenderAttributes(tagName, attributes, limits, result);

            if (rendered is null)
            {
                // The element itself was rejected (an image over the size limit,
                // for instance). Its children, if any, are still kept.
                i = next;
                continue;
            }

            if (VoidElements.Contains(tagName))
            {
                writer.Element(tagName, rendered, selfClose: true);
            }
            else
            {
                writer.Element(tagName, rendered, selfClose: false);
                openTags.Add(tagName);

                if (openTags.Count > 64)
                {
                    // A pathologically nested body is a denial-of-service shape,
                    // not authored content.
                    result.Rejected = true;
                    result.RejectionReason = "The document body is nested too deeply to be stored safely.";
                    return result;
                }
            }

            i = next;
        }

        // Close anything the author's browser left open, innermost first, so the
        // stored body is always balanced regardless of what arrived.
        for (var k = openTags.Count - 1; k >= 0; k--) writer.EndElement(openTags[k]);

        result.Html = output.ToString().Trim();

        // Counted where the images were actually decoded, so this is the exact
        // decoded size rather than an estimate recovered from the base64 text.
        result.TotalImageBytes = result.ImageBytesSeen;

        if (limits.MaxTotalImageBytes > 0 && result.TotalImageBytes > limits.MaxTotalImageBytes)
        {
            result.Rejected = true;
            result.RejectionReason =
                $"The embedded images total {result.TotalImageBytes / 1024:N0} KB, which is over the " +
                $"{limits.MaxTotalImageBytes / 1024:N0} KB limit configured in authoring.maxTotalImageBytes.";
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Tokenizer helpers
    // -------------------------------------------------------------------------

    private static bool StartsWith(string s, int index, string value)
        => index + value.Length <= s.Length && string.CompareOrdinal(s, index, value, 0, value.Length) == 0;

    private static bool IsNameStart(char c) => char.IsLetter(c);

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ':';

    /// <summary>
    /// Returns the index of the '&gt;' that closes the tag, and whether the tag
    /// was self-closed. Quoted attribute values are skipped whole.
    /// </summary>
    private static (int End, bool SelfClosing) FindTagEnd(string html, int from)
    {
        var i = from;
        var selfClosing = false;

        while (i < html.Length)
        {
            var c = html[i];

            if (c == '"' || c == '\'')
            {
                var close = html.IndexOf(c, i + 1);
                i = close < 0 ? html.Length : close + 1;
                continue;
            }

            if (c == '>')
            {
                // Look back past whitespace for a '/'.
                var j = i - 1;
                while (j >= from && char.IsWhiteSpace(html[j])) j--;
                selfClosing = j >= from && html[j] == '/';
                return (i, selfClosing);
            }

            i++;
        }

        return (html.Length, selfClosing);
    }

    private static void CloseTag(BlockWriter writer, List<string> openTags, string tagName)
    {
        // Find the nearest matching open tag. A stray end tag with no match is
        // ignored rather than closing something it did not open.
        var index = openTags.LastIndexOf(tagName);
        if (index < 0) return;

        for (var k = openTags.Count - 1; k >= index; k--)
        {
            writer.EndElement(openTags[k]);
            openTags.RemoveAt(k);
        }
    }

    private static void AppendText(BlockWriter writer, string raw)
    {
        if (raw.Length == 0) return;

        // Decode then re-encode: an entity-smuggled '<' becomes a literal '<'
        // and is written back as "&lt;", so it can never re-enter as markup.
        var decoded = WebUtility.HtmlDecode(raw);

        // Strip characters XML 1.0 does not permit. This matters downstream, not
        // here: the sanitised body is parsed as XML by the .docx writer, and one
        // stray control character from a paste would make the whole document
        // unwritable. Tab, newline and carriage return are kept.
        var clean = CollapseNonBreakingSpaceRuns(StripNonXmlCharacters(decoded));

        writer.Text(WebUtility.HtmlEncode(clean));
    }

    /// <summary>
    /// Collapses a run of two or more non-breaking spaces into one ordinary
    /// space.
    ///
    /// <para>
    /// This is the second half of the Word-paste fix described on
    /// <see cref="SymbolFonts"/>. Word's clipboard spells a list indent as seven
    /// consecutive U+00A0 characters; other applications use two or three. A
    /// single non-breaking space is left alone because it is meaningful - it is
    /// what keeps "10 mg" or "Table 4" from breaking across a line, and an SOP
    /// uses it deliberately. Two or more in a row is never deliberate; it is
    /// somebody's idea of layout, and it survives into the .docx as fixed-width
    /// padding that no longer matches the surrounding text.
    /// </para>
    /// <para>
    /// The collapse happens HERE, in the shared sanitiser, rather than in the
    /// .docx writer, for one reason that matters more than convenience: the
    /// editor displays the sanitised body. Fixing it only on export would leave
    /// the author looking at one document and shipping another, which is the
    /// exact complaint this change exists to answer.
    /// </para>
    /// </summary>
    internal static string CollapseNonBreakingSpaceRuns(string value)
    {
        const char nbsp = '\u00A0';

        if (value.IndexOf(nbsp) < 0) return value;

        var sb = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != nbsp) { sb.Append(value[i]); continue; }

            var start = i;
            while (i < value.Length && value[i] == nbsp) i++;
            var length = i - start;
            i--;

            if (length == 1) { sb.Append(nbsp); continue; }

            // The run stands in for a gap. Emit one ordinary space - unless
            // there is already a space on either side of it, in which case
            // emitting another would leave the double spacing the author
            // reported: the browser collapses it on screen and Word does not, so
            // the editor and the export would still disagree.
            var precededBySpace = sb.Length > 0 && sb[^1] == ' ';
            var followedBySpace = i + 1 < value.Length && value[i + 1] == ' ';

            if (!precededBySpace && !followedBySpace) sb.Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Keeps only characters legal in an XML 1.0 document, plus unpaired
    /// surrogates removed: those encode to numeric entities that no XML parser
    /// will read back.
    /// </summary>
    internal static string StripNonXmlCharacters(string value)
    {
        var needsWork = false;
        foreach (var c in value)
        {
            if (!IsLegalXmlChar(c) || char.IsSurrogate(c)) { needsWork = true; break; }
        }

        if (!needsWork) return value;

        var sb = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(c).Append(value[i + 1]);
                    i++;
                }
                continue;
            }

            if (char.IsLowSurrogate(c)) continue;
            if (IsLegalXmlChar(c)) sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsLegalXmlChar(char c)
        => c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;

        while (i < text.Length)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '/')) i++;
            if (i >= text.Length) break;

            var nameStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '=' && text[i] != '/') i++;
            if (i == nameStart) { i++; continue; }

            var name = text[nameStart..i];

            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

            var value = string.Empty;
            if (i < text.Length && text[i] == '=')
            {
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

                if (i < text.Length && (text[i] == '"' || text[i] == '\''))
                {
                    var quote = text[i++];
                    var valueStart = i;
                    while (i < text.Length && text[i] != quote) i++;
                    value = text[valueStart..i];
                    if (i < text.Length) i++;
                }
                else
                {
                    var valueStart = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                    value = text[valueStart..i];
                }
            }

            // A duplicate attribute keeps the FIRST occurrence, matching what a
            // browser does, so a filter and a browser cannot be made to disagree
            // about which value applies.
            if (!attributes.ContainsKey(name)) attributes[name] = WebUtility.HtmlDecode(value);
        }

        return attributes;
    }

    /// <summary>
    /// Builds the attribute string for one element, or null when the element as
    /// a whole must be dropped.
    /// </summary>
    private static string? RenderAttributes(string tagName, Dictionary<string, string> attributes,
        EditorSanitizeLimits limits, EditorHtmlSanitizeResult result)
    {
        var allowed = AllowedElements[tagName];
        var sb = new StringBuilder();

        foreach (var name in allowed)
        {
            if (!attributes.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;

            string? value = name switch
            {
                "style" => SanitizeStyle(raw),
                "class" => SanitizeClass(raw),
                "src" when tagName == "img" => SanitizeImageSource(raw, limits, result),
                "href" when tagName == "a" => SanitizeHref(raw),
                "colspan" or "rowspan" or "span" or "start" => SanitizeSmallInteger(raw),
                "width" or "height" => SanitizeSmallInteger(raw),
                "alt" or "title" => Truncate(raw, 512),
                "target" => raw == "_blank" ? "_blank" : null,
                "rel" => null, // written below, never taken from the input
                _ => null
            };

            if (value is null) continue;

            sb.Append(' ').Append(name).Append("=\"")
              .Append(WebUtility.HtmlEncode(StripNonXmlCharacters(value))).Append('"');
        }

        if (tagName == "img" && !attributes.ContainsKey("src")) return null;

        if (tagName == "img")
        {
            // An image whose source did not survive validation must not be
            // written as a src-less <img>: that renders as a broken-image icon
            // in a controlled document.
            if (sb.ToString().IndexOf(" src=\"", StringComparison.Ordinal) < 0)
            {
                result.RemovedElements++;
                return null;
            }
        }

        if (tagName == "a")
        {
            if (sb.ToString().IndexOf(" href=\"", StringComparison.Ordinal) < 0) return null;

            // Written by us, not taken from the document: a target=_blank link
            // without noopener hands the opened page a handle on this one.
            sb.Append(" rel=\"noopener noreferrer\"");
        }

        return sb.ToString();
    }

    private static string? SanitizeClass(string raw)
    {
        var kept = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(AllowedClasses.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return kept.Length == 0 ? null : string.Join(' ', kept);
    }

    private static string? SanitizeSmallInteger(string raw)
    {
        var digits = new string(raw.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return null;
        if (value < 1 || value > 10000) return null;
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string? Truncate(string raw, int max)
    {
        var clean = new string(raw.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length <= max ? clean : clean[..max];
    }

    /// <summary>
    /// http, https and mailto only. Absent from that list, and deliberately so:
    /// javascript:, data:, vbscript:, file: and every scheme-relative form.
    /// </summary>
    private static string? SanitizeHref(string raw)
    {
        var value = raw.Trim();

        // Strip characters a browser ignores when resolving a scheme, so
        // "java\tscript:alert(1)" cannot slip past a prefix comparison.
        var collapsed = new string(value.Where(c => !char.IsControl(c) && c != ' ').ToArray()).Trim();
        if (collapsed.Length == 0 || collapsed.Length > 2048) return null;

        if (collapsed.StartsWith("/", StringComparison.Ordinal) &&
            !collapsed.StartsWith("//", StringComparison.Ordinal))
        {
            // A link within this application.
            return collapsed;
        }

        if (!Uri.TryCreate(collapsed, UriKind.Absolute, out var uri)) return null;

        return uri.Scheme switch
        {
            "http" or "https" or "mailto" => uri.AbsoluteUri,
            _ => null
        };
    }

    /// <summary>
    /// Only inline base64 images. An external URL is refused for two reasons
    /// that both matter here: it would make a controlled document depend on a
    /// server outside the validated system, and it would beacon every reader's
    /// IP address and read time to whoever owns that server.
    /// </summary>
    private static string? SanitizeImageSource(string raw, EditorSanitizeLimits limits,
        EditorHtmlSanitizeResult result)
    {
        var value = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            result.RemovedExternalImages++;
            return null;
        }

        var comma = value.IndexOf(',');
        if (comma < 0) return null;

        var header = value[5..comma];
        var payload = value[(comma + 1)..];

        var semi = header.IndexOf(';');
        if (semi < 0) return null;

        var mediaType = header[..semi];
        var encoding = header[(semi + 1)..];

        if (!AllowedImageMediaTypes.Contains(mediaType)) return null;
        if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)) return null;

        // Validate by decoding. A payload that is not real base64, or that is
        // not the image type it claims, never reaches the file store or the
        // OOXML writer.
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length == 0) return null;

        if (limits.MaxImageBytes > 0 && bytes.Length > limits.MaxImageBytes)
        {
            result.OversizedImages++;
            return null;
        }

        if (!LooksLikeImage(bytes, mediaType))
        {
            result.RemovedExternalImages++;
            return null;
        }

        result.ImageBytesSeen += bytes.Length;
        return $"data:{mediaType.ToLowerInvariant()};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>
    /// Magic-number check. A .exe renamed image/png in a data URI would be
    /// stored, embedded in the .docx and handed to whoever opens it; the
    /// declared media type is the attacker's to choose, the first bytes are not.
    /// </summary>
    private static bool LooksLikeImage(byte[] bytes, string mediaType)
    {
        if (bytes.Length < 12) return false;

        return mediaType.ToLowerInvariant() switch
        {
            "image/png" => bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47,
            "image/jpeg" => bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "image/gif" => bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38,
            "image/webp" => bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50,
            _ => false
        };
    }

    /// <summary>
    /// Rebuilds a style attribute from the declarations that are recognised.
    /// Values are checked for the constructs that turn CSS into code -
    /// url(), expression(), the escape backslash and the comment sequence.
    /// </summary>
    private static string? SanitizeStyle(string raw)
    {
        if (raw.Length > 2048) raw = raw[..2048];

        var kept = new List<string>();

        foreach (var declaration in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;

            var property = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim();

            if (!AllowedCssProperties.Contains(property)) continue;
            if (value.Length == 0 || value.Length > 160) continue;

            // See SymbolFonts: a symbol font on a text run is, in practice,
            // always a paste artefact, and honouring it is what turns
            // non-breaking spaces into empty boxes in the exported document.
            if (property == "font-family" && NamesASymbolFont(value)) continue;

            var lower = value.ToLowerInvariant();
            if (lower.Contains("url(", StringComparison.Ordinal)) continue;
            if (lower.Contains("expression", StringComparison.Ordinal)) continue;
            if (lower.Contains("javascript", StringComparison.Ordinal)) continue;
            if (lower.Contains("/*", StringComparison.Ordinal)) continue;
            if (lower.Contains('\\')) continue;
            if (lower.Contains('(') && !IsAllowedFunction(lower)) continue;
            if (value.Any(char.IsControl)) continue;
            if (value.Contains('"') || value.Contains('<') || value.Contains('>')) continue;

            kept.Add($"{property}:{value}");
        }

        return kept.Count == 0 ? null : string.Join(';', kept);
    }

    /// <summary>
    /// True when the first family in a CSS font stack is a symbol font. Only the
    /// first is examined because that is the one that will be used; a fallback
    /// of "Symbol" behind a real font is harmless and stripping the whole
    /// declaration for it would lose the author's actual choice.
    /// </summary>
    private static bool NamesASymbolFont(string value)
    {
        var first = value.Split(',')[0].Trim().Trim('\'', '"').Trim();
        return SymbolFonts.Contains(first);
    }

    /// <summary>rgb() and rgba() are the only CSS functions a body may use.</summary>
    private static bool IsAllowedFunction(string lowerValue)
    {
        var open = lowerValue.IndexOf('(');
        while (open >= 0)
        {
            var nameStart = open;
            while (nameStart > 0 && (char.IsLetterOrDigit(lowerValue[nameStart - 1]) || lowerValue[nameStart - 1] == '-'))
                nameStart--;

            var name = lowerValue[nameStart..open];
            if (name is not ("rgb" or "rgba")) return false;

            open = lowerValue.IndexOf('(', open + 1);
        }

        return true;
    }

    /// <summary>
    /// The one place that appends to the output. Nothing else in this class
    /// writes to the buffer, which is what makes "the output contains only
    /// markup this class wrote itself" a property you can check by reading a
    /// dozen lines rather than the whole file.
    /// </summary>
    private sealed class BlockWriter
    {
        private readonly StringBuilder _output;

        public BlockWriter(StringBuilder output) => _output = output;

        public void Text(string encoded) => _output.Append(encoded);

        public void Element(string tagName, string attributes, bool selfClose)
        {
            _output.Append('<').Append(tagName).Append(attributes);
            _output.Append(selfClose ? " />" : ">");
        }

        public void EndElement(string tagName) => _output.Append("</").Append(tagName).Append('>');
    }
}
