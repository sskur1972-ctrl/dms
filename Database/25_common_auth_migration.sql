-- Apply explicitly ONLY to UnifiedPlatform_dms after a full backup.
-- Canonical credentials and grants live in common. DMS profile IDs are retained
-- so historical ownership, signature manifestations and audit hashes stay intact.
BEGIN;
SET LOCAL search_path = dms, public;
DO $guard$ BEGIN
 IF current_database() <> 'UnifiedPlatform_dms' THEN RAISE EXCEPTION 'Wrong database: migration requires UnifiedPlatform_dms'; END IF;
 IF to_regclass('dms.users_auth_profile') IS NOT NULL THEN RAISE EXCEPTION 'Common authentication migration already applied'; END IF;
END $guard$;
CREATE TEMP TABLE common_function_baseline AS
 SELECT p.oid, md5(pg_get_functiondef(p.oid)) fingerprint FROM pg_proc p
 JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='common' AND p.prokind IN ('f','p');
CREATE TEMP TABLE dms_auth_function_baseline AS
 SELECT p.oid, p.proname, pg_get_functiondef(p.oid) definition FROM pg_proc p
 JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='dms' AND p.prokind='f'
 AND pg_get_functiondef(p.oid) ~* '\m(users|roles|permissions|role_permission)\M';
LOCK TABLE dms.users, dms.roles, dms.permissions, dms.role_permission IN ACCESS EXCLUSIVE MODE;
ALTER TABLE dms.users RENAME TO users_auth_profile;
ALTER TABLE dms.roles RENAME TO roles_auth_profile;
ALTER TABLE dms.permissions RENAME TO permissions_auth_profile;
ALTER TABLE dms.role_permission RENAME TO role_permission_auth_profile;
ALTER TABLE dms.users_auth_profile ADD COLUMN common_id integer UNIQUE REFERENCES common.users(pk_user);
ALTER TABLE dms.roles_auth_profile ADD COLUMN common_id integer UNIQUE REFERENCES common.roles(roleid);
ALTER TABLE dms.permissions_auth_profile ADD COLUMN common_id integer UNIQUE REFERENCES common.permissions(pk_permission);
ALTER TABLE dms.role_permission_auth_profile ADD COLUMN common_id bigint UNIQUE REFERENCES common.role_permission(pk_role_permission);

DO $seed$
DECLARE r record; mapped integer; grant_id bigint; module_id integer; matches integer;
BEGIN
 SELECT min(m.module_id) INTO module_id FROM common.module_master m WHERE lower(trim(module_name))='dms' AND coalesce(is_archived,0)=0;
 IF module_id IS NULL THEN INSERT INTO common.module_master(module_name,is_archived) VALUES('DMS',0) RETURNING common.module_master.module_id INTO module_id; END IF;
 FOR r IN SELECT * FROM dms.roles_auth_profile LOOP
  SELECT count(*),min(roleid) INTO matches,mapped FROM common.roles WHERE role_name='DMS / '||coalesce(r.role_code,r.pk_role::text);
  IF matches>1 THEN RAISE EXCEPTION 'Ambiguous DMS role mapping'; END IF;
  IF mapped IS NULL THEN INSERT INTO common.roles(role_name,isactive,isarchived) VALUES('DMS / '||coalesce(r.role_code,r.pk_role::text),r.isactive::int,coalesce(r.isarchived,false)::int) RETURNING roleid INTO mapped; END IF;
  UPDATE dms.roles_auth_profile SET common_id=mapped WHERE pk_role=r.pk_role;
 END LOOP;
 FOR r IN SELECT * FROM dms.permissions_auth_profile LOOP
  SELECT count(*),min(pk_permission) INTO matches,mapped FROM common.permissions WHERE fk_module_id=module_id AND lower(trim(permission))=lower(trim(r.permission));
  IF matches>1 THEN RAISE EXCEPTION 'Ambiguous DMS permission mapping'; END IF;
  IF mapped IS NULL THEN INSERT INTO common.permissions(permission,isactive,isarchived,isverified,fk_module_id) VALUES(r.permission,r.isactive::int,coalesce(r.isarchived,false)::int,coalesce(r.isverified,false)::int,module_id) RETURNING pk_permission INTO mapped; END IF;
  UPDATE dms.permissions_auth_profile SET common_id=mapped WHERE pk_permission=r.pk_permission;
 END LOOP;
 FOR r IN SELECT * FROM dms.users_auth_profile LOOP
  SELECT count(*),min(pk_user) INTO matches,mapped FROM common.users WHERE lower(trim(username))=lower(trim(r.username));
  IF matches>1 THEN RAISE EXCEPTION 'Ambiguous username mapping; migration refused'; END IF;
  IF mapped IS NULL THEN
   INSERT INTO common.users(username,password,role_id,emailid,address,displayname,mobilenumber,fk_department_id,isactive,isarchived,isfirstlogin)
   VALUES(r.username,r.password,(SELECT common_id FROM dms.roles_auth_profile WHERE pk_role=r.role_id),r.emailid,r.address,r.displayname,r.mobilenumber,coalesce(r.fk_department_id,0)::int,r.isactive::int,coalesce(r.isarchived,false)::int,r.must_change_password)
   RETURNING pk_user INTO mapped;
  END IF;
  UPDATE dms.users_auth_profile SET common_id=mapped WHERE pk_user=r.pk_user;
 END LOOP;
 FOR r IN SELECT g.*,rp.common_id cr,pp.common_id cp FROM dms.role_permission_auth_profile g JOIN dms.roles_auth_profile rp ON rp.pk_role=g.role_id JOIN dms.permissions_auth_profile pp ON pp.pk_permission=g.permission_id LOOP
  SELECT min(pk_role_permission) INTO grant_id FROM common.role_permission WHERE role_id=r.cr AND permission_id=r.cp;
  IF grant_id IS NULL THEN INSERT INTO common.role_permission(role_id,permission_id) VALUES(r.cr,r.cp) RETURNING pk_role_permission INTO grant_id; END IF;
  UPDATE dms.role_permission_auth_profile SET common_id=grant_id WHERE pk_role_permission=r.pk_role_permission;
 END LOOP;
