BEGIN;
LOCK TABLE dms.system_config IN SHARE ROW EXCLUSIVE MODE;
CREATE TABLE IF NOT EXISTS dms.system_config_duplicate_backup AS
 SELECT *,now() AS archived_at FROM dms.system_config WITH NO DATA;
CREATE TEMP TABLE config_duplicates ON COMMIT DROP AS
 SELECT id FROM (SELECT id,row_number() OVER(
  PARTITION BY lower(btrim(config_key)) ORDER BY updated_at DESC NULLS LAST,id DESC) AS ordinal
  FROM dms.system_config) ranked WHERE ordinal>1;
INSERT INTO dms.system_config_duplicate_backup
 SELECT c.*,now() FROM dms.system_config c JOIN config_duplicates d USING(id);
DELETE FROM dms.system_config WHERE id IN(SELECT id FROM config_duplicates);
CREATE UNIQUE INDEX IF NOT EXISTS ux_system_config_key_normalized
 ON dms.system_config(lower(btrim(config_key)));
COMMIT;
