BEGIN;
SET LOCAL lock_timeout='15s';
SET LOCAL search_path=dms,public;

DROP FUNCTION IF EXISTS dms.sp_changecontrol_external_sync(text,text,text,date,text,text,text,text,text,text,text,boolean,integer,text,text);
DROP FUNCTION IF EXISTS dms.sp_changecontrol_external_sync(text,text,text,date,text,text,text,text,text,text,text,boolean,text,integer,text,text);
DROP FUNCTION IF EXISTS dms.sp_changecontrol_external_sync(text,text,text,date,text,text,text,text,text,text,text,boolean,text,bigint,integer,text,text);

CREATE OR REPLACE FUNCTION dms.sp_changecontrol_external_sync(
 p_mode text,p_no text,p_title text,p_on date,p_remarks text,p_scope text,p_plant text,
 p_type text,p_description text,p_reason text,p_impact text,p_plan boolean,p_status text,p_verified_by bigint,p_actor integer,p_ip text,p_external_user text
)
RETURNS TABLE(id bigint,operation text,status text,verified_by bigint,updated_at timestamp without time zone)
LANGUAGE plpgsql SET search_path=dms,public AS $$
DECLARE c record;v_id bigint;v_mode text:=upper(trim(coalesce(p_mode,'')));v_changed boolean;v_base_changed boolean;v_details_changed boolean;
 v_requested_status text:=upper(nullif(trim(coalesce(p_status,'')),''));v_next_status text;v_next_verified_by bigint;
