-- Existing shared common definitions and passwords are preserved.
-- AES-only DMS password writes, no DMS password/login/verification history.
BEGIN;
SET LOCAL search_path=dms,common,public;
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' OR to_regclass('dms.role_permission_auth_profile') IS NULL THEN
  RAISE EXCEPTION 'Requires UnifiedPlatform_dms after migration 28; apply once';
 END IF;
END $guard$;
LOCK TABLE dms.role_permission_auth_profile,dms.permission_verification,dms.user_login_attempts,dms.user_mfa_secrets IN EXCLUSIVE MODE;
CREATE TEMP TABLE preserved_auth_record_counts AS
 SELECT 'role_permission_auth_profile'::text table_name,count(*) total FROM dms.role_permission_auth_profile
 UNION ALL SELECT 'permission_verification',count(*) FROM dms.permission_verification
 UNION ALL SELECT 'user_login_attempts',count(*) FROM dms.user_login_attempts
 UNION ALL SELECT 'user_mfa_secrets',count(*) FROM dms.user_mfa_secrets;
CREATE TEMP TABLE aes_shared_baseline AS
SELECT p.oid,md5(pg_get_functiondef(p.oid)) fingerprint FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname='common' AND p.prokind IN('f','p')
AND NOT EXISTS(SELECT 1 FROM dms.common_auth_routine_map m WHERE m.common_name=p.proname
 AND m.identity_arguments=pg_get_function_identity_arguments(p.oid))
AND p.proname NOT IN('sp_dms_users_bridge','sp_dms_roles_bridge','sp_dms_permissions_bridge','sp_dms_role_permission_bridge');

CREATE OR REPLACE FUNCTION common.sp_dms_updatepasswordbyid(p_id bigint,p_password text)
RETURNS integer LANGUAGE plpgsql SET search_path=dms,common,public AS $fn$
DECLARE v_rows integer; v_days integer:=coalesce((SELECT config_value::integer FROM dms.system_config WHERE config_key='security.password.expiryDays'),0);
BEGIN
 IF nullif(p_password,'') IS NULL OR p_password LIKE 'PBKDF2$%' THEN RAISE EXCEPTION 'PTPL AES encrypted password required'; END IF;
 UPDATE common.dms_users SET password=p_password,password_algo='AES_PTPL',
  password_set_at=LOCALTIMESTAMP(3),
  password_expires_at=CASE WHEN v_days>0 THEN LOCALTIMESTAMP(3)+make_interval(days=>v_days) END,
  must_change_password=false,failed_login_count=0,locked_until=NULL,
  modified_on=now(),row_version=row_version+1 WHERE pk_user=p_id AND isarchived=false;
 GET DIAGNOSTICS v_rows=ROW_COUNT; RETURN v_rows;
END $fn$;
-- Retain old SQL entry points as empty compatibility responses, without storage.
CREATE OR REPLACE FUNCTION common.sp_dms_getpasswordhistory(p_id integer)
RETURNS TABLE(password_hash character varying,password_algo character varying)
LANGUAGE sql STABLE AS $fn$ SELECT NULL::varchar,NULL::varchar WHERE false; $fn$;

CREATE OR REPLACE FUNCTION common.sp_dms_registerloginresult(p_username text,p_user_id integer,p_succeeded boolean,p_failure_reason text,p_ip_address text,p_user_agent text)
RETURNS integer LANGUAGE plpgsql SET search_path=dms,common,public AS $fn$
DECLARE v_max integer:=coalesce((SELECT config_value::integer FROM dms.system_config WHERE config_key='security.lockout.maxFailedAttempts'),5);
 v_mins integer:=coalesce((SELECT config_value::integer FROM dms.system_config WHERE config_key='security.lockout.minutes'),30);
BEGIN
 IF p_user_id IS NULL THEN RETURN 0; END IF;
 IF p_succeeded THEN
  UPDATE common.dms_users SET failed_login_count=0,locked_until=NULL,
   last_login=LOCALTIMESTAMP(3),last_login_ip=left(p_ip_address,64) WHERE pk_user=p_user_id; RETURN 1;
 END IF;
 UPDATE common.dms_users SET failed_login_count=failed_login_count+1,
  locked_until=CASE WHEN failed_login_count+1>=v_max THEN LOCALTIMESTAMP(3)+make_interval(mins=>v_mins) ELSE locked_until END
 WHERE pk_user=p_user_id; RETURN 0;
END $fn$;
CREATE OR REPLACE FUNCTION common.sp_dms_addpermissionverification(p_permissionid integer,p_verifiedby integer,p_comments text)
RETURNS integer LANGUAGE plpgsql AS $fn$
DECLARE v_rows integer;
BEGIN
 UPDATE common.permissions SET isverified=1,modified_on=now(),
  modified_by=(SELECT common_id FROM dms.users_auth_profile WHERE pk_user=p_verifiedby)
 WHERE pk_permission=(SELECT common_id FROM dms.permissions_auth_profile WHERE pk_permission=p_permissionid);
 GET DIAGNOSTICS v_rows=ROW_COUNT; RETURN v_rows;
