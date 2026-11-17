BEGIN;
SET LOCAL lock_timeout = '15s';
SET LOCAL search_path = dms, public;
DO $$ BEGIN
    IF current_database() <> 'UnifiedPlatform_dms' THEN
        RAISE EXCEPTION 'Wrong database';
    END IF;
END $$;

-- days = 0 is reserved for API-managed view links: they remain valid until withdrawn.
-- The token hash has a unique index, so repeated requests return the same active link.
CREATE OR REPLACE FUNCTION dms.sp_share_create(
    p_document_id bigint,
    p_pin_current boolean,
    p_days integer,
    p_purpose text,
    p_note text,
    p_token_sha256 text,
    p_token_hint text,
    p_actor integer,
    p_ip text)
RETURNS bigint
LANGUAGE plpgsql
SET search_path = dms, public
AS $function$
DECLARE
    d record;
    st record;
    v_days integer;
    v_max integer;
    v_id bigint;
    v_expires timestamp;
    v_pin bigint;
    v_existing record;
    v_inserted boolean := false;
BEGIN
    IF NOT common.sp_dms_user_has_permission(p_actor, 'document.share.manage') THEN
        RAISE EXCEPTION 'FORBIDDEN: creating an external view link needs the permission "Manage external view links".'
            USING ERRCODE = '45004';
    END IF;
    IF lower(fn_config_text('share.link.enabled', 'true')) <> 'true' THEN
        RAISE EXCEPTION 'INVALID_STATE: external view links are switched off (share.link.enabled).'
            USING ERRCODE = '45003';
    END IF;

    SELECT * INTO d FROM documents WHERE id = p_document_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'NOT_FOUND: the document was not found.' USING ERRCODE = '45000';
    END IF;
    SELECT * INTO st FROM dms.document_states WHERE id = d.current_state_id;
    IF d.effective_version_id IS NULL OR NOT st.is_effective OR d.is_obsolete THEN
        RAISE EXCEPTION 'INVALID_STATE: only a document in force can be shared outside the system (this one is %).', st.name
            USING ERRCODE = '45003';
    END IF;
    IF length(coalesce(p_token_sha256, '')) <> 64 THEN
        RAISE EXCEPTION 'INVALID_INPUT: bad token.' USING ERRCODE = '45000';
    END IF;

    IF p_days = 0 THEN
        v_days := 0;
        v_expires := 'infinity'::timestamp;
    ELSE
        v_max := greatest(fn_config_int('share.link.maxDays', 1825), 1);
        v_days := coalesce(p_days, fn_config_int('share.link.defaultDays', 365));
        IF v_days < 1 OR v_days > v_max THEN
            RAISE EXCEPTION 'INVALID_INPUT: a link lasts between 1 and % days, or use 0 for no expiry.', v_max
                USING ERRCODE = '45000';
        END IF;
        v_expires := fn_utc_now() + make_interval(days => v_days);
    END IF;

    v_pin := CASE WHEN coalesce(p_pin_current, false) THEN d.effective_version_id END;
    INSERT INTO document_share_links
        (document_id, pinned_version_id, token_sha256, token_hint, purpose, note, expires_at, created_by)
    VALUES
        (d.id, v_pin, lower(p_token_sha256), left(p_token_hint, 16),
         left(coalesce(NULLIF(trim(p_purpose), ''), 'SAP'), 64),
         left(NULLIF(trim(p_note), ''), 512), v_expires, p_actor)
    ON CONFLICT (token_sha256) DO NOTHING
    RETURNING id INTO v_id;

    IF v_id IS NULL THEN
        SELECT * INTO v_existing FROM document_share_links
        WHERE token_sha256 = lower(p_token_sha256);
        IF NOT FOUND OR v_existing.document_id <> d.id OR v_existing.revoked_at IS NOT NULL OR
           v_existing.expires_at <= fn_utc_now() THEN
            RAISE EXCEPTION 'INVALID_STATE: this external view link is withdrawn or unavailable.'
                USING ERRCODE = '45003';
        END IF;
        RETURN v_existing.id;
    END IF;
    v_inserted := true;

    IF v_inserted THEN
        PERFORM sp_audit_write(
            'DOCUMENT_SHARE_LINK', v_id, d.document_number, 'CREATE',
            format('External view link #%s created (%s, %s%s)',
                v_id, coalesce(NULLIF(trim(p_purpose), ''), 'SAP'),
                CASE WHEN v_days = 0 THEN 'no expiry' ELSE v_days::text END,
                CASE WHEN v_pin IS NULL THEN ', follows the revision in force' ELSE ', pinned to this revision' END),
            p_actor, NULL,
            json_build_object('TokenHint', left(p_token_hint, 16), 'ExpiresAtUtc', v_expires,
                              'PinnedVersionId', v_pin, 'Purpose', p_purpose,
                              'Note', NULLIF(trim(p_note), '')),
            NULL, p_ip, d.id, d.effective_version_id);
    END IF;
    RETURN v_id;
END
$function$;

COMMIT;
