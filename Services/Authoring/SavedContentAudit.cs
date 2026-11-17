using System.Text.Json;

namespace DMS.Web.Services.Authoring;

public static class SavedContentAudit
{
    public static (string Before,string After) WithEvidence((string Before,string After) delta,
        string? oldSha,string? oldPath,string? newSha,string? newPath)
    {
        var before=JsonSerializer.Deserialize<Dictionary<string,object?>>(delta.Before)!;
        var after=JsonSerializer.Deserialize<Dictionary<string,object?>>(delta.After)!;
        before["SourceSha256"]=oldSha;before["SourcePath"]=oldPath;
        after["SourceSha256"]=newSha;after["SourcePath"]=newPath;
        return (JsonSerializer.Serialize(before),JsonSerializer.Serialize(after));
    }
    // Store paired, changed values only; context belongs in event metadata, not in every payload.
    public static (string Before,string After) Changes(string? oldHtml,string? oldName,string? newHtml,string? newName)
    {
        var before=new Dictionary<string,object?>(); var after=new Dictionary<string,object?>();
        if(oldName!=newName){before["FileName"]=oldName;after["FileName"]=newName;}
        if(oldHtml==null || newHtml==null)
        {
            if(oldHtml!=newHtml) {
                before["ContentAvailability"]=oldHtml==null?"Readable text unavailable":"Readable text available";
                after["ContentAvailability"]=newHtml==null?"Readable text unavailable":"Readable text available";
            }
            if(oldHtml==null && newHtml!=null) {
                var key=string.IsNullOrEmpty(oldName)?"Content added":"Document content (previous text unavailable)";
                before[key]=string.IsNullOrEmpty(oldName)?null:"Not recorded (previous text unavailable)";
                after[key]=string.Join("\n",TextDiffRules.ToLines(newHtml));
            }
            return (JsonSerializer.Serialize(before),JsonSerializer.Serialize(after));
        }
        var left=TextDiffRules.ToLines(oldHtml); var right=TextDiffRules.ToLines(newHtml); int number=0; string location="";
        void Record(string? oldValue,string? newValue) {
            if(oldValue==newValue)return;
            var key=$"Content change {++number}"+(location.Length==0?"":" ("+location+")");before[key]=oldValue;after[key]=newValue;
        }
        void CompareWords(string? oldValue,string? newValue) {
            if(oldValue==null || newValue==null){Record(oldValue,newValue);return;}
            var oldWords=oldValue.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            var newWords=newValue.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            Diff(oldWords,newWords,Record);
        }
        if(left.Count<=TextDiffRules.MaximumLines && right.Count<=TextDiffRules.MaximumLines)
        {
            var removed=new List<DiffRow>();var added=new List<DiffRow>();
            string Location(int? oldLine,int? newLine) {
                var lines=newLine!=null?right:left;int line=(newLine??oldLine??1);
                if(line>0 && line<=lines.Count) {
                    var cell=System.Text.RegularExpressions.Regex.Match(lines[line-1],@"^(.*?), cell ([A-Z]+[0-9]+):");
                    if(cell.Success)return $"Worksheet {cell.Groups[1].Value}, cell {cell.Groups[2].Value}";
                }
                var heading=lines.Take(Math.Min(line,lines.Count)).LastOrDefault(s=>System.Text.RegularExpressions.Regex.IsMatch(s,@"^\d+(?:\.\d+)*[.)]?\s+\p{L}"));
                return (heading==null?"":("Section "+(heading.Length>100?heading[..100]+"...":heading)+"; "))+(newLine!=null?$"paragraph {newLine}":$"removed paragraph {oldLine}");
            }
            void Flush() {
                for(int i=0;i<Math.Max(removed.Count,added.Count);i++) {
                    var oldRow=i<removed.Count?removed[i]:null;var newRow=i<added.Count?added[i]:null;
                    location=Location(oldRow?.LeftLineNo,newRow?.RightLineNo);
                    CompareWords(oldRow?.Left,newRow?.Right);
                }
                removed.Clear();added.Clear();
            }
            foreach(var row in TextDiffRules.Compare(left,right)) {
                if(row.Kind==DiffKind.Removed)removed.Add(row);else if(row.Kind==DiffKind.Added)added.Add(row);else Flush();
            }
            Flush();
        }
        else Diff(left,right,CompareWords);
        return (JsonSerializer.Serialize(before),JsonSerializer.Serialize(after));
    }

    // LCS preserves unchanged spans. Large inputs are partitioned using stable common anchors.
    static void Diff(IReadOnlyList<string> left,IReadOnlyList<string> right,Action<string?,string?> record)
    {
        int prefix=0,suffix=0;
        while(prefix<left.Count && prefix<right.Count && left[prefix]==right[prefix])prefix++;
        while(suffix<left.Count-prefix && suffix<right.Count-prefix && left[^(suffix+1)]==right[^(suffix+1)])suffix++;
        var l=left.Skip(prefix).Take(left.Count-prefix-suffix).ToArray();
        var r=right.Skip(prefix).Take(right.Count-prefix-suffix).ToArray();
        if(l.Length==0 && r.Length==0)return;
        if(l.Length>TextDiffRules.MaximumLines || r.Length>TextDiffRules.MaximumLines)
        {
            // Deterministic anchors avoid an unbounded quadratic matrix on large files.
            var positions=r.Select((v,i)=>(v,i)).GroupBy(x=>x.v).ToDictionary(g=>g.Key,g=>g.Select(x=>x.i).ToArray());
            foreach(int i in Enumerable.Range(l.Length/2,l.Length-l.Length/2).Concat(Enumerable.Range(0,l.Length/2)))if(positions.TryGetValue(l[i],out var matches)) {
                int j=matches[matches.Length/2];
                Diff(l.Take(i).ToArray(),r.Take(j).ToArray(),record);
                Diff(l.Skip(i+1).ToArray(),r.Skip(j+1).ToArray(),record);return;
            }
            record(l.Length==0?null:string.Join(" ",l),r.Length==0?null:string.Join(" ",r));return;
        }
        var removed=new List<string>(); var added=new List<string>();
        void Flush(){if(removed.Count>0||added.Count>0)record(removed.Count==0?null:string.Join(" ",removed),added.Count==0?null:string.Join(" ",added));removed.Clear();added.Clear();}
        foreach(var row in TextDiffRules.Compare(l,r)) {
            if(row.Kind==DiffKind.Removed)removed.Add(row.Left!);
            else if(row.Kind==DiffKind.Added)added.Add(row.Right!);
            else Flush();
        }
        Flush();
    }
    public static string Snapshot(string? html, string? fileName) => JsonSerializer.Serialize(new {
        FileName = fileName,
        DocumentContent = html == null ? null : string.Join(Environment.NewLine,TextDiffRules.ToLines(html)),
        ContentAvailability = html == null ? "Readable text is unavailable for this file." : null
    });

    public static async Task<(string Before,string After)> CaptureAsync(IDocumentTextExtractor extractor,
        string? oldPath,string? oldName,string? newPath,string? newName,CancellationToken ct)
    {
        var oldHtml=extractor.CanExtract(oldName,null) ? await extractor.ExtractHtmlAsync(oldPath,ct) : null;
        var newHtml=extractor.CanExtract(newName,null) ? await extractor.ExtractHtmlAsync(newPath,ct) : null;
        return Changes(oldHtml,oldName,newHtml,newName);
    }
}
