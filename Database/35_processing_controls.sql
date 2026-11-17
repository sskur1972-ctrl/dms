BEGIN;
SET LOCAL lock_timeout='15s';
SET LOCAL search_path=dms,public;
DO $$ BEGIN IF current_database()<>'UnifiedPlatform_dms' THEN RAISE EXCEPTION 'Wrong database'; END IF; END $$;

ALTER TABLE dms.change_control_references ADD COLUMN IF NOT EXISTS applicability text;
ALTER TABLE dms.change_control_references ADD COLUMN IF NOT EXISTS plant_reference text;
ALTER TABLE dms.change_control_references ADD COLUMN IF NOT EXISTS record_category text NOT NULL DEFAULT 'DOC';
ALTER TABLE dms.change_control_references ADD COLUMN IF NOT EXISTS change_type text;
ALTER TABLE dms.change_control_references ADD COLUMN IF NOT EXISTS description text;
ALTER TABLE dms.change_control_references ADD COLUMN IF NOT EXISTS reason_for_change text;
ALTER TABLE dms.change_control_references ADD COLUMN IF NOT EXISTS impact_assessment text;
ALTER TABLE dms.change_control_references ADD COLUMN IF NOT EXISTS requires_action_plan boolean NOT NULL DEFAULT false;
CREATE TABLE IF NOT EXISTS dms.change_control_signatures (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 change_control_id bigint NOT NULL REFERENCES dms.change_control_references(id),
 action_code text NOT NULL CHECK(action_code IN ('VERIFY','APPROVE')),
 user_id integer NOT NULL REFERENCES common.users(pk_user), signer_name text NOT NULL,
 meaning text NOT NULL, signed_at timestamp NOT NULL DEFAULT (now() AT TIME ZONE 'UTC'),
 content_sha256 text NOT NULL, ip_address text,
 UNIQUE(change_control_id,action_code)
);

CREATE OR REPLACE FUNCTION dms.sp_changecontrol_register_doc(p_no text,p_title text,p_on date,p_remarks text,p_scope text,p_plant text,p_actor integer,p_ip text)
RETURNS bigint LANGUAGE plpgsql SET search_path=dms,public AS $$
DECLARE v_id bigint;
BEGIN
 IF p_scope NOT IN ('PLANT','GLOBAL') OR p_scope IS NULL THEN RAISE EXCEPTION 'INVALID_INPUT: choose Plant or Global.' USING ERRCODE='45004'; END IF;
 IF p_scope='PLANT' AND length(trim(coalesce(p_plant,''))) NOT BETWEEN 1 AND 128 THEN RAISE EXCEPTION 'INVALID_INPUT: specify the plant.' USING ERRCODE='45004'; END IF;
 IF NOT EXISTS(SELECT 1 FROM common.users WHERE pk_user=p_actor AND isactive=1 AND isarchived=0) THEN RAISE EXCEPTION 'FORBIDDEN: inactive account.' USING ERRCODE='45004'; END IF;
 v_id:=sp_changecontrol_register(p_no,p_title,p_on,p_remarks,p_actor,p_ip);
 UPDATE change_control_references SET applicability=p_scope,plant_reference=CASE WHEN p_scope='PLANT' THEN trim(p_plant) END WHERE id=v_id;
 PERFORM sp_audit_write('CHANGE_CONTROL',v_id,p_no,'UPDATE','DOC change-control applicability recorded',p_actor,NULL,json_build_object('applicability',p_scope,'plant',p_plant),NULL,p_ip);
 RETURN v_id;
END $$;

