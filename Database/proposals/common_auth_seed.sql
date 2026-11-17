-- PREPARATION ONLY: this script has NOT been applied to UnifiedPlatform.
-- Existing common tables and functions are not altered. Only missing rows would be inserted.
-- Default: no changes (ROLLBACK). Set the two parameters, inspect the preview,
-- and switch ROLLBACK to COMMIT only when the identity migration is authorized.
-- PostgreSQL identity sequences advance even on rollback; do not execute the seed block
-- while common must remain completely untouched. The companion preview is read-only.
-- No user is created, password copied, role assigned to a user, or business owner remapped.
BEGIN;
SET LOCAL dms.seed_actor_id = '';   -- Required: authorized common.users.pk_user.
SET LOCAL dms.seed_module_id = '';  -- Required: existing common.module_master.module_id for DMS.

DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM common.users WHERE pk_user=nullif(current_setting('dms.seed_actor_id'),'')::integer AND isactive=1 AND coalesce(isarchived,0)=0) THEN
  RAISE EXCEPTION 'Set dms.seed_actor_id to an active common user before seeding.';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM common.module_master WHERE module_id=nullif(current_setting('dms.seed_module_id'),'')::integer AND coalesce(is_archived,0)=0) THEN
  RAISE EXCEPTION 'Set dms.seed_module_id to the existing DMS common module. This script does not create or alter modules.';
 END IF;
 IF EXISTS(SELECT 1 FROM common.permissions WHERE fk_module_id=current_setting('dms.seed_module_id')::integer GROUP BY lower(trim(permission)) HAVING count(*)>1) THEN
  RAISE EXCEPTION 'Duplicate common permission names exist in this module. Resolve the mapping before seeding.';
 END IF;
 IF EXISTS(SELECT 1 FROM common.roles GROUP BY lower(trim(role_name)) HAVING count(*)>1) THEN
  RAISE EXCEPTION 'Duplicate common role names exist. Resolve the mapping before seeding.';
 END IF;
END $$;

CREATE TABLE IF NOT EXISTS dms.common_permission_map (
 legacy_permission_id integer PRIMARY KEY, permission_code text NOT NULL UNIQUE,
 permission_name text NOT NULL, common_permission_id integer NOT NULL REFERENCES common.permissions(pk_permission)
);
CREATE TABLE IF NOT EXISTS dms.common_role_map (
 legacy_role_id integer PRIMARY KEY, role_code text NOT NULL UNIQUE,
 legacy_role_name text NOT NULL, common_role_id integer NOT NULL REFERENCES common.roles(roleid)
);
CREATE TABLE IF NOT EXISTS dms.common_user_migration_map (
 legacy_user_id integer PRIMARY KEY, common_user_id integer REFERENCES common.users(pk_user),
 mapping_basis text NOT NULL DEFAULT 'UNRESOLVED'
);

-- Exact permission display names within the selected module. Preserve codes in the DMS bridge,
-- because common.permissions has no permission_code column.
INSERT INTO common.permissions(permission,isactive,isarchived,isverified,fk_module_id,created_by)
SELECT p.permission,1,0,CASE WHEN p.isverified THEN 1 ELSE 0 END,
 current_setting('dms.seed_module_id')::integer,current_setting('dms.seed_actor_id')::integer
FROM dms.permissions p WHERE p.isactive AND NOT coalesce(p.isarchived,false)
 AND NOT EXISTS(SELECT 1 FROM common.permissions c WHERE c.fk_module_id=current_setting('dms.seed_module_id')::integer AND lower(trim(c.permission))=lower(trim(p.permission)));

INSERT INTO dms.common_permission_map(legacy_permission_id,permission_code,permission_name,common_permission_id)
SELECT p.pk_permission,p.permission_code,p.permission,c.pk_permission
FROM dms.permissions p JOIN common.permissions c ON c.fk_module_id=current_setting('dms.seed_module_id')::integer
 AND lower(trim(c.permission))=lower(trim(p.permission))
WHERE p.isactive AND NOT coalesce(p.isarchived,false) AND nullif(trim(p.permission_code),'') IS NOT NULL
ON CONFLICT(legacy_permission_id) DO NOTHING;

-- Dedicated DMS role names avoid granting application permissions to unrelated shared roles.
-- Match by the immutable DMS role code, not by unrelated numeric role IDs.
INSERT INTO common.roles(role_name,isactive,isarchived,created_by)
SELECT 'DMS / '||r.role_code,1,0,current_setting('dms.seed_actor_id')::integer
FROM dms.roles r WHERE r.isactive AND NOT coalesce(r.isarchived,false)
 AND NOT EXISTS(SELECT 1 FROM common.roles c WHERE lower(trim(c.role_name))=lower('DMS / '||r.role_code));

INSERT INTO dms.common_role_map(legacy_role_id,role_code,legacy_role_name,common_role_id)
SELECT r.pk_role,r.role_code,r.role_name,c.roleid
FROM dms.roles r JOIN common.roles c ON lower(trim(c.role_name))=lower('DMS / '||r.role_code)
WHERE r.isactive AND NOT coalesce(r.isarchived,false)
ON CONFLICT(legacy_role_id) DO NOTHING;

INSERT INTO common.role_permission(role_id,permission_id)
SELECT rm.common_role_id,pm.common_permission_id FROM dms.role_permission rp
JOIN dms.common_role_map rm ON rm.legacy_role_id=rp.role_id
JOIN dms.common_permission_map pm ON pm.legacy_permission_id=rp.permission_id
WHERE NOT EXISTS(SELECT 1 FROM common.role_permission c WHERE c.role_id=rm.common_role_id AND c.permission_id=pm.common_permission_id);

-- User IDs cannot be inferred from code. Preserve unresolved identities until reviewed.
-- An exact normalized username is accepted only when it occurs exactly once in each schema.
INSERT INTO dms.common_user_migration_map(legacy_user_id,common_user_id,mapping_basis)
SELECT d.pk_user,
 CASE WHEN (SELECT count(*) FROM common.users c WHERE lower(trim(c.username))=lower(trim(d.username)))=1
       AND (SELECT count(*) FROM dms.users u WHERE lower(trim(u.username))=lower(trim(d.username)))=1
      THEN (SELECT min(c.pk_user) FROM common.users c WHERE lower(trim(c.username))=lower(trim(d.username))) END,
 CASE WHEN (SELECT count(*) FROM common.users c WHERE lower(trim(c.username))=lower(trim(d.username)))=1
       AND (SELECT count(*) FROM dms.users u WHERE lower(trim(u.username))=lower(trim(d.username)))=1
      THEN 'EXACT_USERNAME_REQUIRES_REVIEW' ELSE 'UNRESOLVED' END
FROM dms.users d ON CONFLICT(legacy_user_id) DO NOTHING;

SELECT * FROM dms.common_permission_map ORDER BY permission_code;
SELECT * FROM dms.common_role_map ORDER BY role_code;
SELECT legacy_user_id,common_user_id,mapping_basis FROM dms.common_user_migration_map ORDER BY legacy_user_id;
-- Deliberately no authentication switch, existing function replacement, FK replacement, or DROP.
-- Any future new common routines must be named sp_dms_{name}.
ROLLBACK;
