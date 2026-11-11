// Sign-out revokes every live request session for the user. Issuer role only.
using Auth.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110300000_Req064RevokeLogin")]
    public partial class Req064RevokeLogin : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064RevokeLogin.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS auth.revoke_login(text, text);
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064RevokeLogin).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