CREATE OR REPLACE FUNCTION dms.sp_changecontrol_doc_details(p_id bigint,p_scope text,p_plant text,p_type text,p_description text,p_reason text,p_impact text,p_plan boolean,p_actor integer,p_ip text)
RETURNS bigint LANGUAGE plpgsql SET search_path=dms,public AS $$
DECLARE c record;
BEGIN
 SELECT * INTO c FROM change_control_references WHERE id=p_id FOR UPDATE;
 IF NOT FOUND THEN RETURN 0; END IF;
 IF NOT common.sp_dms_user_has_permission(p_actor,'changecontrol.verify') THEN RAISE EXCEPTION 'FORBIDDEN: change-control permission required.' USING ERRCODE='45004'; END IF;
 IF c.record_category<>'DOC' OR c.status<>'UNVERIFIED' THEN RAISE EXCEPTION 'INVALID_STATE: signed DOC details cannot be changed.' USING ERRCODE='45003'; END IF;
 IF p_scope IS NULL OR p_scope NOT IN ('PLANT','GLOBAL') OR (p_scope='PLANT' AND length(trim(coalesce(p_plant,''))) NOT BETWEEN 1 AND 128) THEN RAISE EXCEPTION 'INVALID_INPUT: select Global or Plant with a plant reference.' USING ERRCODE='45004'; END IF;
 IF p_type IS NULL OR p_type NOT IN ('PERMANENT','TEMPORARY') THEN RAISE EXCEPTION 'INVALID_INPUT: select permanent or temporary change.' USING ERRCODE='45004'; END IF;
 IF length(trim(coalesce(p_description,''))) NOT BETWEEN 3 AND 4000 OR length(trim(coalesce(p_reason,''))) NOT BETWEEN 3 AND 4000 OR length(trim(coalesce(p_impact,''))) NOT BETWEEN 3 AND 4000 THEN RAISE EXCEPTION 'INVALID_INPUT: enter description, reason and impact assessment (3–4000 characters each).' USING ERRCODE='45004'; END IF;
 UPDATE change_control_references SET applicability=p_scope,plant_reference=CASE WHEN p_scope='PLANT' THEN trim(p_plant) END,change_type=p_type,
 description=trim(p_description),reason_for_change=trim(p_reason),impact_assessment=trim(p_impact),requires_action_plan=coalesce(p_plan,false),updated_by=p_actor,updated_at=now() AT TIME ZONE 'UTC' WHERE id=p_id;
 PERFORM sp_audit_write('CHANGE_CONTROL',p_id,c.cc_number,'UPDATE','DOC change-control details recorded',p_actor,
 json_build_object('scope',c.applicability,'plant',c.plant_reference,'type',c.change_type,'description',c.description,'reason',c.reason_for_change,'impact',c.impact_assessment,'action_plan',c.requires_action_plan),
 json_build_object('scope',p_scope,'plant',p_plant,'type',p_type,'description',p_description,'reason',p_reason,'impact',p_impact,'action_plan',p_plan),NULL,p_ip);
 RETURN p_id;
END $$;

CREATE OR REPLACE FUNCTION dms.sp_changecontrol_register_full(p_no text,p_title text,p_on date,p_remarks text,p_scope text,p_plant text,p_type text,p_description text,p_reason text,p_impact text,p_plan boolean,p_actor integer,p_ip text)
RETURNS bigint LANGUAGE plpgsql SET search_path=dms,public AS $$
DECLARE v_id bigint;
BEGIN
 v_id:=sp_changecontrol_register_doc(p_no,p_title,p_on,p_remarks,p_scope,p_plant,p_actor,p_ip);
 PERFORM sp_changecontrol_doc_details(v_id,p_scope,p_plant,p_type,p_description,p_reason,p_impact,p_plan,p_actor,p_ip);
 RETURN v_id;
END $$;