END $seed$;

-- Generate compatible views from the actual installed column types. Existing DMS
-- functions are rebound below; foreign keys continue to reference stable profiles.
DO $views$
DECLARE entity text; base text; keycol text; commonkey text; col record; expr text; selects text;
 commoncols text; newvals text; updates text; profilecols text; profilevals text; profileupdates text; fn text; mapping text;
BEGIN
 FOREACH entity IN ARRAY ARRAY['users','roles','permissions','role_permission'] LOOP
  base=entity||'_auth_profile';
  keycol=CASE entity WHEN 'users' THEN 'pk_user' WHEN 'roles' THEN 'pk_role' WHEN 'permissions' THEN 'pk_permission' ELSE 'pk_role_permission' END;
  commonkey=CASE entity WHEN 'roles' THEN 'roleid' ELSE keycol END;
  selects='';commoncols='';newvals='';updates='';profilecols='';profilevals='';profileupdates='';
  FOR col IN SELECT a.attname,format_type(a.atttypid,a.atttypmod) typ FROM pg_attribute a WHERE a.attrelid=('dms.'||base)::regclass AND a.attnum>0 AND NOT a.attisdropped AND a.attname<>'common_id' ORDER BY a.attnum LOOP
   expr='p.'||quote_ident(col.attname);
   -- Application-specific role assignment and display captions remain DMS scoped.
   IF col.attname<>keycol AND col.attname<>'role_id' AND NOT(entity='roles' AND col.attname='role_name')
    AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='common' AND table_name=entity AND column_name=col.attname)
    AND col.attname NOT IN ('created_by','modified_by','created_on','modified_on','otp','fk_module_id') THEN
    expr=CASE WHEN col.typ='boolean' THEN '(c.'||quote_ident(col.attname)||'=1)' ELSE 'c.'||quote_ident(col.attname)||'::'||col.typ END;
    IF col.attname='fk_department_id' THEN expr='nullif(c.fk_department_id,0)::'||col.typ; END IF;
   END IF;
   IF entity='role_permission' AND col.attname IN ('role_id','permission_id') THEN expr='p.'||quote_ident(col.attname); END IF;
   selects=selects||CASE WHEN selects='' THEN '' ELSE ',' END||expr||' AS '||quote_ident(col.attname);
   profilecols=profilecols||CASE WHEN profilecols='' THEN '' ELSE ',' END||quote_ident(col.attname);
   profilevals=profilevals||CASE WHEN profilevals='' THEN '' ELSE ',' END||'NEW.'||quote_ident(col.attname);
   IF col.attname<>keycol THEN profileupdates=profileupdates||CASE WHEN profileupdates='' THEN '' ELSE ',' END||quote_ident(col.attname)||'=NEW.'||quote_ident(col.attname); END IF;
   IF col.attname<>keycol AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='common' AND table_name=entity AND column_name=col.attname)
    AND col.attname NOT IN ('created_by','modified_by','created_on','modified_on','otp','fk_module_id') THEN
    mapping='NEW.'||quote_ident(col.attname);
    IF col.typ='boolean' THEN mapping='coalesce('||mapping||',false)::int'; END IF;
    IF col.attname='fk_department_id' THEN mapping='coalesce('||mapping||',0)::int'; END IF;
    IF col.attname='role_id' THEN mapping='(SELECT common_id FROM dms.roles_auth_profile WHERE pk_role=NEW.role_id)'; END IF;
    IF entity='role_permission' AND col.attname='permission_id' THEN mapping='(SELECT common_id FROM dms.permissions_auth_profile WHERE pk_permission=NEW.permission_id)'; END IF;
    commoncols=commoncols||CASE WHEN commoncols='' THEN '' ELSE ',' END||quote_ident(col.attname);
    newvals=newvals||CASE WHEN newvals='' THEN '' ELSE ',' END||mapping;
    updates=updates||CASE WHEN updates='' THEN '' ELSE ',' END||quote_ident(col.attname)||'='||mapping;
   END IF;
  END LOOP;
  IF entity='permissions' THEN
   commoncols=commoncols||',fk_module_id';
   newvals=newvals||',(SELECT min(module_id) FROM common.module_master WHERE lower(trim(module_name))=''dms'' AND coalesce(is_archived,0)=0)';
  END IF;
  EXECUTE format('CREATE VIEW dms.%I AS SELECT %s FROM dms.%I p JOIN common.%I c ON c.%I=p.common_id',entity,selects,base,entity,commonkey);
  FOR col IN SELECT a.attname,pg_get_expr(d.adbin,d.adrelid) def FROM pg_attribute a JOIN pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum WHERE a.attrelid=('dms.'||base)::regclass AND a.attname<>'common_id' LOOP
   EXECUTE format('ALTER VIEW dms.%I ALTER COLUMN %I SET DEFAULT %s',entity,col.attname,col.def);
  END LOOP;
  -- New routines obey the agreed common.sp_dms_* naming convention.
  fn=format($body$
   CREATE FUNCTION common.%I() RETURNS trigger LANGUAGE plpgsql SET search_path=dms,public AS $trigger$
   DECLARE mapped bigint;
   BEGIN
    IF TG_OP='INSERT' THEN
     INSERT INTO common.%I(%s) VALUES(%s) RETURNING %I INTO mapped;
     INSERT INTO dms.%I(%s,common_id) VALUES(%s,mapped);
     RETURN NEW;
    ELSIF TG_OP='UPDATE' THEN
     SELECT common_id INTO mapped FROM dms.%I WHERE %I=OLD.%I FOR UPDATE;
     UPDATE common.%I SET %s WHERE %I=mapped;
     UPDATE dms.%I SET %s WHERE %I=OLD.%I;
     RETURN NEW;
    ELSE
     SELECT common_id INTO mapped FROM dms.%I WHERE %I=OLD.%I FOR UPDATE;
     DELETE FROM dms.%I WHERE %I=OLD.%I;
     DELETE FROM common.%I WHERE %I=mapped;
     RETURN OLD;
    END IF;
   END $trigger$;
  $body$,'sp_dms_'||entity||'_bridge',entity,commoncols,newvals,commonkey,base,profilecols,profilevals,
   base,keycol,keycol,entity,updates,commonkey,base,profileupdates,keycol,keycol,
   base,keycol,keycol,base,keycol,keycol,entity,commonkey);
  EXECUTE fn;
  EXECUTE format('CREATE TRIGGER common_auth_write INSTEAD OF INSERT OR UPDATE OR DELETE ON dms.%I FOR EACH ROW EXECUTE FUNCTION common.%I()',entity,'sp_dms_'||entity||'_bridge');
 END LOOP;
