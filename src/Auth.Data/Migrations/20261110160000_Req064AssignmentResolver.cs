// REQ-064. SQL only. The assignment table is not an EF entity and is not in the snapshot.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Data.Migrations
{
    [DbContext(typeof(AuthDbContext))]
    [Migration("20261110160000_Req064AssignmentResolver")]
    public partial class Req064AssignmentResolver : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Read("Req064Assignment.sql"));

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS app_user_last_sa_truncate ON auth.app_user;
            DROP TRIGGER IF EXISTS admin_assignment_last_sa_truncate ON auth.admin_assignment;
            DROP TRIGGER IF EXISTS app_user_last_sa ON auth.app_user;
            DROP TRIGGER IF EXISTS admin_assignment_last_sa ON auth.admin_assignment;
            DROP FUNCTION IF EXISTS auth.resolve_grant(text, uuid);
            DROP FUNCTION IF EXISTS auth._last_sa_truncate_guard();
            DROP FUNCTION IF EXISTS auth._last_sa_guard();
            DROP SEQUENCE IF EXISTS auth.resolver_read_seq;
            DROP TABLE IF EXISTS auth.admin_assignment;
            """);

        static string Read(string name)
        {
            var full = "Auth.Data.Migrations." + name;
            using var stream = typeof(Req064AssignmentResolver).Assembly.GetManifestResourceStream(full)
                ?? throw new InvalidOperationException("missing embedded resource " + full);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
