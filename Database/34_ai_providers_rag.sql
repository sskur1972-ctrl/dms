BEGIN;
SET LOCAL lock_timeout='15s';
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' THEN RAISE EXCEPTION 'Wrong database'; END IF;
END $guard$;
ALTER TABLE dms.document_intelligence_index ADD COLUMN IF NOT EXISTS format_version integer NOT NULL DEFAULT 1;
CREATE TABLE IF NOT EXISTS dms.ai_knowledge_sources (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 title text NOT NULL CHECK(length(title) BETWEEN 1 AND 200), source_uri text,
 body text NOT NULL CHECK(length(body) BETWEEN 1 AND 2000000), source_sha256 text NOT NULL,
 is_active boolean NOT NULL DEFAULT false, created_by integer NOT NULL REFERENCES common.users(pk_user),
 created_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC')
);
CREATE TABLE IF NOT EXISTS dms.ai_document_chunks (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 version_id bigint REFERENCES dms.document_versions(id) ON DELETE CASCADE,
 knowledge_id bigint REFERENCES dms.ai_knowledge_sources(id) ON DELETE CASCADE,
 source_sha256 text NOT NULL, ordinal integer NOT NULL, page_number integer,
 section_heading text NOT NULL DEFAULT '', content text NOT NULL,
 embedding real[], embedding_key text,
 CHECK((version_id IS NULL) <> (knowledge_id IS NULL)),
 CHECK((embedding IS NULL) = (embedding_key IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ai_chunks_version ON dms.ai_document_chunks(version_id,ordinal) WHERE version_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ai_chunks_knowledge ON dms.ai_document_chunks(knowledge_id,ordinal) WHERE knowledge_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ai_chunks_terms ON dms.ai_document_chunks USING gin(to_tsvector('simple',content));
LOCK TABLE dms.system_config IN SHARE ROW EXCLUSIVE MODE;
SELECT setval(pg_get_serial_sequence('dms.system_config','id'),greatest(coalesce(max(id),1),1),true) FROM dms.system_config;
INSERT INTO dms.system_config(config_key,config_value,value_type,category,description,is_gxp_relevant)
SELECT k,v,t,'ai',description,true FROM (VALUES
 ('ai.providers.openai.endpoint','https://api.openai.com/v1/chat/completions','string','OpenAI chat endpoint'),
 ('ai.providers.openai.model','','string','Approved OpenAI model ID'),
 ('ai.providers.openai.apiKey','','secret','OpenAI key; masked in UI and audit'),
 ('ai.providers.openai.responseFormat','JSON_SCHEMA','string','JSON_SCHEMA, JSON_OBJECT or PROMPT_JSON'),
 ('ai.providers.deepseek.endpoint','https://api.deepseek.com/chat/completions','string','DeepSeek chat endpoint'),
 ('ai.providers.deepseek.model','','string','Approved DeepSeek model ID'),
 ('ai.providers.deepseek.apiKey','','secret','DeepSeek key; masked in UI and audit'),
 ('ai.providers.deepseek.responseFormat','JSON_OBJECT','string','DeepSeek JSON mode'),
 ('ai.providers.ollama.endpoint','http://localhost:11434/api/chat','string','Local Ollama chat endpoint'),
 ('ai.providers.ollama.model','','string','Installed local model name'),
 ('ai.providers.ollama.apiKey','','secret','Optional local proxy authentication'),
 ('ai.providers.ollama.contextTokens','16384','int','Ollama context window; document batching and reference budgets prevent silent input truncation'),
 ('ai.providers.local.endpoint','http://localhost:1234/v1/chat/completions','string','Local OpenAI-compatible server endpoint'),
 ('ai.providers.local.model','','string','Local LM Studio/vLLM model name'),
 ('ai.providers.local.apiKey','','secret','Optional local server authentication'),
 ('ai.providers.local.responseFormat','JSON_SCHEMA','string','JSON_SCHEMA, JSON_OBJECT or PROMPT_JSON based on local model support'),
 ('ai.review.timeoutSeconds','180','int','Per batch AI request timeout'),
 ('ai.review.maxOutputTokens','3000','int','Maximum output tokens per review batch'),
 ('ai.review.maxBatches','150','int','Maximum review batches; uncovered content is reported as PARTIAL'),
 ('ai.rag.enabled','true','bool','Retrieve controlled reference excerpts for AI review'),
 ('ai.rag.includeEffectiveDocuments','true','bool','Include accessible current effective DMS documents as references'),
 ('ai.rag.chunkCharacters','3000','int','Characters per reference chunk; changes take effect when reindexed'),
 ('ai.rag.topK','5','int','Maximum retrieved excerpts per review batch'),
 ('ai.rag.maxContextCharacters','15000','int','Maximum retrieved reference text per batch'),
 ('ai.rag.candidateLimit','2000','int','Maximum ranked candidate chunks per query'),
 ('ai.embeddings.enabled','false','bool','Enable vector semantic search/retrieval; otherwise use ranked lexical RAG'),
 ('ai.embeddings.provider','OLLAMA','string','OLLAMA, OPENAI or LOCAL; separate from review provider'),
 ('ai.embeddings.endpoint','http://localhost:11434/api/embed','string','Full /api/embed or /v1/embeddings endpoint'),
 ('ai.embeddings.model','','string','Installed/approved embedding model'),
 ('ai.embeddings.apiKey','','secret','Optional embedding API key'),
 ('ai.embeddings.timeoutSeconds','120','int','Embedding request timeout'),
 ('ai.embeddings.batchSize','8','int','Embedding chunks per worker cycle')
) AS defaults(k,v,t,description)
WHERE NOT EXISTS(SELECT 1 FROM dms.system_config WHERE lower(btrim(config_key))=lower(k));
UPDATE dms.system_config SET description='OPENAI, DEEPSEEK, OLLAMA, LOCAL or legacy OPENAI_COMPATIBLE' WHERE config_key='ai.review.provider';
UPDATE dms.system_config SET description='Maximum document characters per batch; all batches are tracked for coverage' WHERE config_key='ai.review.maxCharacters';
UPDATE dms.system_config SET description='Use the configured AI model for advisory review; cloud keys or an installed local model required' WHERE config_key='ai.review.modelEnabled';
COMMIT;
