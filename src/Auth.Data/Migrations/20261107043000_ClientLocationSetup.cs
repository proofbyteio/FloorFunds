// Hand-written EF migration (TLD 2026-11-04 7:56 option b). DEC-145/146, 2026-11-07.
// Adds auth.client and nullable setup columns on auth.location in the existing Transaction DB.
// A legacy location leaves every new column null. A new location create fills the required ones
// and points at a client in the same partner. Down drops those objects and does not use CASCADE.
// It does not edit 20261106120000_RefCountryTimeZone. db/migrations.sql is the runner's script.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261107043000_ClientLocationSetup")]
    public partial class ClientLocationSetup : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE auth.client (
                  client_id uuid NOT NULL,
                  partner_id uuid NOT NULL,
                  name text NOT NULL,
                  address1 text NOT NULL,
                  address2 text,
                  city text NOT NULL,
                  region text,
                  country_code character(2) NOT NULL,
                  postal_code text,
                  region_unused_confirmed boolean NOT NULL,
                  postal_unused_confirmed boolean NOT NULL,
                  phone_e164 text NOT NULL,
                  email text,
                  time_zone_id text NOT NULL,
                  created_by uuid NOT NULL,
                  created_at timestamp with time zone NOT NULL,
                  CONSTRAINT client_pkey PRIMARY KEY (client_id),
                  CONSTRAINT client_name_length CHECK (char_length(name) BETWEEN 1 AND 200),
                  CONSTRAINT client_address1_length CHECK (char_length(address1) BETWEEN 1 AND 200),
                  CONSTRAINT client_address2_length CHECK (address2 IS NULL OR char_length(address2) BETWEEN 1 AND 200),
                  CONSTRAINT client_city_length CHECK (char_length(city) BETWEEN 1 AND 100),
                  CONSTRAINT client_region_length CHECK (region IS NULL OR char_length(region) BETWEEN 1 AND 100),
                  CONSTRAINT client_postal_length CHECK (postal_code IS NULL OR char_length(postal_code) BETWEEN 1 AND 32),
                  CONSTRAINT client_phone_e164 CHECK (phone_e164 ~ '^\+[1-9][0-9]{1,14}$'),
                  CONSTRAINT client_email_shape CHECK (email IS NULL OR (char_length(email) BETWEEN 3 AND 254 AND email ~ '^[^[:space:]@]+@[^[:space:]@]+$')),
                  CONSTRAINT client_zone_length CHECK (char_length(time_zone_id) BETWEEN 1 AND 64),
                  CONSTRAINT client_country_fkey FOREIGN KEY (country_code) REFERENCES ref.country (country_code),
                  CONSTRAINT client_zone_fkey FOREIGN KEY (time_zone_id) REFERENCES ref.time_zone (time_zone_id)
                );
                CREATE INDEX client_partner_ix ON auth.client (partner_id);

                ALTER TABLE auth.location
                  ADD COLUMN client_id uuid,
                  ADD COLUMN address1 text,
                  ADD COLUMN address2 text,
                  ADD COLUMN city text,
                  ADD COLUMN region text,
                  ADD COLUMN country_code character(2),
                  ADD COLUMN postal_code text,
                  ADD COLUMN region_unused_confirmed boolean,
                  ADD COLUMN postal_unused_confirmed boolean,
                  ADD COLUMN phone_e164 text,
                  ADD COLUMN email text,
                  ADD COLUMN time_zone_id text,
                  ADD CONSTRAINT location_address1_length CHECK (address1 IS NULL OR char_length(address1) BETWEEN 1 AND 200),
                  ADD CONSTRAINT location_address2_length CHECK (address2 IS NULL OR char_length(address2) BETWEEN 1 AND 200),
                  ADD CONSTRAINT location_city_length CHECK (city IS NULL OR char_length(city) BETWEEN 1 AND 100),
                  ADD CONSTRAINT location_region_length CHECK (region IS NULL OR char_length(region) BETWEEN 1 AND 100),
                  ADD CONSTRAINT location_postal_length CHECK (postal_code IS NULL OR char_length(postal_code) BETWEEN 1 AND 32),
                  ADD CONSTRAINT location_phone_e164 CHECK (phone_e164 IS NULL OR phone_e164 ~ '^\+[1-9][0-9]{1,14}$'),
                  ADD CONSTRAINT location_email_shape CHECK (email IS NULL OR (char_length(email) BETWEEN 3 AND 254 AND email ~ '^[^[:space:]@]+@[^[:space:]@]+$')),
                  ADD CONSTRAINT location_zone_length CHECK (time_zone_id IS NULL OR char_length(time_zone_id) BETWEEN 1 AND 64),
                  ADD CONSTRAINT location_setup_pair CHECK (
                    (country_code IS NULL AND client_id IS NULL AND address1 IS NULL AND address2 IS NULL AND city IS NULL
                      AND region IS NULL AND postal_code IS NULL AND region_unused_confirmed IS NULL AND postal_unused_confirmed IS NULL
                      AND phone_e164 IS NULL AND email IS NULL AND time_zone_id IS NULL)
                    OR
                    (country_code IS NOT NULL AND client_id IS NOT NULL AND address1 IS NOT NULL AND city IS NOT NULL
                      AND phone_e164 IS NOT NULL AND time_zone_id IS NOT NULL
                      AND region_unused_confirmed IS NOT NULL AND postal_unused_confirmed IS NOT NULL)
                  ),
                  ADD CONSTRAINT location_client_fkey FOREIGN KEY (client_id) REFERENCES auth.client (client_id) ON DELETE RESTRICT,
                  ADD CONSTRAINT location_country_fkey FOREIGN KEY (country_code) REFERENCES ref.country (country_code),
                  ADD CONSTRAINT location_zone_fkey FOREIGN KEY (time_zone_id) REFERENCES ref.time_zone (time_zone_id);
                CREATE INDEX location_client_ix ON auth.location (client_id);

                CREATE FUNCTION auth.enforce_setup() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $fn$
                DECLARE
                  fr boolean;
                  fp boolean;
                  retired boolean;
                BEGIN
                  IF TG_OP = 'UPDATE'
                     AND NEW.client_id IS NOT DISTINCT FROM OLD.client_id
                     AND NEW.address1 IS NOT DISTINCT FROM OLD.address1
                     AND NEW.address2 IS NOT DISTINCT FROM OLD.address2
                     AND NEW.city IS NOT DISTINCT FROM OLD.city
                     AND NEW.region IS NOT DISTINCT FROM OLD.region
                     AND NEW.country_code IS NOT DISTINCT FROM OLD.country_code
                     AND NEW.postal_code IS NOT DISTINCT FROM OLD.postal_code
                     AND NEW.region_unused_confirmed IS NOT DISTINCT FROM OLD.region_unused_confirmed
                     AND NEW.postal_unused_confirmed IS NOT DISTINCT FROM OLD.postal_unused_confirmed
                     AND NEW.phone_e164 IS NOT DISTINCT FROM OLD.phone_e164
                     AND NEW.email IS NOT DISTINCT FROM OLD.email
                     AND NEW.time_zone_id IS NOT DISTINCT FROM OLD.time_zone_id
                  THEN
                    RETURN NEW;
                  END IF;

                  IF TG_TABLE_NAME = 'location' AND NEW.country_code IS NULL THEN
                    IF NEW.client_id IS NOT NULL OR NEW.address1 IS NOT NULL OR NEW.address2 IS NOT NULL
                       OR NEW.city IS NOT NULL OR NEW.region IS NOT NULL OR NEW.postal_code IS NOT NULL
                       OR NEW.region_unused_confirmed IS NOT NULL OR NEW.postal_unused_confirmed IS NOT NULL
                       OR NEW.phone_e164 IS NOT NULL OR NEW.email IS NOT NULL OR NEW.time_zone_id IS NOT NULL
                    THEN
                      RAISE EXCEPTION 'legacy location setup columns must all be null' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                  END IF;

                  IF NEW.country_code IS NULL OR NEW.address1 IS NULL OR NEW.city IS NULL
                     OR NEW.phone_e164 IS NULL OR NEW.time_zone_id IS NULL
                     OR NEW.region_unused_confirmed IS NULL OR NEW.postal_unused_confirmed IS NULL
                     OR (TG_TABLE_NAME = 'location' AND NEW.client_id IS NULL)
                  THEN
                    RAISE EXCEPTION 'setup required columns are null' USING ERRCODE = '23514';
                  END IF;

                  SELECT c.fmt_has_region, c.fmt_has_postal, c.retired_release_id IS NOT NULL
                    INTO fr, fp, retired
                    FROM ref.country c
                   WHERE c.country_code = NEW.country_code;
                  IF NOT FOUND OR retired THEN
                    RAISE EXCEPTION 'country is missing or retired' USING ERRCODE = '23514';
                  END IF;

                  IF NOT EXISTS (
                    SELECT 1 FROM ref.time_zone z
                     WHERE z.time_zone_id = NEW.time_zone_id AND z.retired_release_id IS NULL
                  ) THEN
                    RAISE EXCEPTION 'time zone is missing or retired' USING ERRCODE = '23514';
                  END IF;

                  IF fr IS TRUE THEN
                    IF NEW.region IS NULL OR NEW.region_unused_confirmed THEN
                      RAISE EXCEPTION 'region is required' USING ERRCODE = '23514';
                    END IF;
                  ELSIF fr IS FALSE THEN
                    IF NEW.region IS NOT NULL OR NEW.region_unused_confirmed THEN
                      RAISE EXCEPTION 'region is not used' USING ERRCODE = '23514';
                    END IF;
                  ELSE
                    IF NEW.region IS NULL AND NOT NEW.region_unused_confirmed THEN
                      RAISE EXCEPTION 'region confirmation required' USING ERRCODE = '23514';
                    END IF;
                    IF NEW.region IS NOT NULL AND NEW.region_unused_confirmed THEN
                      RAISE EXCEPTION 'region confirmation not applicable' USING ERRCODE = '23514';
                    END IF;
                  END IF;

                  IF fp IS TRUE THEN
                    IF NEW.postal_code IS NULL OR NEW.postal_unused_confirmed THEN
                      RAISE EXCEPTION 'postal is required' USING ERRCODE = '23514';
                    END IF;
                  ELSIF fp IS FALSE THEN
                    IF NEW.postal_code IS NOT NULL OR NEW.postal_unused_confirmed THEN
                      RAISE EXCEPTION 'postal is not used' USING ERRCODE = '23514';
                    END IF;
                  ELSE
                    IF NEW.postal_code IS NULL AND NOT NEW.postal_unused_confirmed THEN
                      RAISE EXCEPTION 'postal confirmation required' USING ERRCODE = '23514';
                    END IF;
                    IF NEW.postal_code IS NOT NULL AND NEW.postal_unused_confirmed THEN
                      RAISE EXCEPTION 'postal confirmation not applicable' USING ERRCODE = '23514';
                    END IF;
                  END IF;

                  RETURN NEW;
                END
                $fn$;
                REVOKE ALL ON FUNCTION auth.enforce_setup() FROM PUBLIC;

                CREATE TRIGGER client_setup_bi BEFORE INSERT OR UPDATE ON auth.client
                  FOR EACH ROW EXECUTE FUNCTION auth.enforce_setup();
                CREATE TRIGGER location_setup_bi BEFORE INSERT OR UPDATE ON auth.location
                  FOR EACH ROW EXECUTE FUNCTION auth.enforce_setup();

                ALTER TABLE auth.client ENABLE ROW LEVEL SECURITY;
                ALTER TABLE auth.client FORCE ROW LEVEL SECURITY;
                CREATE POLICY client_owner ON auth.client FOR ALL TO CURRENT_USER USING (true) WITH CHECK (true);

                DO $setup$
                DECLARE
                  a text := current_database() || '_admin_api';
                  scope text := $p$(current_setting('app.admin_type', true) = 'super' OR (current_setting('app.admin_type', true) = 'partner' AND partner_id = nullif(current_setting('app.partner_id', true), '')::uuid))$p$;
                BEGIN
                  EXECUTE format('CREATE POLICY client_admin ON auth.client FOR ALL TO %I USING %s WITH CHECK %s', a, scope, scope);
                  EXECUTE format('GRANT SELECT, INSERT ON auth.client TO %I', a);
                  EXECUTE format('GRANT EXECUTE ON FUNCTION auth.enforce_setup() TO %I', a);
                END
                $setup$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS location_setup_bi ON auth.location;
                DROP TRIGGER IF EXISTS client_setup_bi ON auth.client;
                DROP FUNCTION IF EXISTS auth.enforce_setup();
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_setup_pair;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_client_fkey;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_country_fkey;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_zone_fkey;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_address1_length;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_address2_length;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_city_length;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_region_length;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_postal_length;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_phone_e164;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_email_shape;
                ALTER TABLE auth.location DROP CONSTRAINT IF EXISTS location_zone_length;
                DROP INDEX IF EXISTS auth.location_client_ix;
                ALTER TABLE auth.location
                  DROP COLUMN IF EXISTS client_id,
                  DROP COLUMN IF EXISTS address1,
                  DROP COLUMN IF EXISTS address2,
                  DROP COLUMN IF EXISTS city,
                  DROP COLUMN IF EXISTS region,
                  DROP COLUMN IF EXISTS country_code,
                  DROP COLUMN IF EXISTS postal_code,
                  DROP COLUMN IF EXISTS region_unused_confirmed,
                  DROP COLUMN IF EXISTS postal_unused_confirmed,
                  DROP COLUMN IF EXISTS phone_e164,
                  DROP COLUMN IF EXISTS email,
                  DROP COLUMN IF EXISTS time_zone_id;
                DROP TABLE IF EXISTS auth.client;
                """);
        }
    }
}
