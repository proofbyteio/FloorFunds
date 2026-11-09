// DEC-161 catalog foundation. SQL only. No EF table builder, no DbSet, no snapshot edit.
// Chain: frozen Rev3 + DEC186/190/security, REV4 packet a0f2ef39, corrections 9ad9e1cc,
// consolidated note f7b737cb. Marker rows are inserted by apply_entry_create and
// apply_claim_edit. The statement trigger does not insert the marker.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261108190000_CatalogMasterData")]
    public partial class CatalogMasterData : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            Read("CatalogMasterData.Up.sql")
            + Read("CatalogMasterData.Functions.sql")
            + Read("CatalogMasterData.Behavior.sql")
            + Read("CatalogMasterData.Triggers.sql")
            + Read("CatalogMasterData.Grants.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("CatalogMasterData.Down.sql"));

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(CatalogMasterData).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
