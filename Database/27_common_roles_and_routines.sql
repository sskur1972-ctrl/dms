-- Explicit migration for UnifiedPlatform_dms. Existing shared common tables and
-- routines are preserved. DMS domain IDs stay stable for audit/signature history.
BEGIN;
SET LOCAL search_path=dms,common,public;
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' OR to_regclass('dms.users_auth_profile') IS NULL THEN RAISE EXCEPTION 'Requires migrated UnifiedPlatform_dms'; END IF;
 IF to_regclass('dms.common_auth_routine_map') IS NOT NULL THEN RAISE EXCEPTION 'Migration 27 already applied'; END IF;
END $guard$;
CREATE TEMP TABLE shared_routine_baseline AS
 SELECT p.oid,md5(pg_get_functiondef(p.oid)) fingerprint FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='common' AND p.prokind IN('f','p');
CREATE TEMP TABLE dms_routine_snapshot AS
 SELECT p.oid,p.proname,pg_get_function_identity_arguments(p.oid) args,pg_get_functiondef(p.oid) definition
 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='dms' AND p.prokind='f';
CREATE TEMP TABLE requested_role_map(role_code text PRIMARY KEY,common_name text NOT NULL,common_id integer,previous_common_id integer);
INSERT INTO requested_role_map(role_code,common_name) VALUES
 ('ADMIN','Admin Department'),('APPROVER','Head of Department'),
 ('AUDITOR','Auditor Read only'),('OPERATOR','Operator'),
 ('QA_CONTROLLER','QA Manager'),('TECHNICAL_DIRECTOR','Technical Director');
DO $map$ DECLARE map_item record; matches integer; matched integer;
BEGIN
 FOR map_item IN SELECT * FROM requested_role_map LOOP
  SELECT count(*),min(roleid) INTO matches,matched FROM common.roles
   WHERE lower(regexp_replace(trim(role_name),'\s+',' ','g'))=lower(map_item.common_name) AND isactive=1 AND coalesce(isarchived,0)=0;
  IF matches<>1 THEN RAISE EXCEPTION 'Missing or ambiguous active shared role: %',map_item.common_name; END IF;
  UPDATE requested_role_map SET common_id=matched,previous_common_id=(SELECT common_id FROM dms.roles_auth_profile WHERE role_code=map_item.role_code) WHERE role_code=map_item.role_code;
 END LOOP;
 INSERT INTO dms.roles_auth_profile(role_code,role_name,isactive,isarchived,is_system,common_id)
 SELECT m.role_code,m.common_name,true,false,true,m.common_id FROM requested_role_map m
 WHERE NOT EXISTS(SELECT 1 FROM dms.roles_auth_profile r WHERE r.role_code=m.role_code);
 -- Add only DMS-scoped grants; unrelated shared application grants stay intact.
 INSERT INTO common.role_permission(role_id,permission_id)
 SELECT DISTINCT m.common_id,g.permission_id FROM requested_role_map m JOIN common.role_permission g ON g.role_id=m.previous_common_id
 JOIN dms.permissions_auth_profile p ON p.common_id=g.permission_id
 WHERE m.role_code<>'AUDITOR' AND NOT EXISTS(SELECT 1 FROM common.role_permission x WHERE x.role_id=m.common_id AND x.permission_id=g.permission_id);
 -- Read-only auditor: preserve the old read/export grants, excluding write actions.
 INSERT INTO common.role_permission(role_id,permission_id)
 SELECT DISTINCT m.common_id,g.permission_id FROM requested_role_map m JOIN common.role_permission g ON g.role_id=m.previous_common_id
 JOIN dms.permissions_auth_profile p ON p.common_id=g.permission_id
 WHERE m.role_code='AUDITOR' AND p.permission_code IN('document.view','audit.view','audit.export','report.view','report.export','print.log.view','errorlog.view')
 AND NOT EXISTS(SELECT 1 FROM common.role_permission x WHERE x.role_id=m.common_id AND x.permission_id=g.permission_id);
 DELETE FROM common.role_permission g USING requested_role_map m,dms.permissions_auth_profile p
 WHERE m.role_code='AUDITOR' AND g.role_id=m.common_id AND g.permission_id=p.common_id
 AND p.permission_code NOT IN('document.view','audit.view','audit.export','report.view','report.export','print.log.view','errorlog.view');
 -- OPERATOR starts with the existing VIEWER's DMS grants, without author/reviewer rights.
 INSERT INTO common.role_permission(role_id,permission_id)
 SELECT DISTINCT m.common_id,g.permission_id FROM requested_role_map m
 JOIN dms.roles_auth_profile v ON v.role_code='VIEWER' JOIN common.role_permission g ON g.role_id=v.common_id
 JOIN dms.permissions_auth_profile p ON p.common_id=g.permission_id
 WHERE m.role_code='OPERATOR' AND NOT EXISTS(SELECT 1 FROM common.role_permission x WHERE x.role_id=m.common_id AND x.permission_id=g.permission_id);
 UPDATE common.users u SET role_id=m.common_id FROM requested_role_map m
 WHERE m.previous_common_id IS NOT NULL AND u.role_id=m.previous_common_id AND m.previous_common_id<>m.common_id;
 UPDATE dms.roles_auth_profile r SET common_id=m.common_id FROM requested_role_map m WHERE r.role_code=m.role_code;
 UPDATE dms.role_permission_auth_profile g SET common_id=(SELECT min(c.pk_role_permission) FROM common.role_permission c WHERE c.role_id=r.common_id AND c.permission_id=p.common_id)
 FROM dms.roles_auth_profile r,dms.permissions_auth_profile p WHERE r.pk_role=g.role_id AND p.pk_permission=g.permission_id;
 UPDATE dms.users_auth_profile p SET role_id=r.pk_role FROM common.users c,dms.roles_auth_profile r WHERE c.pk_user=p.common_id AND r.common_id=c.role_id;
