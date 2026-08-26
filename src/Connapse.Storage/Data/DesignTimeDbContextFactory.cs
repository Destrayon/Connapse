using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Connapse.Storage.Data;

/// <summary>
/// Builds the DbContext for the EF Core tooling — <c>dotnet ef migrations</c>, <c>database update</c>.
/// </summary>
/// <remarks>
/// Only <c>migrations add</c> works without a database; everything else — <c>remove</c>,
/// <c>update</c>, <c>list</c> — connects. This pointed at <c>aikp</c>, the database name from before
/// the project was renamed, so those commands failed with an authentication error that read as a
/// local misconfiguration rather than a wrong constant, and migrations got fixed up by hand instead.
/// <para>
/// Read from the environment, so it follows the same compose file everything else does rather than
/// being a second place the credentials are written down. The default matches
/// <c>docker-compose.yml</c> for the common case of the dev stack being up.
/// </para>
/// </remarks>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KnowledgeDbContext>
{
    /// <summary>Matches docker-compose.yml, with the port the dev override publishes.</summary>
    private const string DevConnection =
        "Host=localhost;Database=connapse;Username=connapse;Password=connapse_dev";

    public KnowledgeDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<KnowledgeDbContext>();

        // The same variable Program.cs takes, so pointing the tooling at another database is
        // setting the one thing that already means that.
        string connection =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
            ?? DevConnection;

        optionsBuilder.UseNpgsql(connection, npgsql => npgsql.UseVector());

        return new KnowledgeDbContext(optionsBuilder.Options);
    }
}
