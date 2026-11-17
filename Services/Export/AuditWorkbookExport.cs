using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace DMS.Web.Services.Export;

/// <summary>Audit-specific native XLSX output. No shared report layouts are changed.</summary>
public static class AuditWorkbookExport
{
    static readonly XNamespace S="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace P="http://schemas.openxmlformats.org/package/2006/relationships";
    public static ExportFile Create(AuditReport report,string format,DateTime generatedUtc)
    {
        if(!format.Equals("xlsx",StringComparison.OrdinalIgnoreCase)) {
            var csv=new TableData{Title="DMS audit trail",Heading=new(report.Sheets[0].Heading),Columns=["Category","Audit reference","Date/time (UTC)","Date/time (local, offset)","Document / record / revision","Action or field","Old Value","New Value","Performed by","Role","Reason","Electronic signature"]};
            foreach(var sheet in report.Sheets)foreach(var row in sheet.Rows) {
                if(sheet.Title=="Other business activity")csv.Rows.Add([sheet.Title,row[0],row[1],row[2],row[3],row[4],null,null,row[5],row[6],row[7],null]);
                else csv.Rows.Add(new object?[]{sheet.Title}.Concat(row).ToArray());
            }
            for(int i=0;i<csv.Rows.Count;i++)csv.Rows[i]=csv.Rows[i].Select(v=>v is DateTime time?(object)time.ToString("dd-MMM-yyyy HH:mm:ss.fff",CultureInfo.InvariantCulture):v).ToArray();
            string Cell(object? value) {
                var text=TableExport.Text(value);var first=text.TrimStart(' ','\t','\r','\n').FirstOrDefault();
                if("=+-@".Contains(first) && first!='\0')text="'"+text;
                return "\""+text.Replace("\"","\"\"")+"\"";
            }
            var content=new StringBuilder();content.AppendLine(Cell(csv.Title));
            foreach(var h in csv.Heading)content.AppendLine(Cell(h));content.AppendLine();
            content.AppendLine(string.Join(',',csv.Columns.Select(c=>Cell(c))));
            foreach(var row in csv.Rows)content.AppendLine(string.Join(',',row.Select(Cell)));
            var body=new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(content.ToString())).ToArray();
            return new(body,"text/csv; charset=utf-8",$"audit-trail-{generatedUtc:yyyyMMdd-HHmmss}.csv",Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        }
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)) {
            void Put(string path,XElement element){using var w=new StreamWriter(zip.CreateEntry(path).Open(),new UTF8Encoding(false));new XDocument(new XDeclaration("1.0","utf-8","yes"),element).Save(w);}
            XNamespace types="http://schemas.openxmlformats.org/package/2006/content-types";
            Put("[Content_Types].xml",new XElement(types+"Types",new XElement(types+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(types+"Default",new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),
                new XElement(types+"Override",new XAttribute("PartName","/xl/workbook.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(types+"Override",new XAttribute("PartName","/xl/styles.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")),
                report.Sheets.Select((_,i)=>new XElement(types+"Override",new XAttribute("PartName",$"/xl/worksheets/sheet{i+1}.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));
            Put("_rels/.rels",new XElement(P+"Relationships",new XElement(P+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",R.NamespaceName+"/officeDocument"),new XAttribute("Target","xl/workbook.xml"))));
            Put("xl/workbook.xml",new XElement(S+"workbook",new XAttribute(XNamespace.Xmlns+"r",R),
                new XElement(S+"bookViews",new XElement(S+"workbookView",new XAttribute("activeTab",0))),
                new XElement(S+"sheets",report.Sheets.Select((t,i)=>new XElement(S+"sheet",new XAttribute("name",t.Title),new XAttribute("sheetId",i+1),new XAttribute(R+"id",$"rId{i+1}")))),
                new XElement(S+"definedNames",report.Sheets.Select((t,i)=>new XElement(S+"definedName",new XAttribute("name","_xlnm.Print_Titles"),new XAttribute("localSheetId",i),$"'{t.Title}'!$1:$7")))));
            Put("xl/_rels/workbook.xml.rels",new XElement(P+"Relationships",report.Sheets.Select((_,i)=>new XElement(P+"Relationship",new XAttribute("Id",$"rId{i+1}"),new XAttribute("Type",R.NamespaceName+"/worksheet"),new XAttribute("Target",$"worksheets/sheet{i+1}.xml"))),new XElement(P+"Relationship",new XAttribute("Id","styles"),new XAttribute("Type",R.NamespaceName+"/styles"),new XAttribute("Target","styles.xml"))));
            Put("xl/styles.xml",Styles());
            for(int i=0;i<report.Sheets.Count;i++)Put($"xl/worksheets/sheet{i+1}.xml",Sheet(report.Sheets[i],generatedUtc));
        }
        var bytes=memory.ToArray();return new(bytes,"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",$"audit-trail-{generatedUtc:yyyyMMdd-HHmmss}.xlsx",Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
    static XElement Styles()
    {
        XElement Font(int size,bool bold,string color)=>new(S+"font",bold?new XElement(S+"b"):null,new XElement(S+"sz",new XAttribute("val",size)),new XElement(S+"color",new XAttribute("rgb",color)),new XElement(S+"name",new XAttribute("val","Arial")));
        XElement Fill(string? color)=>new(S+"fill",new XElement(S+"patternFill",new XAttribute("patternType",color==null?"none":"solid"),color==null?null:new XElement(S+"fgColor",new XAttribute("rgb",color)),color==null?null:new XElement(S+"bgColor",new XAttribute("indexed",64))));
        XElement Xf(int font,int fill,int number=0)=>new(S+"xf",new XAttribute("numFmtId",number),new XAttribute("fontId",font),new XAttribute("fillId",fill),new XAttribute("borderId",0),new XAttribute("xfId",0),new XAttribute("applyFont",1),new XAttribute("applyFill",1),new XAttribute("applyAlignment",1),new XAttribute("applyNumberFormat",number>0?1:0),new XElement(S+"alignment",new XAttribute("vertical","top"),new XAttribute("horizontal","left"),new XAttribute("indent",1),new XAttribute("wrapText",1)));
        return new XElement(S+"styleSheet",new XElement(S+"numFmts",new XAttribute("count",1),new XElement(S+"numFmt",new XAttribute("numFmtId",164),new XAttribute("formatCode","dd-mmm-yyyy hh:mm:ss.000"))),
            new XElement(S+"fonts",new XAttribute("count",4),Font(10,false,"FF223F3A"),Font(10,true,"FFFFFFFF"),Font(15,true,"FFFFFFFF"),Font(10,false,"FF526A64")),
            new XElement(S+"fills",new XAttribute("count",7),Fill(null),new XElement(S+"fill",new XElement(S+"patternFill",new XAttribute("patternType","gray125"))),Fill("FF135E53"),Fill("FFF2F7F5"),Fill("FFFFF3F0"),Fill("FFEDF7F0"),Fill("FFE8F1EE")),
            new XElement(S+"borders",new XAttribute("count",1),new XElement(S+"border",new XElement(S+"left"),new XElement(S+"right"),new XElement(S+"top"),new XElement(S+"bottom"),new XElement(S+"diagonal"))),
            new XElement(S+"cellStyleXfs",new XAttribute("count",1),new XElement(S+"xf",new XAttribute("numFmtId",0),new XAttribute("fontId",0),new XAttribute("fillId",0),new XAttribute("borderId",0))),
            new XElement(S+"cellXfs",new XAttribute("count",9),Xf(0,0),Xf(1,2),Xf(2,2),Xf(3,0),Xf(0,3),Xf(0,4),Xf(0,5),Xf(0,0,164),Xf(0,3,164)),
            new XElement(S+"cellStyles",new XAttribute("count",1),new XElement(S+"cellStyle",new XAttribute("name","Normal"),new XAttribute("xfId",0),new XAttribute("builtinId",0))));
    }
    static XElement Sheet(TableData data,DateTime generated)
    {
        var widths=data.Columns.Select(c=>c switch {"Old Value" or "New Value"=>52d,"Date/time (UTC)"=>28,"Date/time (local, offset)"=>37,"Audit reference"=>18,"Document / revision"=>28,"Field / location"=>38,"Changed by" or "Performed by"=>29,"Role at event"=>22,"Reason for change" or "Reason / comment"=>35,"Electronic signature"=>44,_=>25}).ToArray();
        string last=Column(data.Columns.Count-1);var rows=new XElement(S+"sheetData");
        XElement Cell(int col,int row,object? value,int style) {
            var cell=new XElement(S+"c",new XAttribute("r",Column(col)+row),new XAttribute("s",style));
            if(value is DateTime date)cell.Add(new XElement(S+"v",date.ToOADate().ToString(CultureInfo.InvariantCulture)));
            else {var text=TableExport.Text(value);if(text.Length>32767)throw new InvalidOperationException("An audit cell exceeds Excel's supported length. Narrow this report.");cell.Add(new XAttribute("t","inlineStr"),new XElement(S+"is",new XElement(S+"t",new XAttribute(XNamespace.Xml+"space","preserve"),XmlText(text))));}
            return cell;
        }
        rows.Add(new XElement(S+"row",new XAttribute("r",1),new XAttribute("ht",30),new XAttribute("customHeight",1),Cell(0,1,"DMS "+data.Title.ToUpperInvariant(),2)));
        for(int i=0;i<data.Heading.Count;i++)rows.Add(new XElement(S+"row",new XAttribute("r",i+2),new XAttribute("ht",25),new XAttribute("customHeight",1),Cell(0,i+2,data.Heading[i],3)));
        rows.Add(new XElement(S+"row",new XAttribute("r",7),new XAttribute("ht",32),new XAttribute("customHeight",1),data.Columns.Select((c,i)=>Cell(i,7,c,1))));
        for(int n=0;n<data.Rows.Count;n++) {
            var values=data.Rows[n];int index=n+8;double lines=values.Select((v,i)=>TableExport.Text(v).Split('\n').Sum(s=>Math.Max(1,Math.Ceiling(s.Length/Math.Max(10,widths[i]-3))))).DefaultIfEmpty(1).Max();
            rows.Add(new XElement(S+"row",new XAttribute("r",index),new XAttribute("ht",Math.Clamp(lines*13+10,32,400).ToString(CultureInfo.InvariantCulture)),new XAttribute("customHeight",1),values.Select((v,i)=>Cell(i,index,v,v is DateTime?(n%2==0?7:8):data.Columns[i]=="Old Value"?5:data.Columns[i]=="New Value"?6:n%2==0?0:4))));
        }
        return new XElement(S+"worksheet",new XElement(S+"sheetPr",new XElement(S+"pageSetUpPr",new XAttribute("fitToPage",0))),new XElement(S+"dimension",new XAttribute("ref",$"A1:{last}{Math.Max(8,data.Rows.Count+7)}")),
            new XElement(S+"sheetViews",new XElement(S+"sheetView",new XAttribute("workbookViewId",0),new XAttribute("showGridLines",0),new XAttribute("zoomScale",85),new XElement(S+"pane",new XAttribute("xSplit",1),new XAttribute("ySplit",7),new XAttribute("topLeftCell","B8"),new XAttribute("activePane","bottomRight"),new XAttribute("state","frozen")))),
            new XElement(S+"cols",widths.Select((w,i)=>new XElement(S+"col",new XAttribute("min",i+1),new XAttribute("max",i+1),new XAttribute("width",w),new XAttribute("customWidth",1)))),rows,
            new XElement(S+"autoFilter",new XAttribute("ref",$"A7:{last}{Math.Max(7,data.Rows.Count+7)}")),new XElement(S+"mergeCells",new XAttribute("count",5),Enumerable.Range(1,5).Select(i=>new XElement(S+"mergeCell",new XAttribute("ref",$"A{i}:{last}{i}")))),
            new XElement(S+"printOptions",new XAttribute("horizontalCentered",1)),new XElement(S+"pageMargins",new XAttribute("left",0.25),new XAttribute("right",0.25),new XAttribute("top",0.4),new XAttribute("bottom",0.4),new XAttribute("header",0.2),new XAttribute("footer",0.2)),
            new XElement(S+"pageSetup",new XAttribute("paperSize",8),new XAttribute("orientation","landscape"),new XAttribute("scale",90),new XAttribute("fitToWidth",0),new XAttribute("fitToHeight",0)),new XElement(S+"headerFooter",new XElement(S+"oddFooter",$"&L DMS audit extract | {generated:dd-MMM-yyyy HH:mm} UTC &R Page &P of &N")));
    }
    static string Column(int i){string s="";for(i++;i>0;i=(i-1)/26)s=(char)('A'+(i-1)%26)+s;return s;}
    static string XmlText(string value)=>new(value.Where(c=>c=='\t'||c=='\r'||c=='\n'||c>=' ').ToArray());
}




