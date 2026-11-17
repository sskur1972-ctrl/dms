-- Bound the searchable field-name summary; retain every exact value in the JSON evidence.
CREATE OR REPLACE FUNCTION dms.fn_audit_field_summary(p_values json)
RETURNS text LANGUAGE sql IMMUTABLE AS $$
 WITH names AS (SELECT key,row_number() OVER(ORDER BY key) AS n,count(*) OVER() AS total
   FROM json_object_keys(CASE WHEN json_typeof(p_values)='object' THEN p_values ELSE '{}'::json END) AS fields(key))
 SELECT string_agg(left(key,72),', ' ORDER BY key) FILTER(WHERE n<=8)
   || CASE WHEN max(total)>8 THEN format(' and %s more fields (full values retained)',max(total)-8) ELSE '' END
 FROM names;
$$;

DO $patch$
DECLARE definition text; signature regprocedure;
BEGIN
 signature:=to_regprocedure('dms.sp_audit_append(text,bigint,text,text,text,bigint,bigint,bigint,text,text,text,bigint,text,text,text,text,text,text,json,json,text,text,bigint,boolean)');
 IF signature IS NULL THEN RAISE EXCEPTION 'DMS audit append routine is missing'; END IF;
 SELECT pg_get_functiondef(signature) INTO definition;
 IF position('dms.fn_audit_field_summary' IN definition)=0 THEN
  IF position('SELECT string_agg(key' IN definition)=0 THEN RAISE EXCEPTION 'Apply 39_audit_changed_values.sql before this migration'; END IF;
  definition:=regexp_replace(definition,
    'SELECT string_agg\(key,[^;]+INTO p_changed_fields FROM json_object_keys\(p_after_json\)[^;]+;',
    'p_changed_fields := dms.fn_audit_field_summary(p_after_json);');
  IF position('dms.fn_audit_field_summary' IN definition)=0 THEN RAISE EXCEPTION 'Unsupported audit append definition; no changes applied'; END IF;
  EXECUTE definition;
 END IF;
END $patch$;