CREATE OR REPLACE FUNCTION dms.sp_changecontrol_sign(p_id bigint,p_action text,p_remarks text,p_signed boolean,p_ack boolean,p_actor integer,p_ip text)
RETURNS integer LANGUAGE plpgsql SET search_path=dms,public AS $$
DECLARE c record;u record;v_meaning text;v_status text;v_sha text;
BEGIN
 IF NOT coalesce(p_signed,false) OR NOT coalesce(p_ack,false) THEN RAISE EXCEPTION 'SIGNATURE_REQUIRED: re-enter your own credentials and acknowledge the signature.' USING ERRCODE='45006'; END IF;
 SELECT * INTO c FROM change_control_references WHERE id=p_id FOR UPDATE;
 IF NOT FOUND THEN RETURN 0; END IF;
 SELECT * INTO u FROM common.dms_users WHERE pk_user=p_actor AND isactive AND NOT isarchived;
 IF NOT FOUND THEN RAISE EXCEPTION 'FORBIDDEN: inactive account.' USING ERRCODE='45004'; END IF;
 IF NOT common.sp_dms_user_has_permission(p_actor,'changecontrol.verify') THEN RAISE EXCEPTION 'FORBIDDEN: change-control permission required.' USING ERRCODE='45004'; END IF;
 IF c.record_category<>'DOC' OR c.applicability NOT IN ('PLANT','GLOBAL') OR c.applicability IS NULL THEN RAISE EXCEPTION 'INVALID_INPUT: DOC applicability has not been recorded.' USING ERRCODE='45004'; END IF;
 IF c.change_type IS NULL OR c.description IS NULL OR c.reason_for_change IS NULL OR c.impact_assessment IS NULL THEN RAISE EXCEPTION 'INVALID_INPUT: complete DOC change-control details before signing.' USING ERRCODE='45004'; END IF;
 IF p_action='VERIFY' AND c.status='UNVERIFIED' THEN
  IF c.created_by=p_actor THEN RAISE EXCEPTION 'FORBIDDEN: a different user must verify the change control.' USING ERRCODE='45004'; END IF;
  v_meaning:='Change control verified by';v_status:='PENDING_APPROVAL';
 ELSIF p_action='APPROVE' AND c.status='PENDING_APPROVAL' THEN
  IF c.created_by=p_actor OR c.verified_by=p_actor THEN RAISE EXCEPTION 'FORBIDDEN: approval requires a user other than the registrant and verifier.' USING ERRCODE='45004'; END IF;
  v_meaning:='Change control approved by';v_status:='VERIFIED';
 ELSE RAISE EXCEPTION 'INVALID_STATE: this verification or approval is not available.' USING ERRCODE='45003'; END IF;
 v_sha:=encode(sha256(convert_to(jsonb_build_object('number',c.cc_number,'title',c.title,'initiated',c.initiated_on,'scope',c.applicability,'plant',c.plant_reference,'category',c.record_category,'type',c.change_type,'description',c.description,'reason',c.reason_for_change,'impact',c.impact_assessment,'action_plan',c.requires_action_plan)::text,'UTF8')),'hex');
 IF p_action='APPROVE' AND NOT EXISTS(SELECT 1 FROM change_control_signatures WHERE change_control_id=p_id AND action_code='VERIFY' AND content_sha256=v_sha) THEN RAISE EXCEPTION 'CONCURRENCY_CONFLICT: change-control details no longer match the verified content.' USING ERRCODE='45001'; END IF;
 INSERT INTO change_control_signatures(change_control_id,action_code,user_id,signer_name,meaning,content_sha256,ip_address)
 VALUES(p_id,p_action,p_actor,coalesce(u.displayname,u.username),v_meaning,v_sha,p_ip);
 UPDATE change_control_references SET status=v_status,verified_by=CASE WHEN p_action='VERIFY' THEN p_actor ELSE verified_by END,
 verified_at=CASE WHEN p_action='VERIFY' THEN now() AT TIME ZONE 'UTC' ELSE verified_at END,
 remarks=coalesce(nullif(trim(p_remarks),''),remarks),updated_by=p_actor,updated_at=now() AT TIME ZONE 'UTC' WHERE id=p_id;
 PERFORM sp_audit_write('CHANGE_CONTROL',p_id,c.cc_number,'SIGN',v_meaning,p_actor,json_build_object('status',c.status),json_build_object('status',v_status,'meaning',v_meaning,'signer',coalesce(u.displayname,u.username),'sha256',v_sha),p_remarks,p_ip);
 RETURN 1;
