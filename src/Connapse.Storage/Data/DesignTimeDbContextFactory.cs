using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Connapse.Storage.Data;

/// <summary>
/// Used by EF Core tooling (dotnet ef migrations) to create the DbContext at design time.
/// Usage: dotnet ef migrations add InitialCreate --project src/Connapse.Storage
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KnowledgeDbContext>
{
    public KnowledgeDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<KnowledgeDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=aikp;Username=aikp;Password=aikp_dev",
            npgsql => npgsql.UseVector());

        return new KnowledgeDbContext(optionsBuilder.Options);
    }
}
