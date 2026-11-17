-- Shared administration owns account policies and identity CRUD.
-- No columns or history tables are added to existing common tables.
-- Historical workflow relocation is reversed by migration 33. Apply 33 after
-- this migration: workflow belongs in dms, shared identities remain in common.
BEGIN;
SET LOCAL search_path=dms,common,public;
SET LOCAL lock_timeout='15s';
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' OR to_regclass('dms.user_roles') IS NOT NULL THEN
  RAISE EXCEPTION 'Requires UnifiedPlatform_dms after migration 31'; END IF;
END $guard$;
CREATE TEMP TABLE shared_routine_baseline AS
SELECT p.oid,md5(pg_get_functiondef(p.oid)) fingerprint FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname='common' AND p.prokind IN('f','p') AND NOT EXISTS
 (SELECT 1 FROM dms.common_auth_routine_map m WHERE m.common_name=p.proname AND m.identity_arguments=pg_get_function_identity_arguments(p.oid))
AND p.proname NOT IN('sp_dms_users_bridge','sp_dms_roles_bridge','sp_dms_permissions_bridge','sp_dms_role_permission_bridge');
CREATE TEMP TABLE identity_user_map AS SELECT pk_user legacy_id,common_id canonical_id FROM dms.users_auth_profile;
CREATE TEMP TABLE identity_role_map AS SELECT pk_role legacy_id,common_id canonical_id FROM dms.roles_auth_profile;
CREATE TEMP TABLE identity_fk_snapshot AS
SELECT x.conrelid,x.conname,x.confrelid,pg_get_constraintdef(x.oid) definition,
 a.attname column_name,CASE x.confrelid WHEN 'dms.users_auth_profile'::regclass THEN 'users' ELSE 'roles' END entity
FROM pg_constraint x JOIN pg_attribute a ON a.attrelid=x.conrelid AND a.attnum=x.conkey[1]
JOIN pg_class c ON c.oid=x.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE x.contype='f' AND x.confrelid IN('dms.users_auth_profile'::regclass,'dms.roles_auth_profile'::regclass)
 AND n.nspname='dms' AND c.relname NOT IN('users_auth_profile','roles_auth_profile','permissions_auth_profile');
DO $detach$ DECLARE r record; BEGIN
 FOR r IN SELECT * FROM identity_fk_snapshot LOOP
  EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I',r.conrelid::regclass,r.conname);
 END LOOP;
END $detach$;
CREATE TEMP TABLE identity_user_columns AS
SELECT conrelid table_id,column_name FROM identity_fk_snapshot WHERE entity='users'
UNION
SELECT c.oid,a.attname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid
WHERE n.nspname='dms' AND c.relkind='r' AND a.attnum>0 AND NOT a.attisdropped
 AND c.relname NOT IN('users_auth_profile','roles_auth_profile','permissions_auth_profile')
 AND a.atttypid IN('integer'::regtype,'bigint'::regtype)
 AND a.attname IN('created_by','updated_by','modified_by','user_id','actor_user_id','on_behalf_of_user_id','assigned_by','granted_by');
DO $remap$ DECLARE r record; BEGIN
 FOR r IN SELECT * FROM identity_user_columns LOOP
  EXECUTE format('UPDATE %s t SET %I=m.canonical_id FROM identity_user_map m WHERE t.%I=m.legacy_id',r.table_id::regclass,r.column_name,r.column_name);
 END LOOP;
 FOR r IN SELECT * FROM identity_fk_snapshot WHERE entity='roles' LOOP
  EXECUTE format('UPDATE %s t SET %I=m.canonical_id FROM identity_role_map m WHERE t.%I=m.legacy_id',r.conrelid::regclass,r.column_name,r.column_name);
 END LOOP;
 FOR r IN SELECT * FROM identity_fk_snapshot LOOP
  r.definition=regexp_replace(r.definition,'REFERENCES (dms[.])?users_auth_profile\(pk_user\)','REFERENCES common.users(pk_user)');
  r.definition=regexp_replace(r.definition,'REFERENCES (dms[.])?roles_auth_profile\(pk_role\)','REFERENCES common.roles(roleid)');
  EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I %s',r.conrelid::regclass,r.conname,r.definition);
 END LOOP;
END $remap$;

