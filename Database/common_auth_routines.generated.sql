-- Generated from UnifiedPlatform_dms after migration 32.
-- Only the 45 DMS routines created by migration 27 are included.
-- Existing shared common routines are not included or modified.
-- Run migrations 25-32 first to install the mappings and remove obsolete storage.
SET search_path=dms,common,public;
CREATE OR REPLACE FUNCTION common.sp_dms_addnewrole(p_role_name text, p_isactive integer, p_created_by integer, p_modified_by integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_addpermission(p_permission text, p_fk_module_id integer, p_created_by integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_addpermissionforrole(p_roles_id integer, p_permission_id integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_addpermissionverification(p_permissionid integer, p_verifiedby integer, p_comments text)
 RETURNS integer
 LANGUAGE plpgsql
AS $function$ DECLARE n integer; BEGIN
 UPDATE common.permissions SET isverified=1,modified_on=now(),modified_by=p_verifiedby WHERE pk_permission=p_permissionid;
 GET DIAGNOSTICS n=ROW_COUNT; RETURN n;
END $function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_api_userdetailbyidpassword_get(p_username text, p_password text)
 RETURNS TABLE(username character varying, password character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT u.username, u.password FROM common.dms_users u
    WHERE lower(u.username) = lower(trim(p_username)) AND u.password = p_password
      AND u.isactive = TRUE AND u.isarchived = FALSE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_apply_v2_permission_grants()
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_checkemailexists(p_emailid text, p_userid integer)
 RETURNS TABLE(emailid character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT u.emailid FROM common.dms_users u
    WHERE lower(u.emailid) = lower(trim(p_emailid)) AND u.pk_user <> coalesce(p_userid, 0) AND u.isarchived = FALSE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_deletepermission(p_modifiedby integer, p_id integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_deletepermissionforroleid(p_roles_id integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_deleterole(p_id integer, p_user_id integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_deleterolepermission(p_permissionid integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getcurrentuser_permissionforroles(p_user_id bigint)
 RETURNS TABLE(permission_id integer, permission character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT id::integer, permission FROM common.sp_dms_getpermissionsforuser(p_user_id::integer);
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getpasswordhistory(p_id integer)
 RETURNS TABLE(password_hash character varying, password_algo character varying)
 LANGUAGE sql
 STABLE
AS $function$ SELECT NULL::varchar,NULL::varchar WHERE false; $function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getpermissionalredayexisted(p_permission text)
 RETURNS TABLE(permission character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT p.permission FROM common.dms_permissions p
    WHERE lower(p.permission) = lower(trim(p_permission)) AND p.isarchived = FALSE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getpermissionbyid(p_id integer)
 RETURNS TABLE(pk_permission integer, permission character varying, fk_module_id integer, isarchived boolean, created_on timestamp without time zone, modified_on timestamp without time zone, modified_by numeric, isverified boolean, modulename character varying, parent_module character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT p.pk_permission, p.permission, p.fk_module_id, p.isactive,
           p.created_on, p.modified_on, p.modified_by::numeric, p.isverified,
           m.module_name, m.parent_module
    FROM common.dms_permissions p
    LEFT JOIN module_master m ON m.module_id = p.fk_module_id
    WHERE p.pk_permission = p_id AND p.isarchived = FALSE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getpermissionlist()
 RETURNS TABLE(pk_permission integer, permission character varying, fk_module_id integer, isarchived boolean, created_on timestamp without time zone, modified_on timestamp without time zone, modified_by numeric, isverified boolean, modulename character varying, parent_module character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT p.pk_permission, p.permission, p.fk_module_id, p.isactive,
           p.created_on, p.modified_on, p.modified_by::numeric, p.isverified,
           m.module_name, m.parent_module
    FROM common.dms_permissions p
    LEFT JOIN module_master m ON m.module_id = p.fk_module_id
    WHERE p.isarchived = FALSE
    ORDER BY p.fk_module_id, p.pk_permission;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getpermissionsbindwithroleid(p_roleid integer)
 RETURNS TABLE(roles_id integer, id numeric, permission character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT rp.role_id, p.pk_permission::numeric, p.permission
    FROM common.dms_role_permission rp
    JOIN common.dms_permissions p ON p.pk_permission = rp.permission_id
    WHERE rp.role_id = p_roleid AND p.isarchived = FALSE AND p.isactive = TRUE
    ORDER BY p.pk_permission;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getpermissionsforroleid(p_roleid integer)
 RETURNS TABLE(pk_role_permission integer, role_id integer, permission_id integer)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT rp.pk_role_permission, rp.role_id, rp.permission_id
    FROM common.dms_role_permission rp
    JOIN common.dms_permissions p ON p.pk_permission = rp.permission_id AND p.isarchived = FALSE
    WHERE rp.role_id = p_roleid;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getpermissionsforuser(p_user_id integer)
 RETURNS TABLE(roles_id integer, id numeric, permission character varying, permission_code character varying)
 LANGUAGE sql
 STABLE
AS $function$
 SELECT DISTINCT ON(p.pk_permission) g.role_id,p.pk_permission::numeric,p.permission,p.permission_code
 FROM common.dms_role_permission g JOIN common.dms_roles r ON r.pk_role=g.role_id AND r.isactive AND NOT r.isarchived
 JOIN common.dms_permissions p ON p.pk_permission=g.permission_id AND p.isactive AND NOT p.isarchived
 WHERE g.role_id=ANY(common.sp_dms_user_role_ids(p_user_id)) ORDER BY p.pk_permission,g.role_id;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getrolelist()
 RETURNS TABLE(roleid integer, role_name character varying, isactive boolean, created_on timestamp without time zone, modified_on timestamp without time zone, created_by integer, modified_by integer)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT r.pk_role, r.role_name, r.isactive, r.created_on, r.modified_on, r.created_by, r.modified_by
    FROM common.dms_roles r WHERE r.isarchived = FALSE ORDER BY r.pk_role;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getrolenamebyid(p_roleid integer)
 RETURNS TABLE(roleid integer, role_name character varying, isactive boolean, created_on timestamp without time zone, modified_on timestamp without time zone, created_by integer, modified_by integer)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT r.pk_role, r.role_name, r.isactive, r.created_on, r.modified_on, r.created_by, r.modified_by
    FROM common.dms_roles r WHERE r.pk_role = p_roleid AND r.isarchived = FALSE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getrolenamebyname(p_roles_name text)
 RETURNS TABLE(roleid integer, role_name character varying, isactive boolean, created_on timestamp without time zone, modified_on timestamp without time zone, created_by integer, modified_by integer)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT r.pk_role, r.role_name, r.isactive, r.created_on, r.modified_on, r.created_by, r.modified_by
    FROM common.dms_roles r WHERE lower(r.role_name) = lower(trim(p_roles_name)) AND r.isarchived = FALSE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getuser_index()
 RETURNS TABLE(pk_user integer, username character varying, role_id integer, rolename character varying, emailid character varying, displayname character varying, created_by integer, modified_by integer, created_on timestamp without time zone, modified_on timestamp without time zone)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT u.pk_user, u.username, u.role_id, r.role_name, u.emailid, u.displayname,
           u.created_by, u.modified_by, u.created_on, u.modified_on
    FROM common.dms_users u
    LEFT JOIN common.dms_roles r ON r.pk_role = u.role_id
    WHERE u.isarchived = FALSE
    ORDER BY u.pk_user;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getuserdetailsbyid(p_id integer)
 RETURNS TABLE(pk_user integer, username character varying, password character varying, password_algo character varying, role_id integer, isactive boolean, isarchived boolean, emailid character varying, address character varying, displayname character varying, mobilenumber character varying, fk_department_id integer, employee_code character varying, designation character varying, must_change_password boolean, is_locked boolean, locked_until timestamp without time zone, failed_login_count integer, created_by integer, modified_by integer, created_on timestamp without time zone, modified_on timestamp without time zone)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT u.pk_user, u.username, u.password, u.password_algo, u.role_id, u.isactive, u.isarchived,
           u.emailid, u.address, u.displayname, u.mobilenumber, coalesce(u.fk_department_id, 0)::integer,
           u.employee_code, u.designation, u.must_change_password, u.is_locked, u.locked_until, u.failed_login_count,
           u.created_by, u.modified_by, u.created_on, u.modified_on
    FROM common.dms_users u WHERE u.pk_user = p_id AND u.isarchived = FALSE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getuserdetailsbyidandotp(p_id integer, p_otp text)
 RETURNS TABLE(pk_user integer, username character varying, password character varying, role_id integer, isactive boolean, isarchived boolean, emailid character varying, address character varying, displayname character varying, mobilenumber character varying, created_by integer, modified_by integer, created_on timestamp without time zone, modified_on timestamp without time zone, otp character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT u.pk_user, u.username, u.password, u.role_id, u.isactive, u.isarchived,
           u.emailid, u.address, u.displayname, u.mobilenumber,
           u.created_by, u.modified_by, u.created_on, u.modified_on, u.otp
    FROM common.dms_users u WHERE u.pk_user = p_id AND u.otp = p_otp AND u.isarchived = FALSE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getuserforlogin(p_username text)
 RETURNS TABLE(pk_user integer, username character varying, password character varying, password_algo character varying, role_id integer, isactive boolean, isarchived boolean, emailid character varying, address character varying, displayname character varying, mobilenumber character varying, fk_department_id integer, employee_code character varying, designation character varying, must_change_password boolean, is_locked boolean, locked_until timestamp without time zone, failed_login_count integer, created_by integer, modified_by integer, created_on timestamp without time zone, modified_on timestamp without time zone)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$

    SELECT u.pk_user, u.username, u.password, u.password_algo, u.role_id, u.isactive, u.isarchived,
           u.emailid, u.address, u.displayname, u.mobilenumber, coalesce(u.fk_department_id, 0)::integer,
           u.employee_code, u.designation, u.must_change_password, u.is_locked, u.locked_until, u.failed_login_count,
           u.created_by, u.modified_by, u.created_on, u.modified_on
    FROM common.dms_users u JOIN common.roles login_role ON login_role.roleid=u.role_id AND login_role.isactive=1 AND coalesce(login_role.isarchived,0)=0
    WHERE (lower(u.username) = lower(trim(p_username)) OR lower(u.emailid) = lower(trim(p_username)))
      AND u.isarchived = FALSE
    ORDER BY (lower(u.username) = lower(trim(p_username))) DESC
    LIMIT 1;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getuserslist()
 RETURNS TABLE(pk_user integer, username character varying, role_id integer)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT pk_user, username, role_id FROM common.dms_users WHERE isarchived = FALSE ORDER BY username;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getuserusingusername(p_username text)
 RETURNS TABLE(pk_user integer, username character varying, password character varying, password_algo character varying, role_id integer, isactive boolean, isarchived boolean, emailid character varying, address character varying, displayname character varying, mobilenumber character varying, fk_department_id integer, employee_code character varying, designation character varying, must_change_password boolean, is_locked boolean, locked_until timestamp without time zone, failed_login_count integer, created_by integer, modified_by integer, created_on timestamp without time zone, modified_on timestamp without time zone)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT u.pk_user, u.username, u.password, u.password_algo, u.role_id, u.isactive, u.isarchived,
           u.emailid, u.address, u.displayname, u.mobilenumber, coalesce(u.fk_department_id, 0)::integer,
           u.employee_code, u.designation, u.must_change_password, u.is_locked, u.locked_until, u.failed_login_count,
           u.created_by, u.modified_by, u.created_on, u.modified_on
    FROM common.dms_users u WHERE lower(u.username) = lower(trim(p_username)) AND u.isarchived = FALSE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getuserusingusernameandemail(p_email text)
 RETURNS TABLE(pk_user integer, username character varying, password character varying, password_algo character varying, role_id integer, isactive boolean, isarchived boolean, emailid character varying, address character varying, displayname character varying, mobilenumber character varying, fk_department_id integer, employee_code character varying, designation character varying, must_change_password boolean, is_locked boolean, locked_until timestamp without time zone, failed_login_count integer, created_by integer, modified_by integer, created_on timestamp without time zone, modified_on timestamp without time zone)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT u.pk_user, u.username, u.password, u.password_algo, u.role_id, u.isactive, u.isarchived,
           u.emailid, u.address, u.displayname, u.mobilenumber, coalesce(u.fk_department_id, 0)::integer,
           u.employee_code, u.designation, u.must_change_password, u.is_locked, u.locked_until, u.failed_login_count,
           u.created_by, u.modified_by, u.created_on, u.modified_on
    FROM common.dms_users u
    WHERE (lower(u.emailid) = lower(trim(p_email)) OR lower(u.username) = lower(trim(p_email)))
      AND u.isarchived = FALSE AND u.isactive = TRUE;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_getverifiedpermissionbyid(p_permissionid integer)
 RETURNS TABLE(id integer, permissionid numeric, verifiedby numeric, verifiedon timestamp without time zone, comments text)
 LANGUAGE sql
 STABLE
AS $function$
 SELECT c.pk_permission,c.pk_permission::numeric,c.modified_by::numeric,c.modified_on,NULL::text
 FROM common.permissions c WHERE c.pk_permission=p_permissionid AND c.isverified=1;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_registerloginresult(p_username text, p_user_id integer, p_succeeded boolean, p_failure_reason text, p_ip_address text, p_user_agent text)
 RETURNS integer
 LANGUAGE sql
AS $function$ SELECT 0; $function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_resetpasswordbyid(p_id bigint, p_password text)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_roles_getrolesfordropdown()
 RETURNS TABLE(roleid integer, role_name character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT pk_role, role_name FROM common.dms_roles WHERE isarchived = FALSE AND isactive = TRUE ORDER BY role_name;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_updateotpbyid(p_id bigint, p_otp text)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_updatepasswordbyid(p_id bigint, p_password text)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_updatepermission(p_permission text, p_fk_module_id integer, p_modifiedby integer, p_id integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_updaterole(p_roles_name character varying, p_isactive smallint, p_modifiedby integer, p_roleid integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_updateuser(p_pk_user integer, p_username text, p_roleid integer, p_emailid text, p_address text, p_displayname text, p_mobilenumber text, p_modified_by integer, p_fk_department_id integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_user_has_permission(p_user_id integer, p_code text)
 RETURNS boolean
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT coalesce(NULLIF(trim(p_code), ''), '') = '' OR EXISTS (
        SELECT 1 FROM common.dms_role_permission rp
        JOIN common.dms_roles r ON r.pk_role = rp.role_id AND r.isactive AND NOT r.isarchived
        JOIN common.dms_permissions p ON p.pk_permission = rp.permission_id AND p.isactive AND NOT p.isarchived
        WHERE rp.role_id = ANY (common.sp_dms_user_role_ids(p_user_id)) AND lower(p.permission_code) = lower(trim(p_code)));
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_user_pick_list()
 RETURNS TABLE(user_id integer, full_name character varying, user_name character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT pk_user, displayname, username FROM common.dms_users WHERE isactive AND NOT isarchived ORDER BY displayname;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_user_role_codes(p_user_id integer)
 RETURNS text
 LANGUAGE sql
 STABLE
AS $function$
 SELECT string_agg(DISTINCT coalesce(role_code,role_name),',') FROM common.dms_roles
 WHERE pk_role=ANY(common.sp_dms_user_role_ids(p_user_id)) AND isactive AND NOT isarchived;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_user_role_ids(p_user_id integer)
 RETURNS integer[]
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
 SELECT coalesce(array_agg(role_id) FILTER(WHERE role_id IS NOT NULL),'{}')
 FROM common.dms_users WHERE pk_user=p_user_id;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_users_addupdate(p_username text, p_password text, p_roleid integer, p_emailid text, p_address text, p_displayname text, p_mobilenumber text, p_created_by integer, p_modified_by integer, p_fk_department_id integer)
 RETURNS bigint
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_users_delete(p_id integer, p_user_id integer)
 RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'dms', 'common', 'public'
AS $function$
BEGIN RAISE EXCEPTION 'Manage users, roles, permissions and passwords in the shared administration module'; END;
$function$
;
CREATE OR REPLACE FUNCTION common.sp_dms_users_with_permission(p_code text)
 RETURNS SETOF integer
 LANGUAGE sql
 STABLE
 SET search_path TO 'dms', 'common', 'public'
AS $function$
    SELECT u.pk_user FROM common.dms_users u WHERE u.isactive AND NOT u.isarchived AND common.sp_dms_user_has_permission(u.pk_user, p_code);
$function$
;
