-- Additive editor integration migration. Run manually in the existing DMS database.
BEGIN;
CREATE SCHEMA IF NOT EXISTS dms;
CREATE TABLE IF NOT EXISTS dms.editor_sessions (
 id text PRIMARY KEY, provider text NOT NULL, document_id bigint NOT NULL, version_id bigint NOT NULL,
 user_id integer NOT NULL, can_write boolean NOT NULL, document_key text NOT NULL,
 source_path text NOT NULL, file_name text NOT NULL, expires_at timestamptz NOT NULL,
 last_activity timestamptz NOT NULL DEFAULT now(), closed boolean NOT NULL DEFAULT false);
CREATE INDEX IF NOT EXISTS ix_editor_sessions_version ON dms.editor_sessions(version_id,expires_at);
CREATE TABLE IF NOT EXISTS dms.editor_leases (
 version_id bigint PRIMARY KEY, provider text NOT NULL, document_key text NOT NULL,
 expected_sha text NOT NULL, source_path text NOT NULL, expires_at timestamptz NOT NULL);
CREATE TABLE IF NOT EXISTS dms.editor_commits (
 provider text NOT NULL,event_id text NOT NULL,version_id bigint NOT NULL,source_sha text NOT NULL,
 size_bytes bigint NOT NULL,committed_at timestamptz NOT NULL DEFAULT now(),PRIMARY KEY(provider,event_id));
CREATE TABLE IF NOT EXISTS dms.editor_comment_anchors (
 comment_id bigint PRIMARY KEY,document_id bigint NOT NULL,version_id bigint NOT NULL,
 anchor_type text NOT NULL CHECK(anchor_type IN ('BOOKMARK','QUOTE','SECTION','PAGE')),
 anchor_value text,anchor_text text,page_no integer CHECK(page_no>0),source_sha text,
 created_by integer NOT NULL,created_at timestamptz NOT NULL DEFAULT now());
COMMIT;
