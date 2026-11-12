// session_subject returns the sign-in identifier current_principal's session already stores.
using Auth.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261112060000_Req064SessionSubject")]
    public partial class Req064SessionSubject : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064SessionSubject.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP FUNCTION IF EXISTS auth.session_subject()");

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064SessionSubject).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
