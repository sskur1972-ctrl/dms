-- Retire redundant role assignments: the user's primary common role governs DMS.
BEGIN;
SET LOCAL search_path=dms,common,public;
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' OR to_regclass('dms.user_login_attempts') IS NOT NULL THEN
  RAISE EXCEPTION 'Requires the authorized migration 30 reset'; END IF;
END $guard$;
CREATE TEMP TABLE primary_role_shared_baseline AS
SELECT p.oid,md5(pg_get_functiondef(p.oid)) fingerprint FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname='common' AND p.prokind IN('f','p')
AND p.proname NOT IN('sp_dms_deleterole','sp_dms_updateuser','sp_dms_users_addupdate','sp_dms_user_role_ids');
CREATE OR REPLACE FUNCTION common.sp_dms_user_role_ids(p_user_id integer)
RETURNS integer[] LANGUAGE sql STABLE SET search_path=dms,common,public AS $fn$
 SELECT coalesce(array_agg(role_id) FILTER(WHERE role_id IS NOT NULL),'{}')
 FROM common.dms_users WHERE pk_user=p_user_id;
$fn$;
DO $rewrite$ DECLARE r record; definition text; BEGIN
 FOR r IN SELECT p.oid,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE (n.nspname='common' AND p.proname IN('sp_dms_deleterole','sp_dms_updateuser','sp_dms_users_addupdate'))
   OR (n.nspname='dms' AND p.proname IN('sp_deleterole','sp_updateuser','sp_users_addupdate','sp_report_user_access')) LOOP
  definition=pg_get_functiondef(r.oid);
  IF r.proname IN('sp_dms_deleterole','sp_deleterole') THEN
   definition=regexp_replace(definition,'\s*IF EXISTS \(SELECT 1 FROM user_roles ur[\s\S]*?THEN RETURN 0; END IF;','','g');
  ELSIF r.proname IN('sp_dms_updateuser','sp_updateuser') THEN
   definition=regexp_replace(definition,'\s*-- keep user_roles[\s\S]*?\s+RETURN v_rows;',chr(10)||'    RETURN v_rows;','g');
  ELSIF r.proname IN('sp_dms_users_addupdate','sp_users_addupdate') THEN
   definition=regexp_replace(definition,'\s*IF p_roleid IS NOT NULL THEN\s*INSERT INTO user_roles[\s\S]*?END IF;','','g');
  ELSE
   definition=replace(definition,'SELECT u.role_id UNION SELECT ur.role_id FROM user_roles ur WHERE ur.user_id = u.pk_user','SELECT u.role_id');
  END IF;
  IF definition ~ '\muser_roles\M' THEN RAISE EXCEPTION 'Role dependency remains in %',r.proname; END IF;
  EXECUTE definition;
 END LOOP;
END $rewrite$;
DROP TABLE dms.user_roles;
DO $verify$ BEGIN
 IF EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN('common','dms') AND p.prokind='f' AND p.prosrc ~ '\muser_roles\M') THEN
  RAISE EXCEPTION 'Supplemental role storage dependency remains'; END IF;
 IF EXISTS(SELECT 1 FROM primary_role_shared_baseline b LEFT JOIN pg_proc p ON p.oid=b.oid
  WHERE p.oid IS NULL OR b.fingerprint<>md5(pg_get_functiondef(p.oid))) THEN
  RAISE EXCEPTION 'Pre-existing shared common routine changed'; END IF;
END $verify$;
INSERT INTO dms.common_auth_migration_status(description)
VALUES('31: primary common.users.role_id is the sole role assignment source; redundant dms.user_roles and its dependencies retired.');
COMMIT;
