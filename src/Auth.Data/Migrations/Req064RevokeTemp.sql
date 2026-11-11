-- SA L3. A database created outside db/kit/database.sql still loses PUBLIC TEMP.
-- No statement in this repo creates a temporary table, so the privilege is not granted back.
DO $temp$
BEGIN
  EXECUTE format('REVOKE TEMP ON DATABASE %I FROM PUBLIC', current_database());
END
$temp$;
