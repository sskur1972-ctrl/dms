-- Complete onboarding of existing common accounts without granting DMS access.
BEGIN;
SET LOCAL search_path=dms,public;
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' OR to_regclass('dms.users_auth_profile') IS NULL THEN RAISE EXCEPTION 'Apply only after migration 25 in UnifiedPlatform_dms'; END IF;
END $guard$;
INSERT INTO dms.users_auth_profile(common_id,username,password,password_algo,role_id,emailid,address,displayname,mobilenumber,fk_department_id,isactive,isarchived,must_change_password)
SELECT c.pk_user,c.username,c.password,
 CASE WHEN c.password LIKE 'PBKDF2$%' THEN 'PBKDF2_SHA512_V1' ELSE 'AES_PTPL' END,
 r.pk_role,c.emailid,c.address,c.displayname,c.mobilenumber,
 (SELECT d.id FROM dms.departments d WHERE d.id=c.fk_department_id),
 c.isactive=1,coalesce(c.isarchived,0)=1,c.isfirstlogin
FROM common.users c LEFT JOIN dms.roles_auth_profile r ON r.common_id=c.role_id
WHERE NOT EXISTS(SELECT 1 FROM dms.users_auth_profile p WHERE p.common_id=c.pk_user)
 AND nullif(trim(c.username),'') IS NOT NULL AND nullif(c.password,'') IS NOT NULL;
DO $views$ DECLARE definition text; role_type text;
BEGIN
 SELECT pg_get_viewdef('dms.users'::regclass,true) INTO definition;
 IF definition NOT LIKE '%c.isfirstlogin%' THEN
  definition=replace(definition,'p.must_change_password,','(p.must_change_password OR c.isfirstlogin) AS must_change_password,');
  EXECUTE 'CREATE OR REPLACE VIEW dms.users AS '||definition;
 END IF;
 SELECT format_type(a.atttypid,a.atttypmod) INTO role_type FROM pg_attribute a WHERE a.attrelid='dms.roles_auth_profile'::regclass AND a.attname='role_name';
 SELECT pg_get_viewdef('dms.roles'::regclass,true) INTO definition;
 definition=replace(definition,'p.role_name,','c.role_name::'||role_type||' AS role_name,');
 EXECUTE 'CREATE OR REPLACE VIEW dms.roles AS '||definition;
 -- Only the new DMS bridge created by migration 25 is changed. No pre-existing
 -- shared common function is altered.
 SELECT pg_get_functiondef('common.sp_dms_users_bridge()'::regprocedure) INTO definition;
 IF definition NOT LIKE '%isfirstlogin%' THEN
  definition=regexp_replace(definition,'INSERT INTO common.users\(([^\n]+)\) VALUES\(([^\n]+)\) RETURNING',
   'INSERT INTO common.users(\1,isfirstlogin) VALUES(\2,NEW.must_change_password) RETURNING');
  definition=regexp_replace(definition,'UPDATE common.users SET ([^\n]+) WHERE',
   'UPDATE common.users SET \1,isfirstlogin=NEW.must_change_password WHERE');
  EXECUTE definition;
 END IF;
END $views$;
COMMIT;
