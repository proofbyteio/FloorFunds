using Microsoft.EntityFrameworkCore;

namespace Auth.Data;

public static class AuthDb
{
    /// <summary>Options for the owner (migrations) or an app role. Sensitive-data logging stays off (spec §9).</summary>
    public static DbContextOptions<AuthDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString)
            .Options;
}