END $views$;

-- Read grants directly from common, including grants maintained outside DMS.
-- Translate only the role/permission IDs at the application boundary.
DO $grants_view$ DECLARE col record; fields text=''; expr text;
BEGIN
 FOR col IN SELECT a.attname,format_type(a.atttypid,a.atttypmod) typ FROM pg_attribute a WHERE a.attrelid='dms.role_permission_auth_profile'::regclass AND a.attnum>0 AND NOT a.attisdropped AND a.attname<>'common_id' ORDER BY a.attnum LOOP
  expr=CASE col.attname WHEN 'pk_role_permission' THEN 'c.pk_role_permission::integer' WHEN 'role_id' THEN 'r.pk_role' WHEN 'permission_id' THEN 'p.pk_permission' ELSE 'g.'||quote_ident(col.attname) END;
  fields=fields||CASE WHEN fields='' THEN '' ELSE ',' END||expr||' AS '||quote_ident(col.attname);
 END LOOP;
 IF EXISTS(SELECT 1 FROM common.role_permission WHERE pk_role_permission>2147483647) THEN RAISE EXCEPTION 'Grant IDs exceed supported application integer range'; END IF;
 EXECUTE 'CREATE OR REPLACE VIEW dms.role_permission AS SELECT '||fields||' FROM common.role_permission c JOIN dms.roles_auth_profile r ON r.common_id=c.role_id JOIN dms.permissions_auth_profile p ON p.common_id=c.permission_id LEFT JOIN dms.role_permission_auth_profile g ON g.common_id=c.pk_role_permission';