END $fn$;
CREATE OR REPLACE FUNCTION common.sp_dms_getverifiedpermissionbyid(p_permissionid integer)
RETURNS TABLE(id integer,permissionid numeric,verifiedby numeric,verifiedon timestamp without time zone,comments text)
LANGUAGE sql STABLE AS $fn$
 SELECT p.pk_permission,p.pk_permission::numeric,u.pk_user::numeric,c.modified_on,NULL::text
 FROM common.permissions c JOIN dms.permissions_auth_profile p ON p.common_id=c.pk_permission
 LEFT JOIN dms.users_auth_profile u ON u.common_id=c.modified_by
 WHERE p.pk_permission=p_permissionid AND c.isverified=1;
$fn$;

-- Canonical grants need no duplicate DMS grant rows.
CREATE OR REPLACE VIEW common.dms_role_permission AS
SELECT c.pk_role_permission::integer AS pk_role_permission,r.pk_role AS role_id,
 p.pk_permission AS permission_id,NULL::timestamp AS created_on,NULL::integer AS granted_by
FROM common.role_permission c JOIN dms.roles_auth_profile r ON r.common_id=c.role_id
JOIN dms.permissions_auth_profile p ON p.common_id=c.permission_id;
CREATE OR REPLACE VIEW dms.role_permission AS SELECT * FROM common.dms_role_permission;
ALTER VIEW common.dms_role_permission ALTER COLUMN pk_role_permission DROP DEFAULT;
ALTER VIEW dms.role_permission ALTER COLUMN pk_role_permission DROP DEFAULT;

DO $rebind$
DECLARE r record; definition text;
BEGIN
 SELECT pg_get_functiondef('common.sp_dms_users_addupdate(text,text,integer,text,text,text,text,integer,integer,integer)'::regprocedure) INTO definition;
 definition=replace(definition,'CASE WHEN p_password LIKE ''PBKDF2$%'' THEN ''PBKDF2_SHA512_V1'' ELSE ''AES_PTPL'' END','''AES_PTPL''');
 definition=regexp_replace(definition,'\mBEGIN\M','BEGIN'||chr(10)||' IF nullif(p_password,'''') IS NULL OR p_password LIKE ''PBKDF2$%'' THEN RAISE EXCEPTION ''PTPL AES encrypted password required''; END IF;');
 EXECUTE definition;
 -- Legacy auth entry points delegate to their common DMS APIs.
 FOR r IN SELECT m.*,p.oid FROM dms.common_auth_routine_map m JOIN pg_proc p ON p.proname=m.legacy_name
  JOIN pg_namespace n ON n.oid=p.pronamespace AND n.nspname='dms'
  WHERE m.common_name IN('sp_dms_registerloginresult','sp_dms_addpermissionverification','sp_dms_getverifiedpermissionbyid','sp_dms_users_addupdate')
  AND pg_get_function_identity_arguments(p.oid)=m.identity_arguments LOOP
  SELECT pg_get_functiondef(cp.oid) INTO definition FROM pg_proc cp JOIN pg_namespace cn ON cn.oid=cp.pronamespace
   WHERE cn.nspname='common' AND cp.proname=r.common_name AND pg_get_function_identity_arguments(cp.oid)=r.identity_arguments;
  definition=replace(definition,'FUNCTION common.'||r.common_name||'(', 'FUNCTION dms.'||r.legacy_name||'(');
  EXECUTE definition;
 END LOOP;
END $rebind$;

-- Old records remain until the user explicitly approves their retirement.
-- Canonical grant revocation must not be blocked by an inactive metadata FK.
DO $detach$ DECLARE c record; BEGIN
 FOR c IN SELECT conname FROM pg_constraint
  WHERE conrelid='dms.role_permission_auth_profile'::regclass
   AND confrelid='common.role_permission'::regclass AND contype='f' LOOP
  EXECUTE format('ALTER TABLE dms.role_permission_auth_profile DROP CONSTRAINT %I',c.conname);
 END LOOP;
END $detach$;
ALTER TABLE dms.users_auth_profile ALTER COLUMN password_algo SET DEFAULT 'AES_PTPL';
ALTER VIEW common.dms_users ALTER COLUMN password_algo SET DEFAULT 'AES_PTPL';
ALTER VIEW dms.users ALTER COLUMN password_algo SET DEFAULT 'AES_PTPL';
DELETE FROM dms.system_config WHERE config_key='security.password.historyCount';
DO $verify$ DECLARE r record; remaining bigint; BEGIN
 FOR r IN SELECT * FROM preserved_auth_record_counts LOOP
  EXECUTE format('SELECT count(*) FROM dms.%I',r.table_name) INTO remaining;
  IF remaining<>r.total THEN RAISE EXCEPTION 'Existing authentication records changed: %',r.table_name; END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM aes_shared_baseline b LEFT JOIN pg_proc p ON p.oid=b.oid
  WHERE p.oid IS NULL OR b.fingerprint<>md5(pg_get_functiondef(p.oid))) THEN
  RAISE EXCEPTION 'Pre-existing shared common routine changed';
 END IF;
END $verify$;
INSERT INTO dms.common_auth_migration_status(description)
VALUES('29: PTPL AES-only DMS passwords; password/login/verification history writes disabled; grants read from common only; existing records, shared passwords and shared definitions preserved.');
COMMIT;
