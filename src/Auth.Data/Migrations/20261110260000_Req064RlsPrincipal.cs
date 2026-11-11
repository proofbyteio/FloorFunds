// SA L1. RLS on the principal tables. Ownership of functions stays a superuser step.
using Auth.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110260000_Req064RlsPrincipal")]
    public partial class Req064RlsPrincipal : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064RlsPrincipal.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            DROP POLICY IF EXISTS user_issuer_owner ON auth.user_issuer;
            DROP POLICY IF EXISTS user_issuer_definer ON auth.user_issuer;
            DROP POLICY IF EXISTS login_write_principal_owner ON auth.login_write_principal;
            DROP POLICY IF EXISTS login_write_principal_definer ON auth.login_write_principal;
            ALTER TABLE auth.user_issuer DISABLE ROW LEVEL SECURITY;
            ALTER TABLE auth.login_write_principal DISABLE ROW LEVEL SECURITY;
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064RlsPrincipal).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
