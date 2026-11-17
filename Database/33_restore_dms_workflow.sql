-- Workflow belongs to DMS; shared administration retains canonical identities.
BEGIN;
SET LOCAL search_path=dms,common,public;
SET LOCAL lock_timeout='15s';
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' THEN RAISE EXCEPTION 'Wrong database'; END IF;
END $guard$;
CREATE TEMP TABLE restore_workflow_counts(table_name text PRIMARY KEY,total bigint);
CREATE TEMP TABLE restore_shared_routines AS
SELECT p.oid,md5(pg_get_functiondef(p.oid)) fingerprint FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname='common' AND p.prokind IN('f','p') AND p.proname NOT LIKE 'sp_dms_%';
DO $restore$ DECLARE entity text; total bigint; r record; definition text; BEGIN
 FOREACH entity IN ARRAY ARRAY['document_access_rules','document_state_history','document_state_transitions','document_states','document_workflow_stages','document_workflow_instances'] LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE n.nspname='common' AND c.relname=entity AND c.relkind='r') THEN
   RAISE EXCEPTION 'Expected common workflow table missing: %',entity; END IF;
  IF NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE n.nspname='dms' AND c.relname=entity AND c.relkind='v') THEN
   RAISE EXCEPTION 'Expected DMS compatibility view missing: %',entity; END IF;
  EXECUTE format('SELECT count(*) FROM common.%I',entity) INTO total;
  INSERT INTO restore_workflow_counts VALUES(entity,total);
  -- No CASCADE: unexpected dependencies abort instead of being deleted.
  EXECUTE format('DROP VIEW dms.%I',entity);
  EXECUTE format('ALTER TABLE common.%I SET SCHEMA dms',entity);
  -- Keep old qualified reads compatible while DMS routines target real tables.
  EXECUTE format('CREATE VIEW common.%I AS SELECT * FROM dms.%I',entity,entity);
 END LOOP;
 FOR r IN SELECT p.oid FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE p.prokind='f' AND (n.nspname='dms' OR n.nspname='common' AND p.proname LIKE 'sp_dms_%') LOOP
  definition=pg_get_functiondef(r.oid);
  FOREACH entity IN ARRAY ARRAY['document_access_rules','document_state_history','document_state_transitions','document_states','document_workflow_stages','document_workflow_instances'] LOOP
   definition=regexp_replace(definition,'\mcommon[.]'||entity||'\M','dms.'||entity,'g');
  END LOOP;
  IF definition<>pg_get_functiondef(r.oid) THEN EXECUTE definition; END IF;
 END LOOP;
 FOR r IN SELECT * FROM restore_workflow_counts LOOP
  EXECUTE format('SELECT count(*) FROM dms.%I',r.table_name) INTO total;
  IF total<>r.total THEN RAISE EXCEPTION 'Workflow rows changed: %',r.table_name; END IF;
 END LOOP;
END $restore$;
DO $verify$ BEGIN
 IF EXISTS(SELECT 1 FROM restore_shared_routines b LEFT JOIN pg_proc p ON p.oid=b.oid
  WHERE p.oid IS NULL OR b.fingerprint<>md5(pg_get_functiondef(p.oid))) THEN
  RAISE EXCEPTION 'Existing shared common routine changed'; END IF;
 IF (SELECT count(*) FROM information_schema.tables WHERE table_schema='dms' AND table_type='BASE TABLE'
  AND table_name IN('document_access_rules','document_state_history','document_state_transitions','document_states','document_workflow_stages','document_workflow_instances'))<>6 THEN
  RAISE EXCEPTION 'DMS workflow restoration incomplete'; END IF;
END $verify$;
INSERT INTO dms.common_auth_migration_status(description)
VALUES('33: workflow and access-matrix tables restored to dms; common identities, roles, permissions and shared administration retained unchanged.');
COMMIT;