BEGIN
 IF v_mode NOT IN ('CREATE','UPDATE') THEN RAISE EXCEPTION 'INVALID_INPUT: unsupported synchronization operation.' USING ERRCODE='45004'; END IF;
 IF length(trim(coalesce(p_no,''))) NOT BETWEEN 3 AND 64 THEN RAISE EXCEPTION 'INVALID_INPUT: Change Control number must contain 3 to 64 characters.' USING ERRCODE='45004'; END IF;
 IF v_requested_status IS NOT NULL AND v_requested_status NOT IN ('UNVERIFIED','PENDING_APPROVAL','VERIFIED','CLOSED','CANCEL') THEN RAISE EXCEPTION 'INVALID_INPUT: unsupported Change Control status.' USING ERRCODE='45004'; END IF;
 IF v_mode='CREATE' THEN v_requested_status:=coalesce(v_requested_status,'UNVERIFIED'); END IF;
 IF p_scope='PLANT' AND (coalesce(trim(p_plant),'') !~ '^[1-9][0-9]*$' OR length(trim(p_plant))>18) THEN RAISE EXCEPTION 'INVALID_INPUT: plant_reference must be a positive plant ID for PLANT applicability.' USING ERRCODE='45004'; END IF;
 IF p_scope='GLOBAL' THEN p_plant:=NULL; END IF;
 IF v_requested_status='VERIFIED' AND p_verified_by IS NULL THEN RAISE EXCEPTION 'INVALID_INPUT: verified_by is required when status is VERIFIED.' USING ERRCODE='45004'; END IF;
 IF p_verified_by IS NOT NULL AND NOT EXISTS(SELECT 1 FROM common.users WHERE pk_user=p_verified_by AND isactive=1 AND isarchived=0) THEN RAISE EXCEPTION 'INVALID_INPUT: verified_by must reference an active common user.' USING ERRCODE='45004'; END IF;
 IF NOT EXISTS(SELECT 1 FROM common.users WHERE pk_user=p_actor AND isactive=1 AND isarchived=0) THEN RAISE EXCEPTION 'FORBIDDEN: inactive account.' USING ERRCODE='45004'; END IF;
 IF NOT common.sp_dms_user_has_permission(p_actor,'changecontrol.verify') THEN RAISE EXCEPTION 'FORBIDDEN: change-control permission required.' USING ERRCODE='45004'; END IF;
 SELECT * INTO c FROM change_control_references WHERE lower(cc_number)=lower(trim(p_no)) FOR UPDATE;
 IF FOUND AND v_mode='CREATE' THEN RAISE EXCEPTION 'DUPLICATE: Change Control number already exists.' USING ERRCODE='45005'; END IF;
 IF NOT FOUND AND v_mode='UPDATE' THEN RAISE EXCEPTION 'NOT_FOUND: Change Control number was not found.' USING ERRCODE='P0002'; END IF;

 IF NOT FOUND THEN
  v_id:=sp_changecontrol_register_full(trim(p_no),p_title,p_on,p_remarks,p_scope,p_plant,p_type,p_description,p_reason,p_impact,p_plan,p_actor,p_ip);
  UPDATE change_control_references SET source='EXTERNAL',status=v_requested_status,
    verified_by=p_verified_by,
    verified_at=CASE WHEN v_requested_status='VERIFIED' THEN clock_timestamp() AT TIME ZONE 'UTC' ELSE verified_at END,
    updated_by=p_actor,updated_at=clock_timestamp() AT TIME ZONE 'UTC' WHERE change_control_references.id=v_id;
  PERFORM sp_audit_write('CHANGE_CONTROL',v_id,trim(p_no),'SYNC','Created from External; source user='||left(coalesce(p_external_user,'(not supplied)'),128),p_actor,NULL,
    json_build_object('source','EXTERNAL','operation','CREATE','status',v_requested_status,'verified_by',p_verified_by,'external_user',left(coalesce(p_external_user,'(not supplied)'),128)),NULL,p_ip);
  RETURN QUERY SELECT v_id,'CREATED'::text,v_requested_status,cc_row.verified_by,cc_row.updated_at FROM change_control_references cc_row WHERE cc_row.id=v_id;
  RETURN;
 END IF;

 IF c.record_category IS DISTINCT FROM 'DOC' THEN RAISE EXCEPTION 'INVALID_STATE: Only DOC Change Controls can be synchronized by this API.' USING ERRCODE='45003'; END IF;
 v_next_status:=coalesce(v_requested_status,c.status);
 v_next_verified_by:=coalesce(p_verified_by,c.verified_by);
 v_base_changed:=c.title IS DISTINCT FROM nullif(trim(p_title),'') OR c.initiated_on IS DISTINCT FROM p_on
   OR c.remarks IS DISTINCT FROM nullif(trim(p_remarks),'') OR c.source IS DISTINCT FROM 'EXTERNAL';
 v_details_changed:=c.applicability IS DISTINCT FROM p_scope
   OR (p_scope='PLANT' AND c.plant_reference IS DISTINCT FROM nullif(trim(p_plant),''))
   OR (p_scope='GLOBAL' AND c.plant_reference IS NOT NULL)
   OR c.change_type IS DISTINCT FROM p_type OR c.description IS DISTINCT FROM nullif(trim(p_description),'')
   OR c.reason_for_change IS DISTINCT FROM nullif(trim(p_reason),'') OR c.impact_assessment IS DISTINCT FROM nullif(trim(p_impact),'')
   OR c.requires_action_plan IS DISTINCT FROM coalesce(p_plan,false);
 IF c.status IS DISTINCT FROM 'UNVERIFIED' AND (v_base_changed OR v_details_changed) THEN
  RAISE EXCEPTION 'INVALID_STATE: Change Control details can only be changed while status is UNVERIFIED.' USING ERRCODE='45003';
 END IF;
 v_changed:=v_base_changed OR v_details_changed OR v_next_status IS DISTINCT FROM c.status OR v_next_verified_by IS DISTINCT FROM c.verified_by;
 IF NOT v_changed THEN RETURN QUERY SELECT c.id,'UNCHANGED'::text,c.status::text,c.verified_by,c.updated_at; RETURN; END IF;

 IF v_details_changed THEN
  PERFORM sp_changecontrol_doc_details(c.id,p_scope,p_plant,p_type,p_description,p_reason,p_impact,p_plan,p_actor,p_ip);
 END IF;

 IF v_base_changed THEN
  UPDATE change_control_references SET title=nullif(trim(p_title),''),initiated_on=p_on,
    remarks=nullif(trim(p_remarks),''),source='EXTERNAL',updated_by=p_actor,updated_at=now() AT TIME ZONE 'UTC'
    WHERE change_control_references.id=c.id;
  PERFORM sp_audit_write('CHANGE_CONTROL',c.id,c.cc_number,'SYNC','Updated from External; source user='||left(coalesce(p_external_user,'(not supplied)'),128),p_actor,
    json_build_object('title',c.title,'initiated_on',c.initiated_on,'remarks',c.remarks,'source',c.source),
    json_build_object('title',nullif(trim(p_title),''),'initiated_on',p_on,'remarks',nullif(trim(p_remarks),''),'source','EXTERNAL','external_user',left(coalesce(p_external_user,'(not supplied)'),128)),NULL,p_ip);
 END IF;
 IF v_next_status IS DISTINCT FROM c.status OR v_next_verified_by IS DISTINCT FROM c.verified_by THEN
  UPDATE change_control_references SET status=v_next_status,verified_by=v_next_verified_by,
    verified_at=CASE WHEN v_next_status='VERIFIED' THEN clock_timestamp() AT TIME ZONE 'UTC' ELSE verified_at END,
    updated_by=p_actor,updated_at=clock_timestamp() AT TIME ZONE 'UTC' WHERE change_control_references.id=c.id;
  PERFORM sp_audit_write('CHANGE_CONTROL',c.id,c.cc_number,'SYNC','External status synchronized; source user='||left(coalesce(p_external_user,'(not supplied)'),128),p_actor,
    json_build_object('status',c.status,'verified_by',c.verified_by),json_build_object('status',v_next_status,'verified_by',v_next_verified_by,'external_user',left(coalesce(p_external_user,'(not supplied)'),128)),NULL,p_ip);
 END IF;
 RETURN QUERY SELECT c.id,'UPDATED'::text,v_next_status,r.verified_by,r.updated_at FROM change_control_references r WHERE r.id=c.id;
END $$;

COMMIT;
