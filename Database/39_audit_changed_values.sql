-- DMS-only audit normalization. Existing audit records and common objects are never changed.
CREATE OR REPLACE FUNCTION dms.fn_audit_business_values(p_values json)
RETURNS json LANGUAGE plpgsql STABLE SET search_path=dms,common,public AS $$
DECLARE result jsonb:='{}'; r record; field text; label text; readable text;
BEGIN
 IF p_values IS NULL OR json_typeof(p_values)<>'object' THEN RETURN p_values; END IF;
 FOR r IN SELECT key,value FROM jsonb_each(p_values::jsonb) LOOP
  field:=lower(replace(replace(r.key,'_',''),'-','')); label:=NULL; readable:=NULL;
  IF field IN('roleid','roleids') AND jsonb_typeof(r.value)<>'array' THEN
   label:='Role'; SELECT role_name INTO readable FROM common.dms_roles WHERE pk_role::text=(r.value#>>'{}');
  ELSIF field IN('owneruserid','assigneduserid','controlleruserid','newassigneeuserid') THEN
   label:=CASE field WHEN 'owneruserid' THEN 'Owner' WHEN 'controlleruserid' THEN 'QA controller' ELSE 'Assigned to' END;
   SELECT coalesce(displayname,username) INTO readable FROM common.dms_users WHERE pk_user::text=(r.value#>>'{}');
  ELSIF field='changecontrolid' THEN
   label:='Change control number';SELECT cc_number INTO readable FROM dms.change_control_references WHERE id::text=(r.value#>>'{}');
  ELSIF field IN('stateid','currentstateid') THEN
   label:='State';SELECT name INTO readable FROM dms.document_states WHERE id::text=(r.value#>>'{}');
  END IF;
  IF label IS NOT NULL THEN
   result:=result||jsonb_build_object(label,CASE WHEN r.value='null'::jsonb THEN NULL ELSE coalesce(readable,'Name unavailable') END);
  ELSE result:=result||jsonb_build_object(r.key,r.value); END IF;
 END LOOP;
 RETURN result::json;
END $$;

CREATE OR REPLACE FUNCTION dms.fn_audit_changed_values(p_before json,p_after json)
RETURNS TABLE(old_values json,new_values json) LANGUAGE sql STABLE SET search_path=dms,common,public AS $$
 WITH payload AS (SELECT dms.fn_audit_business_values(p_before)::jsonb AS b,dms.fn_audit_business_values(p_after)::jsonb AS a),
 old_entries AS (SELECT b.* FROM payload p CROSS JOIN LATERAL jsonb_each(CASE WHEN jsonb_typeof(p.b)='object' THEN p.b ELSE '{}' END) b),
 new_entries AS (SELECT a.* FROM payload p CROSS JOIN LATERAL jsonb_each(CASE WHEN jsonb_typeof(p.a)='object' THEN p.a ELSE '{}' END) a),
 changed AS (
   SELECT coalesce(b.key,a.key) AS key,b.value AS old_value,a.value AS new_value
   FROM old_entries b FULL JOIN new_entries a ON a.key=b.key
   WHERE b.value IS DISTINCT FROM a.value)
 SELECT coalesce(jsonb_object_agg(key,old_value),'{}'::jsonb)::json,
        coalesce(jsonb_object_agg(key,new_value),'{}'::jsonb)::json FROM changed;
$$;

-- Patch only the entry point, before its existing hash/signature append logic.
DO $patch$
DECLARE definition text; signature regprocedure;
BEGIN
 signature:=to_regprocedure('dms.sp_audit_append(text,bigint,text,text,text,bigint,bigint,bigint,text,text,text,bigint,text,text,text,text,text,text,json,json,text,text,bigint,boolean)');
 IF signature IS NULL THEN RAISE EXCEPTION 'DMS audit writer is missing'; END IF;
 SELECT pg_get_functiondef(signature) INTO definition;
 IF position('dms.fn_audit_changed_values' IN definition)=0 THEN
  IF position('BEGIN' IN definition)=0 THEN RAISE EXCEPTION 'Unsupported DMS audit writer'; END IF;
  definition:=regexp_replace(definition,'BEGIN',E'BEGIN\n SELECT old_values,new_values INTO p_before_json,p_after_json FROM dms.fn_audit_changed_values(p_before_json,p_after_json);\n SELECT string_agg(key, '', '' ORDER BY key) INTO p_changed_fields FROM json_object_keys(p_after_json) AS fields(key);');
  EXECUTE definition;
 END IF;
END $patch$;
