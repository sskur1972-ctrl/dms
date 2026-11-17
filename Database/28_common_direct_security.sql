-- Reuse existing shared tables; do not create common.dms_* copies.
BEGIN;
SET LOCAL search_path=dms,common,public;
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' OR to_regclass('common.password_history') IS NULL
  OR to_regclass('dms.user_password_history') IS NULL THEN
  RAISE EXCEPTION 'Requires UnifiedPlatform_dms after migrations 25-27; run once';
 END IF;
END $guard$;
CREATE TEMP TABLE direct_auth_shared_baseline AS
SELECT p.oid,md5(pg_get_functiondef(p.oid)) fingerprint
FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname='common' AND p.prokind IN('f','p')
AND p.proname NOT IN('sp_dms_getpasswordhistory','sp_dms_updatepasswordbyid');

LOCK TABLE dms.user_password_history IN ACCESS EXCLUSIVE MODE;
INSERT INTO common.password_history(user_id,password,created_on)
SELECT u.common_id,h.password_hash,h.created_at
FROM dms.user_password_history h JOIN dms.users_auth_profile u ON u.pk_user=h.user_id
WHERE NOT EXISTS(SELECT 1 FROM common.password_history c
 WHERE c.user_id=u.common_id AND c.password=h.password_hash AND c.created_on=h.created_at);
DO $history$ BEGIN
 IF EXISTS(SELECT 1 FROM dms.user_password_history h LEFT JOIN dms.users_auth_profile u ON u.pk_user=h.user_id
  WHERE u.common_id IS NULL OR NOT EXISTS(SELECT 1 FROM common.password_history c
   WHERE c.user_id=u.common_id AND c.password=h.password_hash AND c.created_on=h.created_at)) THEN
  RAISE EXCEPTION 'Password history copy incomplete';
 END IF;
END $history$;

CREATE OR REPLACE FUNCTION common.sp_dms_getpasswordhistory(p_id integer)
RETURNS TABLE(password_hash character varying,password_algo character varying)
LANGUAGE sql STABLE SET search_path=dms,common,public AS $fn$
 SELECT h.password::varchar,
  CASE WHEN h.password LIKE 'PBKDF2$%' THEN 'PBKDF2_SHA512_V1' ELSE 'AES_PTPL' END::varchar
 FROM common.password_history h JOIN dms.users_auth_profile mapping ON mapping.common_id=h.user_id
 WHERE mapping.pk_user=p_id ORDER BY h.created_on DESC,h.id DESC
 LIMIT greatest(0,coalesce((SELECT config_value::integer FROM dms.system_config
  WHERE config_key='security.password.historyCount'),5));
$fn$;
CREATE OR REPLACE FUNCTION common.sp_dms_updatepasswordbyid(p_id bigint,p_password text)
RETURNS integer LANGUAGE plpgsql SET search_path=dms,common,public AS $fn$
DECLARE v_rows integer; v_days integer:=coalesce((SELECT config_value::integer
 FROM dms.system_config WHERE config_key='security.password.expiryDays'),0);
BEGIN
 INSERT INTO common.password_history(user_id,password,created_on)
 SELECT c.pk_user,c.password,LOCALTIMESTAMP(3) FROM common.users c
 JOIN dms.users_auth_profile mapping ON mapping.common_id=c.pk_user
 WHERE mapping.pk_user=p_id AND coalesce(c.isarchived,0)=0;
 UPDATE common.dms_users SET password=p_password,
  password_algo=CASE WHEN p_password LIKE 'PBKDF2$%' THEN 'PBKDF2_SHA512_V1' ELSE 'AES_PTPL' END,
  password_set_at=LOCALTIMESTAMP(3),
  password_expires_at=CASE WHEN v_days>0 THEN LOCALTIMESTAMP(3)+make_interval(days=>v_days) END,
  must_change_password=false,failed_login_count=0,locked_until=NULL,
  modified_on=now(),row_version=row_version+1
 WHERE pk_user=p_id AND isarchived=false;
 GET DIAGNOSTICS v_rows=ROW_COUNT;
 RETURN v_rows;
END $fn$;
CREATE OR REPLACE FUNCTION dms.sp_getpasswordhistory(p_id integer)
RETURNS TABLE(password_hash character varying,password_algo character varying)
LANGUAGE sql STABLE AS $fn$ SELECT * FROM common.sp_dms_getpasswordhistory(p_id); $fn$;
CREATE OR REPLACE FUNCTION dms.sp_updatepasswordbyid(p_id bigint,p_password text)
RETURNS integer LANGUAGE sql AS $fn$ SELECT common.sp_dms_updatepasswordbyid(p_id,p_password); $fn$;

-- Refuse cascading removal of any dependency not accounted for above.
DROP TABLE dms.user_password_history;
DO $verify$ BEGIN
 IF EXISTS(SELECT 1 FROM direct_auth_shared_baseline b LEFT JOIN pg_proc p ON p.oid=b.oid
  WHERE p.oid IS NULL OR b.fingerprint<>md5(pg_get_functiondef(p.oid))) THEN
  RAISE EXCEPTION 'Existing shared common routine changed; rollback';
 END IF;
 IF EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN('common','dms') AND p.prokind IN('f','p')
  AND pg_get_functiondef(p.oid)~'\muser_password_history\M') THEN
  RAISE EXCEPTION 'Old password history function dependency remains';
 END IF;
END $verify$;
INSERT INTO dms.common_auth_migration_status(description)
VALUES('28: password history uses existing common.password_history and common user IDs; no new common tables or changes to existing shared definitions.');
COMMIT;