END $map$;

-- New projections in common read its canonical tables and add only DMS-specific
-- security metadata and stable domain IDs. Compatibility views remain in dms.
DO $views$ DECLARE entity text; definition text; col record;
BEGIN
 FOREACH entity IN ARRAY ARRAY['users','roles','permissions','role_permission'] LOOP
  SELECT pg_get_viewdef(('dms.'||entity)::regclass,true) INTO definition;
  IF entity='users' THEN
   definition=replace(definition,'p.role_id,','mapped_role.pk_role AS role_id,');
   definition=regexp_replace(definition,';\s*$','');
   definition=definition||' LEFT JOIN dms.roles_auth_profile mapped_role ON mapped_role.common_id=c.role_id';
  END IF;
  EXECUTE format('CREATE VIEW common.%I AS %s','dms_'||entity,definition);
  FOR col IN SELECT a.attname,pg_get_expr(d.adbin,d.adrelid) def FROM pg_attribute a JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum WHERE a.attrelid=('dms.'||entity)::regclass LOOP
   EXECUTE format('ALTER VIEW common.%I ALTER COLUMN %I SET DEFAULT %s','dms_'||entity,col.attname,col.def);
  END LOOP;
  EXECUTE format('CREATE TRIGGER dms_auth_write INSTEAD OF INSERT OR UPDATE OR DELETE ON common.%I FOR EACH ROW EXECUTE FUNCTION common.%I()','dms_'||entity,'sp_dms_'||entity||'_bridge');
  EXECUTE format('CREATE OR REPLACE VIEW dms.%I AS SELECT * FROM common.%I',entity,'dms_'||entity);
 END LOOP;
END $views$;

