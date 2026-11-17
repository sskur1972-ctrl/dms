namespace DMS.Web.Services.Intelligence;

public sealed record AiProviderOptions(string Provider, string Endpoint, string Model, string? ApiKey,
    int TimeoutSeconds = 180, int MaxTokens = 3000, string ResponseFormat = "JSON_SCHEMA", int ContextTokens = 16384, string ThinkingMode = "disabled") {
    public string Fingerprint => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{Provider}|{Endpoint}|{Model}")));
}
public sealed record AiFinding(string Severity, string Finding, string Suggestion, string Evidence,
    string Section, List<string> Citations);
public sealed record AiBatchResult(List<AiFinding> Findings);
public sealed record ReviewBatch(int Number, int Start, string Text);
public sealed class RagChunk {
    public long Id { get; set; }
    public long? VersionId { get; set; }
    public long? KnowledgeId { get; set; }
    public long? DocumentId { get; set; }
    public string Title { get; set; } = "";
    public string Sha { get; set; } = "";
    public int Ordinal { get; set; }
    public int? Page { get; set; }
    public string Section { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Uri { get; set; }
    public float[]? Embedding { get; set; }
    public string? EmbeddingKey { get; set; }
    public double Score { get; set; }
    public string Citation => $"R{Id}";
}
public sealed class KnowledgeSource {
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? Uri { get; set; }
    public string Body { get; set; } = "";
    public string Sha { get; set; } = "";
    public bool Active { get; set; }
    public DateTime CreatedAt { get; set; }
}
public sealed record AiStatus(string Provider, string Model, bool Configured, bool ModelEnabled,
    bool RagEnabled, bool EmbeddingsEnabled, int References, int Chunks);
