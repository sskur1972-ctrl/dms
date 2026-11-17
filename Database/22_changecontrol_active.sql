BEGIN;
ALTER TABLE dms.change_control_references ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true;
CREATE OR REPLACE FUNCTION dms.sp_changecontrol_search(p_q text,p_status text)
RETURNS TABLE(id bigint,cc_number varchar,title varchar,source varchar,status varchar,initiated_on date,
 verified_by bigint,verified_by_name varchar,verified_at timestamp,remarks varchar,created_at timestamp,
 created_by_name varchar,document_count bigint)
LANGUAGE sql STABLE SET search_path=dms,public AS $function$
 SELECT c.id,c.cc_number,c.title,c.source,c.status,c.initiated_on,c.verified_by,v.displayname,c.verified_at,
 c.remarks,c.created_at,cb.displayname,(SELECT count(*) FROM document_versions dv WHERE dv.change_control_id=c.id)
 FROM change_control_references c LEFT JOIN users v ON v.pk_user=c.verified_by LEFT JOIN users cb ON cb.pk_user=c.created_by
 WHERE (coalesce(p_q,'')='' OR c.cc_number ILIKE '%'||p_q||'%' OR c.title ILIKE '%'||p_q||'%')
 AND (coalesce(p_status,'')='' OR (upper(p_status)='ACTIVE' AND c.is_active AND c.status IN ('VERIFIED','UNVERIFIED')) OR c.status=upper(p_status))
 ORDER BY c.created_at DESC,c.id DESC LIMIT 500;
$function$;
COMMIT;
