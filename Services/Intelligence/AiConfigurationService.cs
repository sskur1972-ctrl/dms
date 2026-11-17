namespace DMS.Web.Services.Intelligence;

public sealed class AiConfigurationService(IConfigService config) {
    public async Task<AiProviderOptions> Review(CancellationToken ct) {
        var provider = (await config.GetStringAsync("ai.review.provider", "OLLAMA", ct))!.ToUpperInvariant();
        var prefix = "ai.providers." + provider.ToLowerInvariant() + ".";
        var legacy = provider == "OPENAI_COMPATIBLE";
        var endpoint = await config.GetStringAsync(prefix + "endpoint", null, ct) ?? (legacy ? await config.GetStringAsync("ai.review.endpoint", "", ct) : "");
        var model = await config.GetStringAsync(prefix + "model", null, ct) ?? (legacy ? await config.GetStringAsync("ai.review.model", "", ct) : "");
        var key = await config.GetStringAsync(prefix + "apiKey", null, ct) ?? (legacy ? await config.GetStringAsync("ai.review.apiKey", null, ct) : null);
        if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable(provider + "_API_KEY");
        // Legacy Ollama generate configuration now uses the chat API's messages/schema contract.
        if (provider == "OLLAMA" && endpoint?.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase) == true) endpoint = endpoint[..^8] + "chat";
        return new(provider, endpoint ?? "", model ?? "", key,
            Math.Clamp(await config.GetIntAsync("ai.review.timeoutSeconds", 180, ct), 10, 600),
            Math.Clamp(await config.GetIntAsync("ai.review.maxOutputTokens", 3000, ct), 500, 16000),
            (await config.GetStringAsync(prefix + "responseFormat", provider == "DEEPSEEK" ? "JSON_OBJECT" : "JSON_SCHEMA", ct))!.ToUpperInvariant(),
            Math.Clamp(await config.GetIntAsync("ai.providers.ollama.contextTokens",16384,ct),8192,65536),
            (await config.GetStringAsync(prefix + "thinkingMode", "disabled", ct))!.ToLowerInvariant());
    }
    public async Task<AiProviderOptions?> Embeddings(CancellationToken ct) {
        if (!await config.GetBoolAsync("ai.embeddings.enabled", false, ct)) return null;
        var provider = (await config.GetStringAsync("ai.embeddings.provider", "OLLAMA", ct))!.ToUpperInvariant();
        var key = await config.GetStringAsync("ai.embeddings.apiKey", null, ct);
        if (string.IsNullOrWhiteSpace(key)) key = await config.GetStringAsync("ai.providers." + provider.ToLowerInvariant() + ".apiKey", null, ct);
        if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable(provider + "_API_KEY");
        return new(provider,
            (await config.GetStringAsync("ai.embeddings.endpoint", "http://localhost:11434/api/embed", ct))!,
            (await config.GetStringAsync("ai.embeddings.model", "", ct))!,
            key,
            Math.Clamp(await config.GetIntAsync("ai.embeddings.timeoutSeconds", 120, ct), 10, 600));
    }
}
