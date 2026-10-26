using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Reindex;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-99: when a container's storage could not be reached, reindexing fell back to the legacy
/// local file system, found nothing there, and reported the file as missing. An outage looked like
/// lost data. It is now reported as an error that names the storage.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ReindexStorageOutageTests(SharedWebAppFixture fixture)
{
    private const string Outage = "Connection refused (minio:9000)";

    private static async Task<(Guid containerId, Guid documentId)> SeedAsync(IServiceProvider sp)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            ContainerId = container.Id,
            FileName = "notes.txt",
            Path = "/notes.txt",
            ContentHash = Guid.NewGuid().ToString("N"),
            IngestionStatus = DocumentStatus.Ready,
            LastIndexedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            Metadata = [],
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return (container.Id, document.Id);
    }

    private static ReindexService Reindexer(IServiceProvider sp, KnowledgeDbContext ctx,
        IManagedStorageProvider storage, IKnowledgeFileSystem fileSystem) =>
        new(ctx,
            fileSystem,
            storage,
            sp.GetRequiredService<IContainerStore>(),
            new RecordingIngestionQueue(),
            sp.GetRequiredService<IOptionsMonitor<ChunkingSettings>>(),
            sp.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>(),
            NullLogger<ReindexService>.Instance);

    private static IManagedStorageProvider UnreachableStorage(Action<IConnector> fail)
    {
        var connector = Substitute.For<IConnector>();
        fail(connector);
        var storage = Substitute.For<IManagedStorageProvider>();
        storage.CreateConnector(Arg.Any<string>()).Returns(connector);
        return storage;
    }

    public static TheoryData<string> Calls => new() { "exists", "read" };

    private static IManagedStorageProvider StorageFailingOn(string call) => UnreachableStorage(c =>
    {
        if (call == "exists")
        {
            c.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException(Outage));
        }
        else
        {
            c.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
            c.ReadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException(Outage));
        }
    });

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Reindex_StorageUnreachable_FailsNamingTheStorage(string call)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var (containerId, documentId) = await SeedAsync(sp);
        var fileSystem = Substitute.For<IKnowledgeFileSystem>();

        await using var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var result = await Reindexer(sp, ctx, StorageFailingOn(call), fileSystem)
            .ReindexAsync(new ReindexOptions { ContainerId = containerId.ToString() });

        var doc = result.Documents.Should().ContainSingle().Subject;
        doc.DocumentId.Should().Be(documentId.ToString());
        doc.Action.Should().Be(ReindexAction.Failed);
        doc.Reason.Should().Be(ReindexReason.StorageUnavailable);
        doc.ErrorMessage.Should().Contain("storage").And.Contain(containerId.ToString()).And.Contain(Outage);
        result.FailedCount.Should().Be(1);
        result.ReasonCounts.Should().NotContainKey(ReindexReason.FileNotFound);
        fileSystem.ReceivedCalls().Should().BeEmpty("an outage must not fall back to the old local path");
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task CheckDocument_StorageUnreachable_ReportsTheStorageError(string call)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var (containerId, documentId) = await SeedAsync(sp);
        var fileSystem = Substitute.For<IKnowledgeFileSystem>();

        await using var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var check = await Reindexer(sp, ctx, StorageFailingOn(call), fileSystem)
            .CheckDocumentAsync(documentId.ToString());

        check.NeedsReindex.Should().BeFalse();
        check.Reason.Should().Be(ReindexReason.StorageUnavailable);
        check.ErrorMessage.Should().Contain("storage").And.Contain(containerId.ToString()).And.Contain(Outage);
        fileSystem.ReceivedCalls().Should().BeEmpty("an outage must not fall back to the old local path");
    }

    [Fact]
    public async Task Reindex_StorageReachableAndFileGone_StillReportsFileNotFound()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var (containerId, _) = await SeedAsync(sp);
        var storage = UnreachableStorage(c =>
            c.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false));

        await using var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var result = await Reindexer(sp, ctx, storage, Substitute.For<IKnowledgeFileSystem>())
            .ReindexAsync(new ReindexOptions { ContainerId = containerId.ToString() });

        var doc = result.Documents.Should().ContainSingle().Subject;
        doc.Action.Should().Be(ReindexAction.Skipped);
        doc.Reason.Should().Be(ReindexReason.FileNotFound);
    }
}
