using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Connapse.Integration.Tests;

/// <summary>
/// Migration tests that stop the schema before a later migration still read facts through today's store, which selects
/// the check-by columns (CON-340). This adds just that migration on top of the stopped schema, and records it, so a
/// later full migrate skips it.
/// </summary>
internal static class MemoryCheckBySchema
{
    private const string Before = "20261107061822_RemoveFilesystemSources";
    private const string CheckBy = "20261112171314_MemoryFactCheckBy";

    public static async Task AddAsync(KnowledgeDbContext context)
    {
        string script = context.GetService<IMigrator>().GenerateScript(Before, CheckBy, MigrationsSqlGenerationOptions.Idempotent);
        await context.Database.ExecuteSqlRawAsync(script);
    }
}
