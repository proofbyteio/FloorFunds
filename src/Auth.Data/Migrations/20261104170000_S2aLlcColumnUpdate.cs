// Hand-written EF migration (TLD 2026-11-04 7:56 option b; TLD 8:20 P2-1 must-fix). SSE2 DB review of 7e0350b, P2-1:
// the admin API role had table-level UPDATE on auth.location_login_config, so it could rewrite the PK (location_id) and
// the immutable created_by / created_at. Replace it with column-level UPDATE on the mutable columns only (spec §8.4).
// Grants are not part of the EF model, so the model snapshot is unchanged by this migration.
// Valid inside the idempotent script's DO $EF$ wrapper (db/migrations.sql).
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261104170000_S2aLlcColumnUpdate")]
    public partial class S2aLlcColumnUpdate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // REVOKE at table level also removes any column-level UPDATE; then grant exactly the mutable columns.
            migrationBuilder.Sql("""
                DO $s2a_llc_upd$
                DECLARE a text := current_database() || '_admin_api';
                BEGIN
                  EXECUTE format('REVOKE UPDATE ON auth.location_login_config FROM %I', a);
                  EXECUTE format('GRANT UPDATE (provider_kind, display_name, entra_allowed_tenant_ids, oidc_authority, oidc_client_id, secret_ref, allow_db_fallback, mfa_required, updated_by, updated_at, row_version) ON auth.location_login_config TO %I', a);
                END $s2a_llc_upd$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to the S2aInitial state (table-level UPDATE).
            migrationBuilder.Sql("""
                DO $s2a_llc_upd_down$
                DECLARE a text := current_database() || '_admin_api';
                BEGIN
                  EXECUTE format('REVOKE UPDATE ON auth.location_login_config FROM %I', a);
                  EXECUTE format('GRANT UPDATE ON auth.location_login_config TO %I', a);
                END $s2a_llc_upd_down$;
                """);
        }
    }
}
