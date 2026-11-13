-- Location address columns for the screen 05 form. Nullable so a row created
-- before an address was supplied still loads. Requiredness is the server
-- validator against ui/address/country-rules.json, not a column constraint.
ALTER TABLE auth.location ADD COLUMN IF NOT EXISTS address_line_1 text;
ALTER TABLE auth.location ADD COLUMN IF NOT EXISTS address_line_2 text;
ALTER TABLE auth.location ADD COLUMN IF NOT EXISTS city text;
ALTER TABLE auth.location ADD COLUMN IF NOT EXISTS country text;
ALTER TABLE auth.location ADD COLUMN IF NOT EXISTS region text;
ALTER TABLE auth.location ADD COLUMN IF NOT EXISTS postal_code text;
ALTER TABLE auth.location ADD COLUMN IF NOT EXISTS phone text;
ALTER TABLE auth.location ADD COLUMN IF NOT EXISTS email text;
ALTER TABLE auth.location ADD COLUMN IF NOT EXISTS time_zone text;
