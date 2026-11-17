-- READ-ONLY. Safe to inspect while common must remain untouched.
-- Set the module ID below only to restrict name matching to the correct shared DMS module.
WITH parameters AS (SELECT NULL::integer AS dms_module_id)
SELECT p.pk_permission AS dms_permission_id,p.permission_code,p.permission,
 c.pk_permission AS common_permission_id,c.fk_module_id AS common_module_id,
 CASE WHEN c.pk_permission IS NULL THEN 'MISSING: seed after module selection'
      WHEN parameters.dms_module_id IS NULL THEN 'EXACT_NAME: module scope requires review'
      ELSE 'EXACT_NAME_AND_MODULE' END AS matching_basis
FROM dms.permissions p CROSS JOIN parameters
LEFT JOIN common.permissions c ON lower(trim(c.permission))=lower(trim(p.permission))
 AND (parameters.dms_module_id IS NULL OR c.fk_module_id=parameters.dms_module_id)
WHERE p.isactive AND NOT coalesce(p.isarchived,false) ORDER BY p.permission_code,c.pk_permission;

SELECT r.pk_role AS dms_role_id,r.role_code,r.role_name AS dms_role_name,
 'DMS / '||r.role_code AS proposed_common_role_name,c.roleid AS common_role_id
FROM dms.roles r LEFT JOIN common.roles c ON lower(trim(c.role_name))=lower('DMS / '||r.role_code)
WHERE r.isactive AND NOT coalesce(r.isarchived,false) ORDER BY r.role_code;

SELECT d.pk_user AS dms_user_id,c.pk_user AS proposed_common_user_id,
 CASE WHEN count(c.pk_user) OVER(PARTITION BY d.pk_user)=0 THEN 'UNRESOLVED'
      WHEN count(c.pk_user) OVER(PARTITION BY d.pk_user)>1 THEN 'AMBIGUOUS'
      ELSE 'EXACT_USERNAME_REQUIRES_REVIEW' END AS matching_basis
FROM dms.users d LEFT JOIN common.users c ON lower(trim(c.username))=lower(trim(d.username))
ORDER BY d.pk_user,c.pk_user;
