// Hand-written EF migration (TLD 2026-11-04 7:56 option b; Slice A, TLD 2026-11-05 08:00 / 08:00b). Partner-level reason
// bounds for screen 9 (Confirmed DEC-109..117; LD fad73cb3 L207-225): one row per partner that has set its own bounds; a partner
// with no row uses the deployment config LoginConfig:ReasonMinLength / ReasonMaxLength (TLD 08:00 Q3). Stored rows are hard
// 10 <= min <= max <= 500 (DEC-114; = llcc_reason, which is NOT widened). row_version is the If-Match / ETag version
// (TLD 08:00 Q2: first PUT inserts v1, each later PUT bumps it).
// RLS: ENABLE + FORCE; the owner policy as on every RLS table; the admin_api policy admits a Super Admin or ANY Partner Admin
// for every partner row: the cross-partner write is DEC-111 (the only cross-partner Partner Admin exception) and the
// cross-partner read is TO CONFIRM LS-OQ-61. A missing / unknown app.admin_type, or partner without app.partner_id, sees and
// writes nothing (fail closed, as the other tables). The partner id is not a FK: S2a has no partner table.
// Grants: admin_api SELECT, INSERT, and column UPDATE on the mutable columns only (never partner_id); no DELETE; login none.
// Valid inside the idempotent script's DO $EF$ wrapper (db/migrations.sql).
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261105080000_S2aPartnerReasonBounds")]
    public partial class S2aPartnerReasonBounds : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE auth.partner_reason_bounds (
                  partner_id        uuid        NOT NULL,
                  reason_min_length integer     NOT NULL,
                  reason_max_length integer     NOT NULL,
                  updated_by        uuid        NOT NULL,
                  updated_at        timestamptz NOT NULL,
                  row_version       bigint      NOT NULL DEFAULT 1,
                  CONSTRAINT partner_reason_bounds_pkey PRIMARY KEY (partner_id),
                  CONSTRAINT prb_bounds CHECK (reason_min_length >= 10 AND reason_min_length <= reason_max_length AND reason_max_length <= 500),
                  CONSTRAINT prb_row_version CHECK (row_version >= 1)
                );
                ALTER TABLE auth.partner_reason_bounds ENABLE ROW LEVEL SECURITY;
                ALTER TABLE auth.partner_reason_bounds FORCE ROW LEVEL SECURITY;
                CREATE POLICY prb_owner ON auth.partner_reason_bounds FOR ALL TO CURRENT_USER USING (true) WITH CHECK (true);

                DO $s2a_prb$
                DECLARE
                  a text := current_database() || '_admin_api';
                  any_admin text := $p$(current_setting('app.admin_type', true) = 'super' OR (current_setting('app.admin_type', true) = 'partner' AND nullif(current_setting('app.partner_id', true), '') IS NOT NULL))$p$;
                BEGIN
                  EXECUTE format('CREATE POLICY prb_admin ON auth.partner_reason_bounds FOR ALL TO %I USING %s WITH CHECK %s', a, any_admin, any_admin);
                  EXECUTE format('GRANT SELECT, INSERT ON auth.partner_reason_bounds TO %I', a);
                  EXECUTE format('GRANT UPDATE (reason_min_length, reason_max_length, updated_by, updated_at, row_version) ON auth.partner_reason_bounds TO %I', a);
                END $s2a_prb$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to the S2aLlcColumnUpdate state: the table (its policies and grants go with it).
            migrationBuilder.Sql("DROP TABLE auth.partner_reason_bounds;");
        }
    }
}
