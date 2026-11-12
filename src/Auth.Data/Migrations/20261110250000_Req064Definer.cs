// REQ-064. Grants and policies for the NOLOGIN function owner. Ownership itself is a superuser step.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110250000_Req064Definer")]
    public partial class Req064Definer : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064Definer.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            DROP POLICY IF EXISTS admin_assignment_definer ON auth.admin_assignment;
            DROP POLICY IF EXISTS location_definer ON auth.location;
            DROP POLICY IF EXISTS prb_definer ON auth.partner_reason_bounds;
            DROP POLICY IF EXISTS registered_entry_definer ON catalog.registered_entry;
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064Definer).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
