BEGIN;
SET LOCAL lock_timeout='15s';
SET LOCAL search_path=dms,public;
DO $$ BEGIN IF current_database()<>'UnifiedPlatform_dms' THEN RAISE EXCEPTION 'Wrong database'; END IF; END $$;

-- Authorization seals that revision, even if an administrator later changes
-- the matrix or reopens the state. A new revision has independent authorization.
CREATE OR REPLACE FUNCTION dms.fn_revision_authorized(p_version bigint)
RETURNS boolean LANGUAGE sql STABLE SET search_path=dms,public AS $$
 SELECT EXISTS(SELECT 1 FROM document_workflow_instances
 WHERE document_version_id=p_version AND upper(stage_kind)='AUTHORIZATION'
 AND status='COMPLETED' AND decision='APPROVED');
$$;
CREATE OR REPLACE FUNCTION dms.trg_revision_content_guard()
RETURNS trigger LANGUAGE plpgsql SET search_path=dms,public AS $$
DECLARE v_revision bigint;v_doc bigint;
BEGIN
 v_revision:=CASE WHEN TG_TABLE_NAME='document_versions' THEN (to_jsonb(OLD)->>'id')::bigint ELSE (to_jsonb(NEW)->>'document_version_id')::bigint END;
 SELECT document_id INTO v_doc FROM document_versions WHERE id=v_revision;
 -- Workflow also locks the document. A save cannot cross an authorization.
 PERFORM 1 FROM documents WHERE id=v_doc FOR UPDATE;
 IF fn_revision_authorized(v_revision) THEN
  RAISE EXCEPTION 'INVALID_STATE: Technical Director authorization has sealed this revision. Create a new revision to change its content.' USING ERRCODE='45003';
 END IF;
 IF TG_TABLE_NAME='document_versions' THEN
 IF NEW.source_sha256 IS DISTINCT FROM OLD.source_sha256 THEN
  NEW.rendition_base_path:=NULL;NEW.rendition_base_sha256:=NULL;NEW.rendition_source_sha256:=NULL;
  NEW.rendered_pdf_path:=NULL;NEW.rendered_pdf_sha256:=NULL;NEW.rendered_at:=NULL;
 END IF;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS dms_revision_content_guard ON dms.document_versions;
CREATE TRIGGER dms_revision_content_guard BEFORE UPDATE OF source_file_name,source_storage_path,source_mime_type,source_size_bytes,source_sha256
ON dms.document_versions FOR EACH ROW EXECUTE FUNCTION dms.trg_revision_content_guard();
DROP TRIGGER IF EXISTS dms_html_content_guard ON dms.document_content;
CREATE TRIGGER dms_html_content_guard BEFORE INSERT OR UPDATE OF content_html,draft_html ON dms.document_content
FOR EACH ROW EXECUTE FUNCTION dms.trg_revision_content_guard();

