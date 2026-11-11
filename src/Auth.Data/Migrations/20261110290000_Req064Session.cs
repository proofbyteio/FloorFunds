// DEC-213. Session-bound request principal. The raw token is not stored.
using Auth.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110290000_Req064Session")]
    public partial class Req064Session : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064Session.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS app_user_revoke_sessions ON auth.app_user;
            DROP TRIGGER IF EXISTS admin_assignment_revoke_sessions ON auth.admin_assignment;
            DROP FUNCTION IF EXISTS auth.revoke_on_principal_change();
            DROP FUNCTION IF EXISTS auth.revoke_session(text);
            DROP FUNCTION IF EXISTS auth.session_issuer();
            DROP FUNCTION IF EXISTS auth.current_principal();
            DROP FUNCTION IF EXISTS auth.begin_request(text);
            DROP FUNCTION IF EXISTS auth.open_session(text, text, uuid, timestamptz);
            DROP TABLE IF EXISTS auth.request_binding;
            DROP TABLE IF EXISTS auth.request_session;
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064Session).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
