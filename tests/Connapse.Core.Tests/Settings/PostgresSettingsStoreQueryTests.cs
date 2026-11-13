using System.Data.Common;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Connapse.Core.Tests.Settings;

/// <summary>
/// #1201: a settings read logged EF's "First/FirstOrDefault without OrderBy and filter" warning, because EF cannot see
/// the WHERE inside raw SQL. The warning is raised while the query compiles, before any connection opens, so a server
/// that refuses the connection is enough to show whether it fires.
/// </summary>
[Trait("Category", "Unit")]
public class PostgresSettingsStoreQueryTests
{
    [Fact]
    public async Task GetWithRevisionAsync_CompilesWithoutTheFirstWithoutOrderByWarning()
    {
        var options = new DbContextOptionsBuilder<KnowledgeDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Timeout=1;Pooling=false", npgsql => npgsql.UseVector())
            .ConfigureWarnings(w => w.Throw(CoreEventId.FirstWithoutOrderByAndFilterWarning))
            .Options;
        await using var context = new KnowledgeDbContext(options);
        var store = new PostgresSettingsStore(context, Substitute.For<ISettingsReloader>());

        Func<Task> read = () => store.GetWithRevisionAsync<object>("embedding");

        // Reaching the database (and failing to connect) means the query compiled without the warning, which throws bare.
        (await read.Should().ThrowAsync<InvalidOperationException>()).WithInnerException<DbException>();
    }
}