DO $patch$
DECLARE body text;old_text text;
BEGIN
 SELECT pg_get_functiondef(oid) INTO STRICT body FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_document_access';
 IF position('fn_revision_authorized' IN body)=0 THEN
  old_text:='IF NOT d.is_editable THEN can_author := false; can_upload := false; can_edit := false; END IF;';
  IF position(old_text IN body)=0 THEN RAISE EXCEPTION 'Unexpected access function'; END IF;
  EXECUTE replace(body,old_text,'IF NOT d.is_editable OR dms.fn_revision_authorized(d.current_version_id) THEN can_author := false; can_upload := false; can_edit := false; END IF;');
 END IF;
 SELECT pg_get_functiondef(oid) INTO STRICT body FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_rendition_set_base';
 IF position('conversion was running' IN body)=0 THEN
  body:=replace(body,'BEGIN',E'BEGIN\n    PERFORM 1 FROM dms.document_versions WHERE id=p_version_id FOR UPDATE;\n    IF NOT EXISTS(SELECT 1 FROM dms.document_versions WHERE id=p_version_id AND source_sha256 IS NOT DISTINCT FROM p_source_sha256) THEN RAISE EXCEPTION ''Source changed while conversion was running; stale PDF refused.'' USING ERRCODE=''45001''; END IF;');
  EXECUTE body;
 END IF;
 SELECT pg_get_functiondef(oid) INTO STRICT body FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_rendition_complete';
 IF position('Stale rendition refused' IN body)=0 THEN
  old_text:='SELECT * INTO v FROM document_versions WHERE id = j.document_version_id;';
  IF position(old_text IN body)=0 THEN RAISE EXCEPTION 'Unexpected rendition completion function'; END IF;
  EXECUTE replace(body,old_text,old_text||E'\n    IF NOT fn_rendition_base_ready(v.id) OR j.state_code IS DISTINCT FROM (SELECT code FROM dms.document_states WHERE id=v.state_id) THEN RAISE EXCEPTION ''Stale rendition refused; source or workflow changed.'' USING ERRCODE=''45001''; END IF;');
 END IF;
 -- Running work must not be queued again on every readiness poll.
 SELECT pg_get_functiondef(oid) INTO STRICT body FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='fn_rendition_enqueue';
 IF position('Reuse the running conversion' IN body)=0 THEN
  body:=replace(body,'BEGIN',E'BEGIN\n    -- Reuse the running conversion for the same state.\n    SELECT id INTO v_id FROM dms.document_rendition_jobs WHERE document_version_id=p_version_id AND state_code=p_state_code AND status=''RUNNING'' ORDER BY id DESC LIMIT 1;\n    IF v_id IS NOT NULL THEN RETURN v_id; END IF;');
  EXECUTE body;
 END IF;
 -- Render before review/approval, rather than discovering conversion failure at
 -- the final approval. Readiness is mandatory for every rendition-bearing state.
 SELECT pg_get_functiondef(oid) INTO STRICT body FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_rendition_gate';
 body:=replace(body,'IF lower(fn_config_text(''rendition.requireBeforePublication'', ''true'')) <> ''true'' THEN RETURN NULL; END IF;','');
 body:=replace(body,'IF NOT st.requires_rendition THEN RETURN NULL; END IF;','IF NOT st.requires_rendition AND upper(st.code) NOT IN (''UNDER_REVIEW'',''UNDER_APPROVAL'') THEN RETURN NULL; END IF;');
 EXECUTE body;
 SELECT pg_get_functiondef(oid) INTO STRICT body FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_workflow_execute';
 body:=replace(body,'IF st_to.requires_rendition AND lower(fn_config_text(''rendition.requireBeforePublication'', ''true'')) = ''true''','IF (st_to.requires_rendition OR upper(st_to.code) IN (''UNDER_REVIEW'',''UNDER_APPROVAL''))');
 IF position('Open the linked QAMS reference' IN body)=0 THEN
  body:=replace(body,'linked change control must be verified and active before publication.','linked change control must be verified and active before publication. Open the linked QAMS reference in Change Control Register; complete independent Verify and Approve signatures, or sync its verified status from QAMS.');
 END IF;
 body:=regexp_replace(body,'( Open the linked QAMS reference in Change Control Register; complete independent Verify and Approve signatures, or sync its verified status from QAMS\.)+',' Open the linked QAMS reference in Change Control Register; complete independent Verify and Approve signatures, or sync its verified status from QAMS.','g');
 EXECUTE body;
 SELECT pg_get_functiondef(oid) INTO STRICT body FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_workflow_available_actions';
 body:=replace(body,'requires_signature := t.requires_signature;','requires_signature := t.requires_signature OR upper(t.action_code) IN (''PUBLISH'',''MAKE_EFFECTIVE'');');
 EXECUTE body;
 SELECT pg_get_functiondef(oid) INTO STRICT body FROM pg_proc WHERE pronamespace='dms'::regnamespace AND proname='sp_document_set_source_from_editor';
 IF position('Queue saved source' IN body)=0 THEN
  body:=replace(body,'    RETURN 1;',E'    -- Queue saved source before the next review/approval request.\n    PERFORM fn_rendition_enqueue(v_doc,p_version_id,(SELECT s.code FROM dms.documents d JOIN dms.document_states s ON s.id=d.current_state_id WHERE d.id=v_doc),p_actor);\n    RETURN 1;');
  EXECUTE body;
 END IF;
END $patch$;
COMMIT;