-- Retain SQL result shapes for callers while sourcing identities directly from
-- common. Absent, centrally managed metadata is returned as typed NULL/default.
DO $views$ DECLARE entity text; keycol text; commonkey text; col record; r record; expr text; fields text; role_case text; permission_case text; module_id integer; BEGIN
 SELECT 'CASE c.roleid '||string_agg(format('WHEN %s THEN %L ',common_id,role_code),'')||'ELSE NULL END'
 INTO role_case FROM dms.roles_auth_profile;
 SELECT 'CASE lower(trim(c.permission)) '||string_agg(format('WHEN %L THEN %L ',lower(trim(permission)),permission_code),'')||'ELSE NULL END'
 INTO permission_case FROM dms.permissions_auth_profile;
 SELECT min(m.module_id) INTO module_id FROM common.module_master m WHERE lower(trim(m.module_name))='dms' AND coalesce(is_archived,0)=0;
 IF module_id IS NULL THEN RAISE EXCEPTION 'Common DMS module missing'; END IF;
 FOREACH entity IN ARRAY ARRAY['users','roles','permissions'] LOOP
  keycol=CASE entity WHEN 'users' THEN 'pk_user' WHEN 'roles' THEN 'pk_role' ELSE 'pk_permission' END;
  commonkey=CASE entity WHEN 'roles' THEN 'roleid' ELSE keycol END;
  fields='';
  FOR col IN SELECT a.attname,format_type(a.atttypid,a.atttypmod) typ FROM pg_attribute a
   WHERE a.attrelid=('dms.'||entity||'_auth_profile')::regclass AND a.attnum>0 AND NOT a.attisdropped AND a.attname<>'common_id' ORDER BY a.attnum LOOP
   expr='NULL::'||col.typ;
   IF col.attname=keycol THEN expr='c.'||commonkey||'::'||col.typ;
   ELSIF entity='roles' AND col.attname='role_code' THEN expr='('||role_case||')::'||col.typ;
   ELSIF entity='permissions' AND col.attname='permission_code' THEN expr='('||permission_case||')::'||col.typ;
   ELSIF col.attname='password_algo' THEN expr='''AES_PTPL''::'||col.typ;
   ELSIF col.attname='must_change_password' THEN expr='coalesce(c.isfirstlogin,false)';
   ELSIF col.attname IN('is_system','mfa_enabled','is_locked') THEN expr='false';
   ELSIF col.attname='failed_login_count' THEN expr='0';
   ELSIF col.attname='row_version' THEN expr='1::bigint';
   ELSIF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='common' AND table_name=entity AND column_name=col.attname) THEN
    expr=CASE WHEN col.typ='boolean' THEN '(coalesce(c.'||quote_ident(col.attname)||',0)=1)' ELSE 'c.'||quote_ident(col.attname)||'::'||col.typ END;
    IF col.attname='fk_department_id' THEN expr='nullif(c.fk_department_id,0)::'||col.typ; END IF;
   END IF;
   fields=fields||CASE WHEN fields='' THEN '' ELSE ',' END||expr||' AS '||quote_ident(col.attname);
  END LOOP;
  EXECUTE format('CREATE OR REPLACE VIEW common.%I AS SELECT %s FROM common.%I c %s','dms_'||entity,fields,entity,
   CASE WHEN entity='permissions' THEN format('WHERE c.fk_module_id=%s',module_id) ELSE '' END);
  EXECUTE format('CREATE OR REPLACE VIEW dms.%I AS SELECT * FROM common.%I',entity,'dms_'||entity);
  FOR r IN SELECT n.nspname,c.relname,a.attname FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid=d.adrelid AND a.attnum=d.adnum
   JOIN pg_class c ON c.oid=d.adrelid JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE (n.nspname='common' AND c.relname='dms_'||entity) OR (n.nspname='dms' AND c.relname=entity) LOOP
   EXECUTE format('ALTER VIEW %I.%I ALTER COLUMN %I DROP DEFAULT',r.nspname,r.relname,r.attname);
  END LOOP;
 END LOOP;
 EXECUTE format('CREATE OR REPLACE VIEW common.dms_role_permission AS SELECT c.pk_role_permission::integer AS pk_role_permission,c.role_id::integer AS role_id,c.permission_id::integer AS permission_id,NULL::timestamp AS created_on,NULL::integer AS granted_by FROM common.role_permission c JOIN common.permissions p ON p.pk_permission=c.permission_id WHERE p.fk_module_id=%s',module_id);
END $views$;
DO $dependent_views$ DECLARE r record; definition text; BEGIN
 FOR r IN SELECT c.oid,n.nspname,c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
  WHERE n.nspname='dms' AND c.relkind='v' LOOP
  definition=pg_get_viewdef(r.oid,true);
  IF definition ~ '(users|roles|permissions)_auth_profile' THEN
   definition=regexp_replace(definition,'(?:dms[.])?users_auth_profile\M','common.dms_users','g');
   definition=regexp_replace(definition,'(?:dms[.])?roles_auth_profile\M','common.dms_roles','g');
   definition=regexp_replace(definition,'(?:dms[.])?permissions_auth_profile\M','common.dms_permissions','g');
   EXECUTE format('CREATE OR REPLACE VIEW %I.%I AS %s',r.nspname,r.relname,definition);
  END IF;
 END LOOP;
END $dependent_views$;
DO $login$ DECLARE r record; definition text; BEGIN
 FOR r IN SELECT p.oid FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='common' AND p.proname='sp_dms_getuserforlogin' OR n.nspname='dms' AND p.proname='sp_getuserforlogin' LOOP
  definition=pg_get_functiondef(r.oid);
  definition=replace(definition,'FROM common.dms_users u',
   'FROM common.dms_users u JOIN common.roles login_role ON login_role.roleid=u.role_id AND login_role.isactive=1 AND coalesce(login_role.isarchived,0)=0');
  EXECUTE definition;
 END LOOP;
END $login$;

CREATE OR REPLACE FUNCTION common.sp_dms_registerloginresult(p_username text,p_user_id integer,p_succeeded boolean,p_failure_reason text,p_ip_address text,p_user_agent text)
RETURNS integer LANGUAGE sql AS $fn$ SELECT 0; $fn$;
CREATE OR REPLACE FUNCTION common.sp_dms_addpermissionverification(p_permissionid integer,p_verifiedby integer,p_comments text)
RETURNS integer LANGUAGE plpgsql AS $fn$ DECLARE n integer; BEGIN
 UPDATE common.permissions SET isverified=1,modified_on=now(),modified_by=p_verifiedby WHERE pk_permission=p_permissionid;
 GET DIAGNOSTICS n=ROW_COUNT; RETURN n;
END $fn$;
CREATE OR REPLACE FUNCTION common.sp_dms_getverifiedpermissionbyid(p_permissionid integer)
RETURNS TABLE(id integer,permissionid numeric,verifiedby numeric,verifiedon timestamp without time zone,comments text)
LANGUAGE sql STABLE AS $fn$
 SELECT c.pk_permission,c.pk_permission::numeric,c.modified_by::numeric,c.modified_on,NULL::text
 FROM common.permissions c WHERE c.pk_permission=p_permissionid AND c.isverified=1;
$fn$;

DO $admin$ DECLARE r record; definition text; BEGIN
 -- Account/role/permission mutation belongs to shared administration.
 FOR r IN SELECT p.oid,p.prosrc FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  JOIN dms.common_auth_routine_map m ON (n.nspname='common' AND p.proname=m.common_name OR n.nspname='dms' AND p.proname=m.legacy_name)
   AND pg_get_function_identity_arguments(p.oid)=m.identity_arguments
  WHERE m.common_name IN('sp_dms_updatepasswordbyid','sp_dms_resetpasswordbyid','sp_dms_updateotpbyid','sp_dms_users_addupdate','sp_dms_users_delete','sp_dms_updateuser','sp_dms_addnewrole','sp_dms_updaterole','sp_dms_deleterole','sp_dms_addpermission','sp_dms_updatepermission','sp_dms_deletepermission','sp_dms_addpermissionforrole','sp_dms_deletepermissionforroleid','sp_dms_deleterolepermission','sp_dms_apply_v2_permission_grants') LOOP
  definition=pg_get_functiondef(r.oid);
  definition=replace(definition,'LANGUAGE sql','LANGUAGE plpgsql');
  definition=replace(definition,r.prosrc,chr(10)||'BEGIN RAISE EXCEPTION ''Manage users, roles, permissions and passwords in the shared administration module''; END;'||chr(10));
  EXECUTE definition;
 END LOOP;
 -- Legacy verification/auth APIs must also lose profile references.
 FOR r IN SELECT m.*,cp.oid FROM dms.common_auth_routine_map m JOIN pg_proc cp ON cp.proname=m.common_name
  JOIN pg_namespace n ON n.oid=cp.pronamespace AND n.nspname='common'
  WHERE m.common_name IN('sp_dms_registerloginresult','sp_dms_addpermissionverification','sp_dms_getverifiedpermissionbyid')
   AND pg_get_function_identity_arguments(cp.oid)=m.identity_arguments LOOP
  definition=replace(pg_get_functiondef(r.oid),'FUNCTION common.'||r.common_name||'(','FUNCTION dms.'||r.legacy_name||'(');
  EXECUTE definition;
 END LOOP;
 FOR r IN SELECT n.nspname,c.relname,t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
  JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_proc p ON p.oid=t.tgfoid
  WHERE n.nspname IN('dms','common') AND p.proname IN('sp_dms_users_bridge','sp_dms_roles_bridge','sp_dms_permissions_bridge','sp_dms_role_permission_bridge') LOOP
  EXECUTE format('DROP TRIGGER %I ON %I.%I',r.tgname,r.nspname,r.relname);
 END LOOP;
END $admin$;
DROP FUNCTION common.sp_dms_users_bridge();
DROP FUNCTION common.sp_dms_roles_bridge();
DROP FUNCTION common.sp_dms_permissions_bridge();
DROP FUNCTION common.sp_dms_role_permission_bridge();
DROP TABLE dms.users_auth_profile;
DROP TABLE dms.roles_auth_profile;
DROP TABLE dms.permissions_auth_profile;

DO $workflow$ DECLARE entity text; r record; definition text; BEGIN
 FOREACH entity IN ARRAY ARRAY['document_states','document_state_transitions','document_workflow_stages','document_workflow_instances','document_state_history','document_access_rules'] LOOP
  IF to_regclass('common.'||entity) IS NOT NULL THEN RAISE EXCEPTION 'Common workflow table already exists: %',entity; END IF;
  EXECUTE format('ALTER TABLE dms.%I SET SCHEMA common',entity);
  EXECUTE format('CREATE VIEW dms.%I AS SELECT * FROM common.%I',entity,entity);
 END LOOP;
 FOR r IN SELECT p.oid FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE p.prokind='f' AND (n.nspname='dms' OR n.nspname='common' AND p.proname LIKE 'sp_dms_%') LOOP
  definition=pg_get_functiondef(r.oid);
  FOREACH entity IN ARRAY ARRAY['document_states','document_state_transitions','document_workflow_stages','document_workflow_instances','document_state_history','document_access_rules'] LOOP
   definition=regexp_replace(definition,'(?<![\w.])(?:dms[.])?'||entity||'\M','common.'||entity,'g');
  END LOOP;
  IF definition<>pg_get_functiondef(r.oid) THEN EXECUTE definition; END IF;
 END LOOP;
END $workflow$;
-- Old tokens contain old identity IDs. Force editor sessions to reopen.
TRUNCATE dms.editor_sessions,dms.editor_leases,dms.template_editor_leases;
DELETE FROM dms.system_config WHERE config_key LIKE 'security.password.%' OR config_key LIKE 'security.lockout.%';
DO $verify$ BEGIN
 IF EXISTS(SELECT 1 FROM shared_routine_baseline b LEFT JOIN pg_proc p ON p.oid=b.oid
  WHERE p.oid IS NULL OR b.fingerprint<>md5(pg_get_functiondef(p.oid))) THEN RAISE EXCEPTION 'Shared routine changed'; END IF;
 IF EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE p.prokind='f' AND n.nspname IN('common','dms') AND p.prosrc ~ '(users|roles|permissions)_auth_profile') THEN
  RAISE EXCEPTION 'Identity profile dependency remains'; END IF;
 IF (SELECT count(*) FROM common.dms_users)<>(SELECT count(*) FROM common.users) THEN RAISE EXCEPTION 'Canonical account coverage incomplete'; END IF;
END $verify$;
INSERT INTO dms.common_auth_migration_status(description)
VALUES('32: common-only canonical identities and role grants; shared administration owns user/password policies; three DMS profiles removed; six workflow tables moved to common; no common columns added.');
COMMIT;