CREATE TABLE dms.common_auth_routine_map(
 legacy_name text NOT NULL,common_name text NOT NULL,identity_arguments text NOT NULL,
 PRIMARY KEY(legacy_name,identity_arguments)
);
INSERT INTO dms.common_auth_routine_map
SELECT proname,'sp_dms_'||regexp_replace(proname,'^(sp_dms_|sp_|fn_)',''),args FROM dms_routine_snapshot WHERE proname IN(
 'fn_user_has_permission','fn_user_role_codes','fn_user_role_ids','fn_users_with_permission',
 'sp_addnewrole','sp_addpermission','sp_addpermissionforrole','sp_addpermissionverification',
 'sp_api_userdetailbyidpassword_get','sp_deletepermission','sp_deletepermissionforroleid','sp_deleterole','sp_deleterolepermission','sp_dms_apply_v2_permission_grants',
 'sp_getcurrentuser_permissionforroles','sp_getpasswordhistory','sp_getpermissionalredayexisted','sp_getpermissionbyid','sp_getpermissionlist',
 'sp_getpermissionsbindwithroleid','sp_getpermissionsforroleid','sp_getpermissionsforuser','sp_getrolelist','sp_getrolenamebyid','sp_getrolenamebyname',
 'sp_getuser_index','sp_getuserdetailsbyid','sp_getuserdetailsbyidandotp','sp_getuserforlogin','sp_getuserslist','sp_getuserusingusername','sp_getuserusingusernameandemail',
 'sp_getverifiedpermissionbyid','sp_registerloginresult','sp_resetpasswordbyid','sp_roles_getrolesfordropdown','sp_updatepasswordbyid','sp_updatepermission',
 'sp_updaterole','sp_updateuser','sp_users_addupdate','sp_users_delete','sp_checkemailexists','sp_updateotpbyid','sp_user_pick_list'
);
CREATE FUNCTION pg_temp.sp_dms_rewrite_auth(sql text,replace_calls boolean) RETURNS text LANGUAGE plpgsql AS $rewrite$
DECLARE r record;
BEGIN
 sql=regexp_replace(sql,'(\mFROM\M|\mJOIN\M|\mUPDATE\M|\mINTO\M)\s+(?:dms\.)?(users|roles|permissions|role_permission)\M','\1 common.dms_\2','gi');
 sql=regexp_replace(sql,'(?<![\w.])(users|roles|permissions|role_permission)\.','dms_\1.','gi');
 sql=regexp_replace(sql,'(?:dms\.)?(users|roles|permissions|role_permission)%ROWTYPE','common.dms_\1%ROWTYPE','gi');
 IF replace_calls THEN
  FOR r IN SELECT DISTINCT legacy_name,common_name FROM dms.common_auth_routine_map LOOP
   sql=regexp_replace(sql,'(?<![\w.])(?:dms\.)?'||r.legacy_name||'\s*\(','common.'||r.common_name||'(','gi');
  END LOOP;
 END IF;
 RETURN sql;
END $rewrite$;
DO $routines$ DECLARE r record; definition text;
BEGIN
 -- First create all new entry points; only then rewrite cross-function calls.
 FOR r IN SELECT s.*,m.common_name FROM dms_routine_snapshot s JOIN dms.common_auth_routine_map m ON m.legacy_name=s.proname AND m.identity_arguments=s.args LOOP
  IF EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='common' AND p.proname=r.common_name) THEN RAISE EXCEPTION 'Shared routine already exists: %',r.common_name; END IF;
  definition=pg_temp.sp_dms_rewrite_auth(r.definition,false);
  definition=replace(definition,'CREATE OR REPLACE FUNCTION dms.'||r.proname||'(','CREATE FUNCTION common.'||r.common_name||'(');
  EXECUTE definition;
  EXECUTE format('ALTER FUNCTION common.%I(%s) SET search_path=dms,common,public',r.common_name,r.args);
 END LOOP;
 FOR r IN SELECT s.*,m.common_name FROM dms_routine_snapshot s JOIN dms.common_auth_routine_map m ON m.legacy_name=s.proname AND m.identity_arguments=s.args LOOP
  definition=replace(r.definition,'CREATE OR REPLACE FUNCTION dms.'||r.proname||'(','CREATE OR REPLACE FUNCTION common.'||r.common_name||'(');
  definition=pg_temp.sp_dms_rewrite_auth(definition,true);
  EXECUTE definition;
  EXECUTE format('ALTER FUNCTION common.%I(%s) SET search_path=dms,common,public',r.common_name,r.args);
 END LOOP;
 -- Business routines stay in dms but explicitly use common auth projections and APIs.
 FOR r IN SELECT * FROM dms_routine_snapshot LOOP
  definition=pg_temp.sp_dms_rewrite_auth(r.definition,true);
  -- Keep each legacy function's public name; rewritten calls target common.
  definition=regexp_replace(definition,'^CREATE OR REPLACE FUNCTION common\.[^(]+\(','CREATE OR REPLACE FUNCTION dms.'||r.proname||'(');
  IF definition<>r.definition THEN EXECUTE definition; END IF;
 END LOOP;
