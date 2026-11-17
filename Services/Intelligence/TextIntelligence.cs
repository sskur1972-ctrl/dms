using System.Text.RegularExpressions;
namespace DMS.Web.Services.Intelligence;
public sealed record AdvisoryRule(string Code,string Kind,string Pattern,string Severity,string Title,string Detail,string? Suggestion);
public sealed record AdvisoryFinding(string Rule,string Severity,string Finding,string? Suggestion);
public static class TextIntelligence {
 static readonly HashSet<string> StopWords=new("the a an and or to of for in on is are be with by this that from as shall will must document page section has have was it at not".Split(' '));
 static readonly Dictionary<string,string> Synonyms=new(StringComparer.OrdinalIgnoreCase){["sanitization"]="cleaning",["sanitation"]="cleaning",["sanitize"]="cleaning",["clean"]="cleaning",["sop"]="procedure",["procedures"]="procedure",["deviations"]="deviation",["nonconformance"]="deviation",["calibrate"]="calibration",["calibrated"]="calibration",["training"]="competency",["trained"]="competency",["gmp"]="manufacturing"};
 public static string[] Tokens(string text)=>Regex.Matches(text.ToLowerInvariant(),@"[\p{L}\p{N}][\p{L}\p{N}_-]{2,}").Select(m=>Synonyms.GetValueOrDefault(m.Value,m.Value)).Where(t=>!StopWords.Contains(t)).Take(100000).ToArray();
 public static string Keywords(string text)=>string.Join(", ",Tokens(text).GroupBy(x=>x).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key).Take(12).Select(g=>g.Key));
 public static double[] Rank(string query,IReadOnlyList<string> corpus){
  var bags=corpus.Select(s=>Tokens(s).GroupBy(x=>x).ToDictionary(g=>g.Key,g=>g.Count())).ToArray();
  var frequency=bags.SelectMany(b=>b.Keys).GroupBy(x=>x).ToDictionary(g=>g.Key,g=>g.Count());
  double Idf(string term)=>Math.Log(1d+(corpus.Count+1d)/(frequency.GetValueOrDefault(term)+1d));
  var q=Tokens(query).GroupBy(x=>x).ToDictionary(g=>g.Key,g=>(1+Math.Log(g.Count()))*Idf(g.Key));
  var qNorm=Math.Sqrt(q.Values.Sum(v=>v*v));
  return bags.Select(b=>{var v=b.ToDictionary(t=>t.Key,t=>(1+Math.Log(t.Value))*Idf(t.Key));var norm=Math.Sqrt(v.Values.Sum(x=>x*x));return norm==0||qNorm==0?0:q.Sum(t=>t.Value*v.GetValueOrDefault(t.Key))/(norm*qNorm);}).ToArray();
 }
 public static List<AdvisoryFinding> Review(string text,IEnumerable<AdvisoryRule> rules,string? changeControl=null,string? changeSummary=null,IEnumerable<string>? linkedNumbers=null){
  var findings=new List<AdvisoryFinding>();
  foreach(var rule in rules){
   try{
    bool Match(string pattern)=>Regex.IsMatch(text,pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(200));
    var kind=rule.Kind.ToUpperInvariant();
    var triggered=kind switch {
     "CHANGE_CONTROL_REQUIRED"=>string.IsNullOrWhiteSpace(changeControl),
     "CHANGE_SUMMARY_REQUIRED"=>string.IsNullOrWhiteSpace(changeSummary),
     "TEXT_MUST_CONTAIN_ALL"=>rule.Pattern.Split('|',StringSplitOptions.RemoveEmptyEntries).Any(p=>!Match(p)),
     "REFERENCES_NOT_LINKED"=>Regex.Matches(text,rule.Pattern,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(200)).Any(m=>!(linkedNumbers??[]).Contains(m.Value,StringComparer.OrdinalIgnoreCase)),
     "REQUIRED" or "REQUIRED_SECTION" or "REQUIRED_PATTERN" or "MISSING_PATTERN"=>!Match(rule.Pattern),
     "TEXT_MUST_NOT_MATCH" or "PATTERN"=>Match(rule.Pattern),
     _=>false
    };
    if(triggered){
     var detail=rule.Detail;
     if(detail.Contains("{0}",StringComparison.Ordinal)){
      var items=kind=="TEXT_MUST_CONTAIN_ALL" ? rule.Pattern.Split('|',StringSplitOptions.RemoveEmptyEntries).Where(p=>!Match(p)).Select(p=>p.Replace(@"\b","").Trim('^','$','(',')'))
       : kind=="REFERENCES_NOT_LINKED" ? Regex.Matches(text,rule.Pattern,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(200)).Select(m=>m.Value).Where(v=>!(linkedNumbers??[]).Contains(v,StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)
       : Enumerable.Empty<string>();
      var values=string.Join(", ",items); detail=detail.Replace("{0}",string.IsNullOrWhiteSpace(values)?"requires manual QA verification":values);
     }
     findings.Add(new(rule.Code,rule.Severity,rule.Title+": "+detail,rule.Suggestion));
    }
   }catch(ArgumentException){findings.Add(new(rule.Code,"INFO","Rule configuration is invalid; QA must correct it.",null));}
    catch(RegexMatchTimeoutException){findings.Add(new(rule.Code,"INFO","Rule evaluation timed out; manual review required.",null));}
  }
  return findings;
 }
 public static bool IsSpike(IReadOnlyList<double> history,double current){if(history.Count<3)return false;var mean=history.Average();var sd=Math.Sqrt(history.Sum(v=>(v-mean)*(v-mean))/history.Count);return current>mean+2*Math.Max(1,sd);}
}
