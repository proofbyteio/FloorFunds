// SA L3. PUBLIC loses TEMP on the database. No temporary tables are created.
using Auth.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110280000_Req064RevokeTemp")]
    public partial class Req064RevokeTemp : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064RevokeTemp.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            DO $temp$
            BEGIN
              EXECUTE format('GRANT TEMP ON DATABASE %I TO PUBLIC', current_database());
            END
            $temp$;
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064RevokeTemp).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
