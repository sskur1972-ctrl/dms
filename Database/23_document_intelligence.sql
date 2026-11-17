BEGIN;
LOCK TABLE dms.system_config IN SHARE ROW EXCLUSIVE MODE;
SELECT setval(pg_get_serial_sequence('dms.system_config','id'),greatest(coalesce(max(id),1),1),true) FROM dms.system_config;
CREATE TABLE IF NOT EXISTS dms.document_intelligence_index (
 version_id bigint PRIMARY KEY REFERENCES dms.document_versions(id),
 source_sha256 text NOT NULL, extracted_text text NOT NULL DEFAULT '',
 suggested_keywords text NOT NULL DEFAULT '', engine text NOT NULL,
 status text NOT NULL CHECK(status IN ('READY','FAILED')), message text,
 indexed_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC')
);
CREATE TABLE IF NOT EXISTS dms.document_intelligence_reviews (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 version_id bigint NOT NULL REFERENCES dms.document_versions(id),
 source_sha256 text NOT NULL, actor_user_id integer NOT NULL,
 engine text NOT NULL, findings_json jsonb NOT NULL,
 created_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC')
);
INSERT INTO dms.system_config(config_key,config_value,value_type,category,description,is_gxp_relevant)
SELECT k,v,t,'ai',description,true FROM (VALUES
 ('ai.trends.enabled','true','bool','Document activity trends and anomaly indicators'),
 ('ai.ocr.endpoint','http://localhost:8091','string','Private OCR service endpoint'),
 ('ai.ocr.apiKey','','secret','Private OCR service key; never displayed'),
 ('ai.autoIndex.batchSize','2','int','Maximum files indexed per worker cycle'),
 ('ai.review.modelEnabled','false','bool','Use an approved local model for additional advisory findings'),
 ('ai.review.provider','OPENAI_COMPATIBLE','string','OPENAI_COMPATIBLE or OLLAMA'),
 ('ai.review.endpoint','','string','Approved AI endpoint, including /v1/chat/completions or /api/generate'),
 ('ai.review.apiKey','','secret','Approved AI service API key'),
 ('ai.review.model','','string','Approved model deployment name'),
 ('ai.review.maxCharacters','18000','int','Maximum document text sent to the local model')
) AS defaults(k,v,t,description)
WHERE NOT EXISTS(SELECT 1 FROM dms.system_config WHERE lower(btrim(config_key))=lower(k));
COMMIT;
