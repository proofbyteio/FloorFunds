// REQ-064 H-B. Policies read the request principal, not a setting the app role can forge.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110230000_Req064Principal")]
    public partial class Req064Principal : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064Principal.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS auth.actor_sees_change(uuid, text, uuid, uuid);
            DROP FUNCTION IF EXISTS auth.actor_can_location(uuid, uuid);
            DROP FUNCTION IF EXISTS auth.actor_has_location(uuid);
            DROP FUNCTION IF EXISTS auth.actor_location_sees_partner(uuid);
            DROP FUNCTION IF EXISTS auth.actor_can_partner(uuid);
            DROP FUNCTION IF EXISTS auth.actor_is_any_admin();
            DROP FUNCTION IF EXISTS auth.actor_has_any_location();
            DROP FUNCTION IF EXISTS auth.actor_is_global();
            DROP FUNCTION IF EXISTS auth._request_user();
            DROP FUNCTION IF EXISTS auth.record_login_write(text, uuid);
            DROP TABLE IF EXISTS auth.login_write_principal;
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064Principal).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
