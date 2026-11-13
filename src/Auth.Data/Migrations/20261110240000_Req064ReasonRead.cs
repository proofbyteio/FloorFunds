// REQ-064 M-B. A reason-bounds read sees only the partner the actor is allowed.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110240000_Req064ReasonRead")]
    public partial class Req064ReasonRead : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064ReasonRead.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS auth.save_partner_bounds(uuid, bigint, integer, integer, uuid, timestamptz);
            DROP FUNCTION IF EXISTS auth.read_partner_bounds(uuid);
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064ReasonRead).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
