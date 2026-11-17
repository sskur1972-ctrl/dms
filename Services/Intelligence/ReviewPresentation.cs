namespace DMS.Web.Services.Intelligence;

public sealed record ReviewObservation(string Id, string Source, string Rule, string Severity, string Finding, string Recommendation, string Section, string Evidence, IReadOnlyList<string> Citations);

public static class ReviewPresentation
{
    public static int Coverage(IntelligenceReview review) => review.TotalCharacters <= 0 ? 0 : (int)Math.Clamp(100L * review.ReviewedCharacters / review.TotalCharacters, 0, 100);
    public static string Severity(string value) => value.ToUpperInvariant() is "HIGH" or "MEDIUM" or "LOW" ? value.ToUpperInvariant() : "INFO";
    public static string Display(string? text) => (text ?? "").Replace("{0}", "details were not captured in this saved review; rerun the review for specifics");
    public static List<ReviewObservation> Observations(IntelligenceReview review) => review.Findings.Select((f,i) =>
        new ReviewObservation($"QA-{i+1:00}", "QA rule", f.Rule, Severity(f.Severity), Display(f.Finding), Display(f.Suggestion), "Document-wide", "", []))
        .Concat(review.AiFindings.Select((f,i) => new ReviewObservation($"AI-{i+1:00}", "AI review", "", Severity(f.Severity), Display(f.Finding), Display(f.Suggestion), f.Section, f.Evidence, f.Citations)))
        .OrderBy(f => f.Severity switch { "HIGH" => 0, "MEDIUM" => 1, "LOW" => 2, _ => 3 }).ThenBy(f => f.Id).ToList();
    public static string Status(string status) => status switch { "COMPLETE" => "AI review complete", "PARTIAL" => "AI review incomplete", "FAILED" => "AI review unavailable", "WITHHELD" => "AI findings withheld", _ => "QA rules only" };
}
