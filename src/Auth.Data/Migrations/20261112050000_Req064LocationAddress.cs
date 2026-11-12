// Address columns on auth.location. Requiredness is CountryAddress, not the column.
using Auth.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261112050000_Req064LocationAddress")]
    public partial class Req064LocationAddress : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064LocationAddress.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1");

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064LocationAddress).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
