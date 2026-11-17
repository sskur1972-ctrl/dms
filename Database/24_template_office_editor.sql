BEGIN;
ALTER TABLE dms.editor_sessions ADD COLUMN IF NOT EXISTS template_id bigint REFERENCES dms.document_templates(id);
ALTER TABLE dms.editor_sessions ADD COLUMN IF NOT EXISTS browser_origin text;
ALTER TABLE dms.editor_sessions ALTER COLUMN document_id DROP NOT NULL;
ALTER TABLE dms.editor_sessions ALTER COLUMN version_id DROP NOT NULL;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='editor_session_target' AND conrelid='dms.editor_sessions'::regclass) THEN
  ALTER TABLE dms.editor_sessions ADD CONSTRAINT editor_session_target CHECK
   ((template_id IS NULL AND document_id IS NOT NULL AND version_id IS NOT NULL) OR
    (template_id IS NOT NULL AND document_id IS NULL AND version_id IS NULL));
 END IF;
END $$;
CREATE TABLE IF NOT EXISTS dms.template_editor_leases (
 template_id bigint PRIMARY KEY REFERENCES dms.document_templates(id),
 provider text NOT NULL, document_key text NOT NULL, expected_sha text NOT NULL,
 expires_at timestamptz NOT NULL,
 lock_value text, lock_expires_at timestamptz
);
CREATE TABLE IF NOT EXISTS dms.template_editor_commits (
 provider text NOT NULL, event_id text NOT NULL, template_id bigint NOT NULL REFERENCES dms.document_templates(id),
 source_sha text NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(provider,event_id)
);
COMMIT;
