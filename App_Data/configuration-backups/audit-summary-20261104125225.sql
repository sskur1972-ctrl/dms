CREATE OR REPLACE FUNCTION dms.sp_audit_append(p_entity_type text, p_entity_id bigint, p_entity_key text, p_action text, p_action_detail text, p_document_id bigint, p_document_version_id bigint, p_actor_user_id bigint, p_actor_user_name text, p_actor_full_name text, p_actor_role_codes text, p_on_behalf_of_user_id bigint, p_event_at_local text, p_time_zone_id text, p_ip_address text, p_user_agent text, p_session_id text, p_correlation_id text, p_before_json json, p_after_json json, p_changed_fields text, p_reason text, p_signature_id bigint, p_is_gxp_critical boolean, OUT p_audit_id bigint)
 RETURNS bigint
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_prev_hash char(64);
    v_row_hash  char(64);
    v_now       timestamp(3) := date_trunc('milliseconds', clock_timestamp() AT TIME ZONE 'UTC');
BEGIN
 SELECT old_values,new_values INTO p_before_json,p_after_json FROM dms.fn_audit_changed_values(p_before_json,p_after_json);
 SELECT string_agg(key, ', ' ORDER BY key) INTO p_changed_fields FROM json_object_keys(p_after_json) AS fields(key);
    SELECT last_row_hash INTO v_prev_hash FROM audit_chain_head WHERE hash_shard = 0 FOR UPDATE;

    v_row_hash := fn_audit_row_hash(v_prev_hash, p_entity_type, p_entity_id, p_action,
                                    p_actor_user_name, v_now, p_before_json, p_after_json, p_reason);

    INSERT INTO audit_trail (
        entity_type, entity_id, entity_key, action, action_detail, document_id, document_version_id,
        actor_user_id, actor_user_name, actor_full_name, actor_role_codes, on_behalf_of_user_id,
        event_at_utc, event_at_local_display, time_zone_id, ip_address, user_agent, session_id, correlation_id,
        before_json, after_json, changed_fields, reason, signature_id, is_gxp_critical, prev_hash, row_hash, hash_shard)
    VALUES (
        p_entity_type, p_entity_id, p_entity_key, p_action, p_action_detail, p_document_id, p_document_version_id,
        p_actor_user_id, p_actor_user_name, p_actor_full_name, p_actor_role_codes, p_on_behalf_of_user_id,
        v_now, p_event_at_local, coalesce(p_time_zone_id, 'Asia/Kolkata'), p_ip_address, p_user_agent, p_session_id, p_correlation_id,
        p_before_json, p_after_json, p_changed_fields, p_reason, p_signature_id, coalesce(p_is_gxp_critical, TRUE),
        v_prev_hash, v_row_hash, 0)
    RETURNING id INTO p_audit_id;

    UPDATE audit_chain_head
       SET last_audit_id = p_audit_id, last_row_hash = v_row_hash, row_count = row_count + 1
     WHERE hash_shard = 0;
END $function$
;