END $routines$;

-- Role lookup reflects common.users.role_id immediately. Old primary assignments
-- are not reinterpreted as extra grants after a shared-role change.
CREATE OR REPLACE FUNCTION common.sp_dms_user_role_ids(p_user_id integer) RETURNS integer[] LANGUAGE sql STABLE SET search_path=dms,common,public AS $fn$
 SELECT coalesce(array_agg(DISTINCT x.role_id) FILTER(WHERE x.role_id IS NOT NULL),'{}')
 FROM(SELECT role_id FROM common.dms_users WHERE pk_user=p_user_id
 UNION SELECT ur.role_id FROM dms.user_roles ur JOIN dms.users_auth_profile profile ON profile.pk_user=ur.user_id
 JOIN common.dms_users u ON u.pk_user=ur.user_id
 WHERE ur.user_id=p_user_id AND (ur.valid_from IS NULL OR ur.valid_from<=CURRENT_DATE) AND (ur.valid_to IS NULL OR ur.valid_to>=CURRENT_DATE)
 AND (ur.role_id IS DISTINCT FROM profile.role_id OR ur.role_id IS NOT DISTINCT FROM u.role_id)) x;
$fn$;
CREATE OR REPLACE FUNCTION common.sp_dms_user_role_codes(p_user_id integer) RETURNS text LANGUAGE sql STABLE AS $fn$
 SELECT string_agg(DISTINCT coalesce(role_code,role_name),',') FROM common.dms_roles
 WHERE pk_role=ANY(common.sp_dms_user_role_ids(p_user_id)) AND isactive AND NOT isarchived;
$fn$;
CREATE OR REPLACE FUNCTION common.sp_dms_getpermissionsforuser(p_user_id integer)
RETURNS TABLE(roles_id integer,id numeric,permission character varying,permission_code character varying) LANGUAGE sql STABLE AS $fn$
 SELECT DISTINCT ON(p.pk_permission) g.role_id,p.pk_permission::numeric,p.permission,p.permission_code
 FROM common.dms_role_permission g JOIN common.dms_roles r ON r.pk_role=g.role_id AND r.isactive AND NOT r.isarchived
 JOIN common.dms_permissions p ON p.pk_permission=g.permission_id AND p.isactive AND NOT p.isarchived
 WHERE g.role_id=ANY(common.sp_dms_user_role_ids(p_user_id)) ORDER BY p.pk_permission,g.role_id;
$fn$;
CREATE OR REPLACE FUNCTION dms.fn_user_role_ids(p_user_id integer) RETURNS integer[] LANGUAGE sql STABLE AS $fn$ SELECT common.sp_dms_user_role_ids(p_user_id); $fn$;
CREATE OR REPLACE FUNCTION dms.fn_user_role_codes(p_user_id integer) RETURNS text LANGUAGE sql STABLE AS $fn$ SELECT common.sp_dms_user_role_codes(p_user_id); $fn$;
CREATE OR REPLACE FUNCTION dms.sp_getpermissionsforuser(p_user_id integer)
RETURNS TABLE(roles_id integer,id numeric,permission character varying,permission_code character varying) LANGUAGE sql STABLE AS $fn$ SELECT * FROM common.sp_dms_getpermissionsforuser(p_user_id); $fn$;
DO $verify$ BEGIN
 IF EXISTS(SELECT 1 FROM shared_routine_baseline b JOIN pg_proc p ON p.oid=b.oid WHERE b.fingerprint<>md5(pg_get_functiondef(p.oid))) THEN RAISE EXCEPTION 'Existing shared routine changed; rollback'; END IF;
 IF EXISTS(SELECT 1 FROM dms.roles_auth_profile WHERE role_code IN('AUTHOR','REVIEWER','VIEWER') AND common_id IS NULL) THEN RAISE EXCEPTION 'Unspecified role lost'; END IF;
END $verify$;
INSERT INTO dms.common_auth_migration_status(description) VALUES('27: requested shared role mappings and common.sp_dms authentication APIs; shared definitions and historical domain IDs preserved.');
COMMIT;
