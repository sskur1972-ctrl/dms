using Dapper;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DMS.Web.Services.Intelligence;

public sealed class RagService(IConfiguration configuration, IConfigService config, AiConfigurationService settings, AiProviderClient client) {
    NpgsqlConnection Connection() => new(configuration.GetConnectionString("DMSConnection"));
    public string RetrievalMode { get; private set; } = "Ranked lexical retrieval";
    const string ChunkColumns = "c.id,c.version_id AS VersionId,c.knowledge_id AS KnowledgeId,c.source_sha256 AS Sha,c.ordinal,c.page_number AS Page,c.section_heading AS Section,c.content AS Text,c.embedding,c.embedding_key AS EmbeddingKey";
    public static List<RagChunk> Chunk(string text, int size) {
        size = Math.Clamp(size, 1000, 6000);
        var chunks = new List<RagChunk>(); var pages = text.Replace("\r\n", "\n").Split('\f');
        for (var page = 0; page < pages.Length; page++) {
            var body = pages[page]; var heading = "Unspecified";
            for (var start = 0; start < body.Length;) {
                var length = Math.Min(size, body.Length - start);
                if (start + length < body.Length) { var newline = body.LastIndexOf('\n', start + length - 1, length); if (newline > start + length / 2) length = newline - start + 1; }
                if(start+length<body.Length&&char.IsHighSurrogate(body[start+length-1]))length--;
                var part = body.Substring(start, length);
                var found = part.Split('\n').FirstOrDefault(l => l.Trim().Length is > 2 and < 100 && Regex.IsMatch(l.Trim(), @"^(\d+[.\d]*[\s.)]+|purpose\b|scope\b|responsibilit|procedure\b|references\b)", RegexOptions.IgnoreCase));
                if (found != null) heading = found.Trim();
                if (!string.IsNullOrWhiteSpace(part)) chunks.Add(new() { Ordinal = chunks.Count, Page = pages.Length > 1 ? page + 1 : null, Section = heading, Text = part });
                // Every character is covered, with overlap for context across boundaries.
                start += length == body.Length - start ? length : Math.Max(1, length - 200);
                if(start>0&&start<body.Length&&char.IsLowSurrogate(body[start]))start--;
            }
        }
        return chunks;
    }
    public static List<ReviewBatch> Batches(string text, int size) {
        size = Math.Clamp(size, 1000, 50000); var list = new List<ReviewBatch>();
        for (var start = 0; start < text.Length;) {
            var length = Math.Min(size, text.Length - start);
            if (start + length < text.Length) { var newline = text.LastIndexOf('\n', start + length - 1, length); if (newline > start + length / 2) length = newline - start + 1; }
            if(start+length<text.Length&&char.IsHighSurrogate(text[start+length-1]))length--;
            list.Add(new(list.Count + 1, start, text.Substring(start, length))); start += length;
        }
        return list;
    }
    public async Task EnsureVersion(long version, string sha, string text, bool force, CancellationToken ct) {
        await using var cn = Connection(); await cn.OpenAsync(ct);
        if (!force && await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM dms.ai_document_chunks WHERE version_id=@version AND source_sha256=@sha)", new { version, sha }, cancellationToken: ct))) return;
        await using var tx = await cn.BeginTransactionAsync(ct);
        var actual = await cn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT source_sha256 FROM dms.document_versions WHERE id=@version FOR UPDATE", new { version }, tx, cancellationToken: ct));
        if (actual != sha) throw new InvalidOperationException("Document changed during chunk indexing; reindex the current revision.");
        await cn.ExecuteAsync(new CommandDefinition("DELETE FROM dms.ai_document_chunks WHERE version_id=@version", new { version }, tx, cancellationToken: ct));
        var chunks = Chunk(text, await config.GetIntAsync("ai.rag.chunkCharacters", 3000, ct));
        await cn.ExecuteAsync(new CommandDefinition("INSERT INTO dms.ai_document_chunks(version_id,source_sha256,ordinal,page_number,section_heading,content) VALUES(@version,@sha,@Ordinal,@Page,@Section,@Text)", chunks.Select(c => new { version, sha, c.Ordinal, c.Page, c.Section, c.Text }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
    public async Task<List<KnowledgeSource>> Sources(CancellationToken ct) {
        await using var cn = Connection();
        return (await cn.QueryAsync<KnowledgeSource>(new CommandDefinition("SELECT id,title,source_uri AS Uri,source_sha256 AS Sha,is_active AS Active,created_at AS CreatedAt FROM dms.ai_knowledge_sources ORDER BY created_at DESC", cancellationToken: ct))).ToList();
    }
    public async Task<KnowledgeSource?> Source(long id,CancellationToken ct){
        await using var cn=Connection();return await cn.QuerySingleOrDefaultAsync<KnowledgeSource>(new CommandDefinition("SELECT id,title,source_uri AS Uri,body,source_sha256 AS Sha,is_active AS Active,created_at AS CreatedAt FROM dms.ai_knowledge_sources WHERE id=@id",new{id},cancellationToken:ct));
    }
    public async Task<long> AddSource(string title, string? uri, string body, bool active, int actor, string? ip, CancellationToken ct) {
        title = title.Trim();
        if (title.Length is < 1 or > 200 || string.IsNullOrWhiteSpace(body) || body.Length > 2000000) throw new InvalidOperationException("Provide a reference title and text (maximum 2 million characters).");
        if (!string.IsNullOrWhiteSpace(uri) && (!System.Uri.TryCreate(uri, UriKind.Absolute, out var address) || address.Scheme != "https" || !string.IsNullOrEmpty(address.UserInfo))) throw new InvalidOperationException("Reference links must use HTTPS without embedded credentials.");
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        await using var cn = Connection(); await cn.OpenAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        var id = await cn.ExecuteScalarAsync<long>(new CommandDefinition("INSERT INTO dms.ai_knowledge_sources(title,source_uri,body,source_sha256,is_active,created_by) VALUES(@title,@uri,@body,@sha,@active,@actor) RETURNING id", new { title, uri, body, sha, active, actor }, tx, cancellationToken: ct));
        var chunks = Chunk(body, await config.GetIntAsync("ai.rag.chunkCharacters", 3000, ct));
        await cn.ExecuteAsync(new CommandDefinition("INSERT INTO dms.ai_document_chunks(knowledge_id,source_sha256,ordinal,page_number,section_heading,content) VALUES(@id,@sha,@Ordinal,@Page,@Section,@Text)", chunks.Select(c => new { id, sha, c.Ordinal, c.Page, c.Section, c.Text }), tx, cancellationToken: ct));
        await cn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT dms.sp_audit_write('AI_REFERENCE',@id::bigint,NULL::text,'AI_REFERENCE_ADD',@detail::text,@actor::integer,NULL::json,NULL::json,NULL::text,@ip::text,NULL::bigint,NULL::bigint)", new { id, actor, ip, detail = $"Reference {title}; source {sha}; active {active}" }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct); return id;
    }
    public async Task SetActive(long id, bool active, int actor, string? ip, CancellationToken ct) {
        await using var cn = Connection(); await cn.OpenAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        if (await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.ai_knowledge_sources SET is_active=@active WHERE id=@id", new { id, active }, tx, cancellationToken: ct)) != 1) throw new InvalidOperationException("Reference not found.");
        await cn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT dms.sp_audit_write('AI_REFERENCE',@id::bigint,NULL::text,'AI_REFERENCE_STATUS',@detail::text,@actor::integer,NULL::json,NULL::json,NULL::text,@ip::text,NULL::bigint,NULL::bigint)", new { id, actor, ip, detail = $"Active {active}" }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
    public async Task<List<RagChunk>> Retrieve(string query, int actor, long? excludedVersion, bool search, CancellationToken ct) {
        if (!search && !await config.GetBoolAsync("ai.rag.enabled", true, ct)) return [];
        var includeDocs = search || await config.GetBoolAsync("ai.rag.includeEffectiveDocuments", true, ct);
        var limit = Math.Clamp(await config.GetIntAsync("ai.rag.candidateLimit", 2000, ct), 100, 10000);
        // Retrieve only current fingerprints and content readable by this common user. Metadata-only grants never expose content.
        var sql = $"""
            WITH allowed AS (
              SELECT {ChunkColumns},d.id AS DocumentId,d.document_number||' / '||d.title AS Title,NULL::text AS Uri
              FROM dms.ai_document_chunks c JOIN dms.document_versions v ON v.id=c.version_id
              JOIN dms.documents d ON d.current_version_id=v.id JOIN dms.document_states s ON s.id=d.current_state_id
              CROSS JOIN LATERAL dms.sp_document_access(d.id,@actor::integer) a
              WHERE @includeDocs AND a.can_view AND a.can_view_content AND c.source_sha256=v.source_sha256
                AND (@excludedVersion IS NULL OR v.id<>@excludedVersion) AND (@search OR s.code='EFFECTIVE')
              UNION ALL
              SELECT {ChunkColumns},NULL::bigint AS DocumentId,k.title,k.source_uri AS Uri
              FROM dms.ai_document_chunks c JOIN dms.ai_knowledge_sources k ON k.id=c.knowledge_id
              WHERE NOT @search AND k.is_active AND c.source_sha256=k.source_sha256
            ) SELECT * FROM allowed
            WHERE EXISTS(SELECT 1 FROM common.users u JOIN common.roles r ON r.roleid=u.role_id WHERE u.pk_user=@actor AND u.isactive=1 AND coalesce(u.isarchived,0)=0 AND NOT coalesce(u.isfirstlogin,false) AND r.isactive=1 AND coalesce(r.isarchived,0)=0)
            ORDER BY ts_rank_cd(to_tsvector('simple',Text),plainto_tsquery('simple',@terms)) DESC,id DESC LIMIT @limit
            """;
        var terms = string.Join(' ', TextIntelligence.Tokens(query).Distinct().Take(40));
        await using var cn = Connection();
        var chunks = (await cn.QueryAsync<RagChunk>(new CommandDefinition(sql, new { actor, includeDocs, excludedVersion, search, terms, limit }, cancellationToken: ct))).ToList();
        if (chunks.Count == 0) return [];
        var lexical = TextIntelligence.Rank(terms, chunks.Select(c => c.Title + " " + c.Section + " " + c.Text).ToList());
        float[]? vector = null;AiProviderOptions? options=null;RetrievalMode="Ranked lexical retrieval";
        try{options=await settings.Embeddings(ct);}catch(InvalidOperationException){RetrievalMode="Lexical retrieval (embedding configuration needs attention)";}
        if (options != null && chunks.Any(c => c.EmbeddingKey == options.Fingerprint)) {
            try { vector = (await client.Embed(options, [query[..Math.Min(query.Length, 6000)]], ct))[0]; RetrievalMode = "Hybrid semantic and lexical retrieval"; }
            catch (InvalidOperationException) { RetrievalMode = "Lexical retrieval (embedding service unavailable)"; }
        } else if (options != null) RetrievalMode = "Lexical retrieval (embeddings pending)";
        for (var i = 0; i < chunks.Count; i++) {
            var semantic = vector != null && chunks[i].EmbeddingKey == options?.Fingerprint ? Cosine(vector, chunks[i].Embedding) : 0;
            chunks[i].Score = semantic >= .35 ? .65 * semantic + .35 * lexical[i] : lexical[i] * (vector == null ? 1 : .35);
        }
        var top = search ? 100 : Math.Clamp(await config.GetIntAsync("ai.rag.topK", 5, ct), 1, 20);
        var maximum = search ? int.MaxValue : Math.Clamp(await config.GetIntAsync("ai.rag.maxContextCharacters", 15000, ct), 1000, 60000);
        var selected = new List<RagChunk>(); var characters = 0;
        foreach (var c in chunks.Where(c => c.Score > 0).OrderByDescending(c => c.Score).DistinctBy(c=>c.Text).Take(top)) {
            if (characters + c.Text.Length > maximum) { if (selected.Count == 0) { c.Text = c.Text[..maximum]; selected.Add(c); } break; }
            characters += c.Text.Length; selected.Add(c);
        }
        return selected;
    }
    public static double Cosine(float[] a, float[]? b) {
        if (b == null || a.Length != b.Length) return 0;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i]; }
        return aa == 0 || bb == 0 ? 0 : Math.Clamp(dot / Math.Sqrt(aa * bb), -1, 1);
    }
    public async Task BackfillEmbeddings(CancellationToken ct) {
        var options = await settings.Embeddings(ct); if (options == null) return;
        AiProviderClient.ValidateEndpoint(options);
        var batch = Math.Clamp(await config.GetIntAsync("ai.embeddings.batchSize", 8, ct), 1, 32);
        await using var cn = Connection();
        var rows = (await cn.QueryAsync<RagChunk>(new CommandDefinition($"SELECT {ChunkColumns} FROM dms.ai_document_chunks c LEFT JOIN dms.document_versions v ON v.id=c.version_id LEFT JOIN dms.ai_knowledge_sources k ON k.id=c.knowledge_id WHERE (c.source_sha256=v.source_sha256 OR k.is_active AND c.source_sha256=k.source_sha256) AND c.embedding_key IS DISTINCT FROM @key ORDER BY c.id LIMIT @batch", new { key = options.Fingerprint, batch }, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return;
        var vectors = await client.Embed(options, rows.Select(c => c.Text).ToList(), ct);
        for (var i = 0; i < rows.Count; i++) await cn.ExecuteAsync(new CommandDefinition("UPDATE dms.ai_document_chunks SET embedding=@vector,embedding_key=@key WHERE id=@id AND source_sha256=@sha", new { vector = vectors[i], key = options.Fingerprint, id = rows[i].Id, sha = rows[i].Sha }, cancellationToken: ct));
    }
    public async Task<AiStatus> Status(CancellationToken ct) {
        AiProviderOptions options; bool configured;
        try { options=await settings.Review(ct);AiProviderClient.ValidateEndpoint(options); configured = true; } catch (InvalidOperationException) {
            var provider=(await config.GetStringAsync("ai.review.provider","",ct))!;
            options=new(provider,"",(await config.GetStringAsync("ai.providers."+provider.ToLowerInvariant()+".model","",ct))!,null);configured=false;
        }
        await using var cn = Connection();
        var counts = await cn.QuerySingleAsync<(int refs, int chunks)>(new CommandDefinition("SELECT (SELECT count(*)::integer FROM dms.ai_knowledge_sources WHERE is_active),(SELECT count(*)::integer FROM dms.ai_document_chunks)", cancellationToken: ct));
        return new(options.Provider, options.Model, configured, await config.GetBoolAsync("ai.review.modelEnabled", false, ct), await config.GetBoolAsync("ai.rag.enabled", true, ct), await config.GetBoolAsync("ai.embeddings.enabled", false, ct), counts.refs, counts.chunks);
    }
}
