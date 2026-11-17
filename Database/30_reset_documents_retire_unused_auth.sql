-- User-authorized fresh DMS reset in UnifiedPlatform_dms only.
-- Preserve shared common accounts, roles, permissions, routines and master data.
BEGIN;
SET LOCAL search_path=dms,common,public;
SET LOCAL lock_timeout='15s';
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' THEN RAISE EXCEPTION 'Wrong database'; END IF;
 IF EXISTS(SELECT 1 FROM dms.user_mfa_secrets) OR EXISTS(SELECT 1 FROM dms.users_auth_profile WHERE mfa_enabled) THEN
  RAISE EXCEPTION 'MFA is in use; deletion refused'; END IF;
END $guard$;
CREATE TEMP TABLE shared_definition_baseline AS
SELECT p.oid,md5(pg_get_functiondef(p.oid)) fingerprint FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname='common' AND p.prokind IN('f','p') AND p.proname<>'sp_dms_role_permission_bridge';
CREATE TEMP TABLE shared_account_baseline AS
SELECT 'users'::text entity,count(*) total FROM common.users
UNION ALL SELECT 'roles',count(*) FROM common.roles
UNION ALL SELECT 'permissions',count(*) FROM common.permissions
UNION ALL SELECT 'role_permission',count(*) FROM common.role_permission;

-- Explicit list, without CASCADE. Unanticipated dependencies abort the reset.
-- Keep IDs monotonic so old document links cannot point to new documents.
-- A one-time, explicitly authorized reset temporarily suspends only these
-- three truncate guards; normal append-only protections are restored below.
ALTER TABLE dms.audit_trail DISABLE TRIGGER trg_audit_trail_no_truncate;
ALTER TABLE dms.document_signatures DISABLE TRIGGER trg_document_signatures_no_truncate;
ALTER TABLE dms.print_log DISABLE TRIGGER trg_print_log_no_truncate;
TRUNCATE TABLE
 dms.documents,dms.document_versions,dms.document_content,dms.attachments,
 dms.document_edit_locks,dms.document_intelligence_index,dms.document_intelligence_reviews,
 dms.document_links,dms.document_rendition_jobs,dms.document_review_comments,
 dms.document_share_links,dms.document_signatures,dms.document_state_history,
 dms.document_temp_revision_log,dms.document_training_links,dms.document_workflow_instances,
 dms.notifications,dms.print_controlled_copies,dms.print_log,dms.print_requests,
 dms.editor_comment_anchors,dms.editor_commits,dms.editor_leases,
 dms.audit_trail_reviews,dms.audit_trail,dms.legacy_import_rows,dms.legacy_import_batches
 CONTINUE IDENTITY;
ALTER TABLE dms.audit_trail ENABLE TRIGGER trg_audit_trail_no_truncate;
ALTER TABLE dms.document_signatures ENABLE TRIGGER trg_document_signatures_no_truncate;
ALTER TABLE dms.print_log ENABLE TRIGGER trg_print_log_no_truncate;
-- Template editing sessions are independent of the deleted document sessions.
DELETE FROM dms.editor_sessions WHERE template_id IS NULL;
DELETE FROM dms.integration_outbox WHERE lower(entity_type) IN('document','documentversion','document_version','documents','document_versions');
UPDATE dms.audit_chain_head SET last_audit_id=NULL,last_row_hash=NULL,row_count=0,updated_at=LOCALTIMESTAMP;
UPDATE dms.document_number_sequences SET sequence_value=0,updated_at=LOCALTIMESTAMP;

DO $bridge$ DECLARE definition text; BEGIN
 SELECT pg_get_functiondef('common.sp_dms_role_permission_bridge()'::regprocedure) INTO definition;
 definition=replace(definition,'DELETE FROM dms.role_permission_auth_profile WHERE common_id=OLD.pk_role_permission;','');
 EXECUTE definition;
END $bridge$;
DROP TABLE dms.role_permission_auth_profile;
DROP TABLE dms.permission_verification;
DROP TABLE dms.user_login_attempts;
DROP TABLE dms.user_mfa_secrets;

DO $verify$ DECLARE r record; actual bigint; BEGIN
 IF EXISTS(SELECT 1 FROM dms.documents) OR EXISTS(SELECT 1 FROM dms.document_versions)
  OR EXISTS(SELECT 1 FROM dms.audit_trail) THEN RAISE EXCEPTION 'Document reset incomplete'; END IF;
 IF (SELECT count(*) FROM pg_trigger WHERE tgname IN('trg_audit_trail_no_truncate','trg_document_signatures_no_truncate','trg_print_log_no_truncate') AND tgenabled='O')<>3 THEN
  RAISE EXCEPTION 'Append-only guards were not restored'; END IF;
 FOR r IN SELECT * FROM shared_account_baseline LOOP
  EXECUTE format('SELECT count(*) FROM common.%I',r.entity) INTO actual;
  IF actual<>r.total THEN RAISE EXCEPTION 'Shared account records changed: %',r.entity; END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM shared_definition_baseline b LEFT JOIN pg_proc p ON p.oid=b.oid
  WHERE p.oid IS NULL OR b.fingerprint<>md5(pg_get_functiondef(p.oid))) THEN
  RAISE EXCEPTION 'Shared common routine changed'; END IF;
 IF EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN('common','dms') AND p.prokind='f'
   AND p.prosrc ~ 'dms[.](role_permission_auth_profile|permission_verification|user_login_attempts|user_mfa_secrets)') THEN
  RAISE EXCEPTION 'Retired authentication dependency remains'; END IF;
END $verify$;
INSERT INTO dms.common_auth_migration_status(description)
VALUES('30: user-authorized old DMS documents and transaction/audit reset; four unused authentication storage tables retired; shared common records and master definitions retained.');
COMMIT;
