-- Superseded by user-authorized migration 30, which includes these removals.
-- Historical proposal only. Do not execute after migration 30.
-- This deletes existing login, verification and legacy grant metadata records.
-- Authentication uses common canonical tables after migration 29.
BEGIN;
DO $guard$ BEGIN
 IF current_database()<>'UnifiedPlatform_dms' THEN RAISE EXCEPTION 'Wrong database'; END IF;
 IF EXISTS(SELECT 1 FROM dms.user_mfa_secrets) OR EXISTS(SELECT 1 FROM dms.users_auth_profile WHERE mfa_enabled) THEN
  RAISE EXCEPTION 'MFA is in use; deletion refused'; END IF;
END $guard$;
DO $bridge$ DECLARE definition text; BEGIN
 SELECT pg_get_functiondef('common.sp_dms_role_permission_bridge()'::regprocedure) INTO definition;
 definition=replace(definition,'DELETE FROM dms.role_permission_auth_profile WHERE common_id=OLD.pk_role_permission;','');
 EXECUTE definition;
END $bridge$;
DROP TABLE dms.role_permission_auth_profile;
DROP TABLE dms.permission_verification;
DROP TABLE dms.user_login_attempts;
DROP TABLE dms.user_mfa_secrets;
COMMIT;
