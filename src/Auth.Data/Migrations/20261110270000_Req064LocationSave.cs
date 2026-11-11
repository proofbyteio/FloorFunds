// SA L2. The location login-config write is a definer function. admin_api has no direct UPDATE.
using Auth.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110270000_Req064LocationSave")]
    public partial class Req064LocationSave : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064LocationSave.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS auth.save_location_login_config(uuid, bigint, text, text, uuid[], text, text, boolean, text, boolean, boolean, uuid, timestamptz);
            DROP POLICY IF EXISTS llc_definer ON auth.location_login_config;
            DROP POLICY IF EXISTS llc_definer_read ON auth.location_login_config;
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064LocationSave).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