END $grants_view$;
CREATE OR REPLACE FUNCTION common.sp_dms_role_permission_bridge() RETURNS trigger
LANGUAGE plpgsql SET search_path=dms,public AS $trigger$
DECLARE mapped bigint;
BEGIN
 IF TG_OP='INSERT' THEN
  PERFORM pg_advisory_xact_lock(73263642);
  SELECT c.pk_role_permission INTO mapped FROM common.role_permission c
   JOIN dms.roles_auth_profile r ON r.common_id=c.role_id
   JOIN dms.permissions_auth_profile p ON p.common_id=c.permission_id
   WHERE r.pk_role=NEW.role_id AND p.pk_permission=NEW.permission_id LIMIT 1;
  IF mapped IS NULL THEN
   INSERT INTO common.role_permission(role_id,permission_id)
    VALUES((SELECT common_id FROM dms.roles_auth_profile WHERE pk_role=NEW.role_id),
     (SELECT common_id FROM dms.permissions_auth_profile WHERE pk_permission=NEW.permission_id))
    RETURNING pk_role_permission INTO mapped;
  END IF;
  IF mapped>2147483647 THEN RAISE EXCEPTION 'Grant ID exceeds supported range'; END IF;
  NEW.pk_role_permission=mapped::integer;
  RETURN NEW;
 ELSIF TG_OP='DELETE' THEN
  DELETE FROM dms.role_permission_auth_profile WHERE common_id=OLD.pk_role_permission;
  DELETE FROM common.role_permission WHERE pk_role_permission=OLD.pk_role_permission;
  RETURN OLD;
 ELSE RAISE EXCEPTION 'Remove and recreate a grant to change its role or permission';
 END IF;
END $trigger$;

-- Force SQL functions to resolve their table references to the adapter views,
-- including definitions that had previously cached the physical table OIDs.
DO $rebind$ DECLARE r record; definition text;
BEGIN
 FOR r IN SELECT * FROM dms_auth_function_baseline LOOP
  definition=r.definition;
  IF r.proname='sp_addpermissionforrole' THEN
   definition=replace(definition,'BEGIN'||chr(10)||'    INSERT INTO role_permission',
    'BEGIN'||chr(10)||'    PERFORM pg_advisory_xact_lock(73263642);'||chr(10)||'    SELECT pk_role_permission INTO v_id FROM dms.role_permission WHERE role_id=p_roles_id AND permission_id=p_permission_id;'||chr(10)||'    IF v_id IS NOT NULL THEN RETURN v_id; END IF;'||chr(10)||'    INSERT INTO role_permission');
   definition=replace(definition,'ON CONFLICT (role_id, permission_id) DO UPDATE SET created_on = role_permission.created_on','');
  ELSIF r.proname='sp_dms_apply_v2_permission_grants' THEN
   definition=replace(definition,'BEGIN'||chr(10),'BEGIN'||chr(10)||'    PERFORM pg_advisory_xact_lock(73263642);'||chr(10));
   definition=replace(definition,'ON CONFLICT (role_id, permission_id) DO NOTHING','WHERE NOT EXISTS(SELECT 1 FROM dms.role_permission existing WHERE existing.role_id=rp.role_id AND existing.permission_id=pn.pk_permission)');
  END IF;
  EXECUTE definition;
 END LOOP;
 IF EXISTS(SELECT 1 FROM common_function_baseline b JOIN pg_proc p ON p.oid=b.oid WHERE b.fingerprint<>md5(pg_get_functiondef(p.oid))) THEN RAISE EXCEPTION 'Existing common routine changed; rolling back'; END IF;
END $rebind$;
CREATE TABLE dms.common_auth_migration_status(
 applied_at timestamptz NOT NULL DEFAULT now(), description text NOT NULL
);
INSERT INTO dms.common_auth_migration_status(description) VALUES('Common canonical credentials and grants; stable DMS domain IDs and security profiles. Existing common definitions unchanged.');
COMMIT;

