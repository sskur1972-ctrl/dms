using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DMS.Web.Services.DocumentEditor;

public static class DocumentSectionLocator
{
    public static string? FindPage(Stream source,string pdfText,int page)
        => FindPageSections(source,pdfText,page).FirstOrDefault();

    public static IReadOnlyList<string> FindPageSections(Stream source,string pdfText,int page)
    {
        var pages=pdfText.Split('\f');
        if(page<1||page>pages.Length)return Array.Empty<string>();
        using var zip=new ZipArchive(source,ZipArchiveMode.Read,leaveOpen:true);
        var entry=zip.GetEntry("word/document.xml");if(entry==null||entry.Length>8*1024*1024)return Array.Empty<string>();
        using var stream=entry.Open();using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024});
        var doc=XDocument.Load(reader);
        var customHeadings=new HashSet<string>();var styleEntry=zip.GetEntry("word/styles.xml");
        if(styleEntry!=null&&styleEntry.Length<=8*1024*1024){using var styleStream=styleEntry.Open();using var styleReader=XmlReader.Create(styleStream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024});
            customHeadings=XDocument.Load(styleReader).Descendants(W+"style").Where(s=>s.Descendants(W+"outlineLvl").Any()||Regex.IsMatch((string?)s.Element(W+"name")?.Attribute(W+"val")??"",@"^heading\s*\d",RegexOptions.IgnoreCase)).Select(s=>(string?)s.Attribute(W+"styleId")).OfType<string>().ToHashSet();}
        var headings=doc.Descendants(W+"p").Select(p=>new { Text=Normalize(string.Concat(p.Descendants(W+"t").Select(t=>t.Value))),Style=(string?)p.Element(W+"pPr")?.Element(W+"pStyle")?.Attribute(W+"val"),Outline=p.Element(W+"pPr")?.Element(W+"outlineLvl")!=null })
          .Where(p=>p.Text.Length>0&&(p.Outline||customHeadings.Contains(p.Style??"")||Regex.IsMatch(p.Style??"",@"^Heading\d",RegexOptions.IgnoreCase)||Regex.IsMatch(p.Text,@"^\d+(?:\.\d+)*(?:\.|\s)\s*\S")&&p.Text.Length<=128))
          .Select(p=>(Text:p.Text,Section:Regex.Match(p.Text,@"^(\d+(?:\.\d+)*)").Success?Regex.Match(p.Text,@"^(\d+(?:\.\d+)*)").Value:p.Text[..Math.Min(128,p.Text.Length)])).ToList();
        string? section=null;var pageSections=new List<string>();
        for(var i=0;i<page;i++){
            var text=Normalize(pages[i]);
            var matches=headings.Select(h=>(Heading:h,Position:text.IndexOf(h.Text,StringComparison.OrdinalIgnoreCase))).Where(h=>h.Position>=0).OrderBy(h=>h.Position).ToList();
            // A page may span sections: selection has priority; page-only binding uses its first heading.
            if(matches.Count>0){
                if(i==page-1)pageSections.AddRange(matches.Select(m=>m.Heading.Section));
                section=matches[^1].Heading.Section;
            }
        }
        if(pageSections.Count==0&&section!=null)pageSections.Add(section);
        return pageSections.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    public static string? Find(Stream source, string selection, string? sectionReference = null)
    {
        var quote = Normalize(selection);
        if (quote.Length == 0 && string.IsNullOrWhiteSpace(sectionReference)) return null;
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        XDocument? Read(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return null;
            if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Document section index exceeds the limit.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        var doc = Read("word/document.xml");
        if (doc == null) return null;
        var styles = Read("word/styles.xml");
        var headings = (styles?.Descendants(W + "style") ?? [])
            .Where(s => s.Descendants(W + "outlineLvl").Any()
                || Regex.IsMatch((string?)s.Element(W + "name")?.Attribute(W + "val") ?? "", @"^heading\s*\d", RegexOptions.IgnoreCase))
            .Select(s => (string?)s.Attribute(W + "styleId")).OfType<string>().ToHashSet();
        var paragraphs = doc.Root?.Element(W + "body")?.Descendants(W + "p").ToList() ?? [];
        var body = new System.Text.StringBuilder();
        var sections = new List<(int Start, string? Section)>();
        var matchingHeadings = new List<string>();
        string? section = null;
        foreach (var p in paragraphs)
        {
            var value = Normalize(string.Concat(p.Descendants()
                .Where(t => t.Name == W + "t" || t.Name == W + "tab" || t.Name == W + "br")
                .Select(t => t.Name == W + "t" ? t.Value : " ")));
            var style = (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");
            var numbered = Regex.Match(value, @"^(\d+(?:\.\d+)*)(?:\.|\s)\s*\S");
            if (headings.Contains(style ?? "") || p.Element(W + "pPr")?.Element(W + "outlineLvl") != null
                || (numbered.Success && value.Length <= 128))
            {
                section = numbered.Success ? numbered.Groups[1].Value : value[..Math.Min(value.Length, 128)];
                if (string.Equals(section, sectionReference?.Trim(), StringComparison.OrdinalIgnoreCase))
                    matchingHeadings.Add(value);
            }
            sections.Add((body.Length, section));
            body.Append(value).Append(' ');
        }
        var text = body.ToString();
        if (sectionReference != null) return matchingHeadings.Count == 1 ? matchingHeadings[0] : null;
        var position = text.IndexOf(quote, StringComparison.Ordinal);
        // Ambiguous selections must not be assigned to the wrong section.
        if (position < 0 || text.IndexOf(quote, position + 1, StringComparison.Ordinal) >= 0) return null;
        return sections.LastOrDefault(s => s.Start <= position).Section;
    }
}