END $$;

-- Block the former unsigned verification entry point.
CREATE OR REPLACE FUNCTION dms.sp_changecontrol_verify(p_id bigint,p_remarks text,p_actor integer,p_ip text)
RETURNS integer LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'SIGNATURE_REQUIRED: use signed DOC verification and approval.' USING ERRCODE='45006'; END $$;

CREATE OR REPLACE FUNCTION dms.sp_changecontrol_register_search(p_q text,p_status text)
RETURNS TABLE(id bigint,cc_number varchar,title varchar,source varchar,status varchar,initiated_on date,verified_by bigint,verified_by_name varchar,verified_at timestamp,remarks varchar,created_at timestamp,created_by_name varchar,document_count bigint,applicability text,plant_reference text,signature_summary text)
LANGUAGE sql STABLE SET search_path=dms,public AS $$
 SELECT x.*,c.applicability,c.plant_reference,
 (SELECT string_agg(s.meaning||' '||s.signer_name||' · '||to_char(s.signed_at,'DD-Mon-YYYY HH24:MI')||' UTC',E'\n' ORDER BY s.id) FROM change_control_signatures s WHERE s.change_control_id=c.id)
 FROM sp_changecontrol_search(p_q,p_status) x JOIN change_control_references c ON c.id=x.id;
$$;

