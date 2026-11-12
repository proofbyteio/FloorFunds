// REQ-064 H-A. Partner ids come only from partner_admin rows at that partner.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110200000_Req064RoleScope")]
    public partial class Req064RoleScope : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064RoleScope.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            -- Down keeps the function. The previous body is restored by reverting this migration's SQL.
            SELECT 1;
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064RoleScope).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