-- Change only the known DMS function bodies; fail if an upstream version differs.
DO $patch$
DECLARE v_oid oid;v_sql text;v_old text;v_new text;
BEGIN
 SELECT oid INTO STRICT v_oid FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_workflow_execute';
 v_sql:=pg_get_functiondef(v_oid);
 IF position('Change control is fixed on the revision' IN v_sql)=0 AND position('    IF coalesce(trim(p_change_control_number)' IN v_sql)>0 THEN
  v_old:=substring(v_sql FROM position('    IF coalesce(trim(p_change_control_number)' IN v_sql) FOR position('    IF v_cc_required AND v_cc_id IS NULL THEN' IN v_sql)-position('    IF coalesce(trim(p_change_control_number)' IN v_sql));
 END IF;
 IF v_old IS NULL AND position('Change control is fixed on the revision' IN v_sql)=0 THEN RAISE EXCEPTION 'Unexpected workflow change-control body'; END IF;
 IF v_old IS NOT NULL THEN
  v_new:=E'    -- Change control is fixed on the revision; workflow posts cannot replace it.\n    IF coalesce(trim(p_change_control_number),'''')<>'''' AND NOT EXISTS(SELECT 1 FROM change_control_references WHERE id=v.change_control_id AND lower(cc_number)=lower(trim(p_change_control_number))) THEN\n        RAISE EXCEPTION ''INVALID_INPUT: change control cannot be changed during submission.'' USING ERRCODE=''45004'';\n    END IF;\n';
  v_sql:=replace(v_sql,v_old,v_new);
 END IF;
 v_sql:=replace(v_sql,'IF t.requires_signature THEN','IF (t.requires_signature OR upper(t.action_code) IN (''PUBLISH'',''MAKE_EFFECTIVE'')) AND NOT (upper(t.action_code)=''MAKE_EFFECTIVE'' AND p_ip=''scheduler'' AND coalesce(current_setting(''dms.scheduled_effective'',true),'''')=''1'') THEN');
 EXECUTE v_sql;
 -- Publication must use a live QA-verified reference if a change control is linked.
 v_sql:=pg_get_functiondef(v_oid);
 IF position('linked change control must be verified' IN v_sql)=0 THEN
  v_sql:=replace(v_sql,'v_cc_required := t.requires_change_control AND coalesce(d.type_cc, true);',E'v_cc_required := t.requires_change_control AND coalesce(d.type_cc, true);\n    IF upper(t.action_code)=''PUBLISH'' AND v_cc_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM change_control_references WHERE id=v_cc_id AND status=''VERIFIED'' AND is_active) THEN RAISE EXCEPTION ''INVALID_STATE: linked change control must be verified and active before publication.'' USING ERRCODE=''45003''; END IF;');
  EXECUTE v_sql;
 END IF;
 SELECT oid INTO STRICT v_oid FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_print_copy_issue';
 v_sql:=pg_get_functiondef(v_oid);
 v_old:=substring(v_sql FROM '    ELSIF c.status = ''ISSUED'' THEN[\s\S]*?    ELSE');
 IF v_old IS NOT NULL THEN v_sql:=replace(v_sql,v_old,E'    ELSIF c.status = ''ISSUED'' THEN\n        RAISE EXCEPTION ''INVALID_STATE: this copy was already issued; request a new numbered copy.'' USING ERRCODE=''45003'';\n    ELSE');
 ELSIF position('request a new numbered copy' IN v_sql)=0 THEN RAISE EXCEPTION 'Unexpected print issue body'; END IF;
 EXECUTE v_sql;
 SELECT oid INTO STRICT v_oid FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_print_request_create';
 v_sql:=pg_get_functiondef(v_oid);
 v_sql:=replace(v_sql,'IF p_copies IS NULL OR p_copies < 1 OR p_copies > v_max THEN','IF p_copies IS DISTINCT FROM 1 THEN');
 v_sql:=replace(v_sql,'ask for between 1 and % copies.','ask for exactly one copy (limit %).');
 EXECUTE v_sql;
 SELECT oid INTO STRICT v_oid FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_print_request_decide';
 v_sql:=pg_get_functiondef(v_oid);
 v_sql:=replace(v_sql,'BEGIN',E'BEGIN\n    IF p_approve AND p_copies_authorized IS DISTINCT FROM 1 THEN RAISE EXCEPTION ''INVALID_INPUT: authorise exactly one copy.'' USING ERRCODE=''45004''; END IF;');
 IF position('authorise exactly one copy' IN pg_get_functiondef(v_oid))=0 THEN EXECUTE v_sql; END IF;
END $patch$;

UPDATE dms.document_state_transitions SET requires_signature=true,
 signature_meaning=coalesce(nullif(signature_meaning,''),CASE WHEN upper(action_code)='PUBLISH' THEN 'Published by' ELSE 'Made effective by' END)
WHERE upper(action_code) IN ('PUBLISH','MAKE_EFFECTIVE');
-- Scheduled activation is an audited service event, never a fabricated signature.
DO $scheduled$
DECLARE v_oid oid;v_sql text;
BEGIN
 SELECT oid INTO STRICT v_oid FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_scheduler_make_effective';
 v_sql:=pg_get_functiondef(v_oid);
 IF position('dms.scheduled_effective' IN v_sql)=0 THEN
  IF position('PERFORM * FROM sp_workflow_execute' IN v_sql)=0 THEN RAISE EXCEPTION 'Unexpected scheduled activation body'; END IF;
  v_sql:=replace(v_sql,'PERFORM * FROM sp_workflow_execute',E'PERFORM set_config(''dms.scheduled_effective'',''1'',true);\n                PERFORM * FROM sp_workflow_execute');
  v_sql:=replace(v_sql,'v_done := true;',E'PERFORM set_config(''dms.scheduled_effective'',''0'',true);\n                v_done := true;');
  EXECUTE v_sql;
 END IF;
END $scheduled$;

INSERT INTO dms.system_config(config_key,config_value,value_type,category,description,is_gxp_relevant)
SELECT k,v,t,'Rendition',d,true FROM (VALUES
 ('rendition.provider','LIBREOFFICE','STRING','PDF engine: LIBREOFFICE or COLLABORA.'),
 ('rendition.collaboraUrl','http://localhost:9981','STRING','Internal Collabora conversion origin.')
) x(k,v,t,d) WHERE NOT EXISTS(SELECT 1 FROM dms.system_config c WHERE c.config_key=x.k);
UPDATE dms.system_config SET config_value='1' WHERE config_key='print.maxCopiesPerRequest';
COMMIT;
