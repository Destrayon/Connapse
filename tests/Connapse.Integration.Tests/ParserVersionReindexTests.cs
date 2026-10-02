using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Parsers;
using Connapse.Ingestion.Pipeline;
using Connapse.Ingestion.Reindex;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// #596: a parser that now reads the same bytes differently leaves the content hash unchanged,
/// so only the recorded parser version can tell a reindex the chunks are stale.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ParserVersionReindexTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    private static async Task<Guid> SeedSourceAsync(IServiceProvider sp)
    {
        var connection = await sp.GetRequiredService<IConnectionStore>().CreateAsync(
            new CreateConnectionRequest(ShortName("c"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);
        var source = await sp.GetRequiredService<ISourceStore>().CreateAsync(
            new CreateSourceRequest(ShortName("s"), connection.Id, """{"bucketName":"b"}"""));
        return source.Id;
    }

    /// <summary>
    /// Ingests a source-owned file. A real source's bytes live on the remote, never on the local
    /// file system, so nothing is written there.
    /// </summary>
    private static async Task<Guid> IngestAsync(IServiceProvider sp, Guid sourceId, string path, string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);

        var created = await sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(
            new MemoryStream(bytes),
            new IngestionOptions(FileName: Path.GetFileName(path), ContentType: "text/plain", Path: path)
            {
                Owner = OwnerRef.ForSource(sourceId),
            });
        return Guid.Parse(created.DocumentId);
    }

    private static ReindexService Reindexer(IServiceProvider sp, KnowledgeDbContext ctx, RecordingIngestionQueue queue,
        params IDocumentParser[] parsers) =>
        new(ctx,
            sp.GetRequiredService<IKnowledgeFileSystem>(),
            sp.GetRequiredService<IManagedStorageProvider>(),
            sp.GetRequiredService<IContainerStore>(),
            queue,
            sp.GetRequiredService<IOptionsMonitor<ChunkingSettings>>(),
            sp.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>(),
            NullLogger<ReindexService>.Instance,
            parsers);

    [Fact]
    public async Task Ingest_RecordsTheParserAndItsVersion()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid sourceId = await SeedSourceAsync(sp);
        Guid id = await IngestAsync(sp, sourceId, $"/pv-{Guid.NewGuid():N}/notes.txt", "Notes on the quarterly plan.");

        await using var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var doc = await ctx.Documents.AsNoTracking().SingleAsync(d => d.Id == id);

        doc.Metadata[IngestionPipeline.MetadataKeyParser].Should().Be(nameof(TextParser));
        doc.Metadata[IngestionPipeline.MetadataKeyParserVersion].Should().Be("2");
    }

    [Fact]
    public async Task Reindex_ParserVersionBumped_RequeuesOnlyThatParsersDocuments()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid sourceId = await SeedSourceAsync(sp);
        string folder = $"/pv-{Guid.NewGuid():N}";
        Guid markdown = await IngestAsync(sp, sourceId, $"{folder}/guide.md", "# Guide\n\nHow the plan works.");
        Guid text = await IngestAsync(sp, sourceId, $"{folder}/notes.txt", "Notes on the quarterly plan.");

        var factory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var options = new ReindexOptions { DetectSettingsChanges = true, DocumentIds = [markdown.ToString(), text.ToString()] };

        // Nothing changed yet: both documents were parsed by the current TextParser.
        var unchanged = new RecordingIngestionQueue();
        await using (var ctx = await factory.CreateDbContextAsync())
            await Reindexer(sp, ctx, unchanged, new TextParser()).ReindexAsync(options, CancellationToken.None);
        unchanged.Jobs.Should().BeEmpty();

        // The Markdown reader gets a new version; the plain-text one does not.
        var queue = new RecordingIngestionQueue();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var result = await Reindexer(sp, ctx, queue, new Bumped(new TextParser(), version: 3, ".md"), new TextParser())
                .ReindexAsync(options, CancellationToken.None);
            result.Documents.Should().Contain(d => d.DocumentId == markdown.ToString() && d.Reason == ReindexReason.ParserChanged);
        }

        queue.Jobs.Select(j => j.DocumentId).Should().Equal(markdown.ToString());
    }

    [Fact]
    public async Task Reindex_DocumentIndexedBeforeVersionsWereRecorded_IsReparsedByTheNewerParser()
    {
        // Text documents indexed before #594 were read as UTF-8 regardless of encoding. They carry
        // no parser version, which counts as 1; TextParser is now 2.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid sourceId = await SeedSourceAsync(sp);
        Guid id = await IngestAsync(sp, sourceId, $"/pv-{Guid.NewGuid():N}/legacy.txt", "Café menu, old encoding.");

        var factory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var doc = await ctx.Documents.SingleAsync(d => d.Id == id);
            doc.Metadata = doc.Metadata
                .Where(kv => kv.Key is not IngestionPipeline.MetadataKeyParser and not IngestionPipeline.MetadataKeyParserVersion)
                .ToDictionary();
            await ctx.SaveChangesAsync();
        }

        var queue = new RecordingIngestionQueue();
        await using (var ctx = await factory.CreateDbContextAsync())
            await Reindexer(sp, ctx, queue, new TextParser()).ReindexAsync(
                new ReindexOptions { DetectSettingsChanges = true, DocumentIds = [id.ToString()] }, CancellationToken.None);

        queue.Jobs.Should().ContainSingle().Which.DocumentId.Should().Be(id.ToString());
    }

    [Fact]
    public async Task Reindex_LegacyDocumentWhoseExtensionAnotherParserTookOver_IsReparsed()
    {
        // No recorded name means "the parser that owned the extension then"; a different parser
        // at the same version is still a different parser.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid sourceId = await SeedSourceAsync(sp);
        Guid id = await IngestAsync(sp, sourceId, $"/pv-{Guid.NewGuid():N}/legacy.md", "# Legacy\n\nIndexed long ago.");

        var factory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var doc = await ctx.Documents.SingleAsync(d => d.Id == id);
            doc.Metadata = doc.Metadata
                .Where(kv => kv.Key is not IngestionPipeline.MetadataKeyParser and not IngestionPipeline.MetadataKeyParserVersion)
                .ToDictionary();
            await ctx.SaveChangesAsync();
        }

        var queue = new RecordingIngestionQueue();
        await using (var ctx = await factory.CreateDbContextAsync())
            await Reindexer(sp, ctx, queue, new Renamed("MarkdownEngine", version: 1, ".md")).ReindexAsync(
                new ReindexOptions { DetectSettingsChanges = true, DocumentIds = [id.ToString()] }, CancellationToken.None);

        queue.Jobs.Should().ContainSingle().Which.DocumentId.Should().Be(id.ToString());
    }

    /// <summary>Three text documents indexed before parser versions were recorded (#627).</summary>
    private static async Task<List<Guid>> SeedLegacyTextDocumentsAsync(IServiceProvider sp)
    {
        Guid sourceId = await SeedSourceAsync(sp);
        string folder = $"/pv-{Guid.NewGuid():N}";
        var ids = new List<Guid>();
        for (int i = 0; i < 3; i++)
            ids.Add(await IngestAsync(sp, sourceId, $"{folder}/legacy{i}.txt", $"Legacy note number {i}."));

        await using var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        foreach (var doc in await ctx.Documents.Where(d => ids.Contains(d.Id)).ToListAsync())
        {
            doc.Metadata = doc.Metadata
                .Where(kv => kv.Key is not IngestionPipeline.MetadataKeyParser and not IngestionPipeline.MetadataKeyParserVersion)
                .ToDictionary();
        }
        await ctx.SaveChangesAsync();
        return ids;
    }

    [Fact]
    public async Task Reindex_DryRun_ReportsWhatWouldBeQueuedAndQueuesNothing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var ids = await SeedLegacyTextDocumentsAsync(sp);

        var queue = new RecordingIngestionQueue();
        await using var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var result = await Reindexer(sp, ctx, queue, new TextParser()).ReindexAsync(
            new ReindexOptions { DetectSettingsChanges = true, DryRun = true, DocumentIds = ids.Select(i => i.ToString()).ToList() },
            CancellationToken.None);

        queue.Jobs.Should().BeEmpty();
        result.DryRun.Should().BeTrue();
        result.PlannedCount.Should().Be(3);
        result.ReasonCounts[ReindexReason.ParserChanged].Should().Be(3);
    }

    [Fact]
    public async Task Reindex_WithACap_QueuesUpToItAndDefersTheRest()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var ids = await SeedLegacyTextDocumentsAsync(sp);

        var queue = new RecordingIngestionQueue();
        await using var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var result = await Reindexer(sp, ctx, queue, new TextParser()).ReindexAsync(
            new ReindexOptions { DetectSettingsChanges = true, MaxDocuments = 2, DocumentIds = ids.Select(i => i.ToString()).ToList() },
            CancellationToken.None);

        queue.Jobs.Should().HaveCount(2);
        result.EnqueuedCount.Should().Be(2);
        result.DeferredCount.Should().Be(1);
    }

    [Fact]
    public async Task Reindex_DocumentAlreadyQueued_IsNotQueuedAgain()
    {
        // A capped rollout's next run must move on to the deferred documents rather than
        // re-queue the batch still in flight.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var ids = await SeedLegacyTextDocumentsAsync(sp);

        var factory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (var mark = await factory.CreateDbContextAsync())
        {
            var doc = await mark.Documents.SingleAsync(d => d.Id == ids[0]);
            doc.IngestionStatus = DocumentStatus.Queued;
            await mark.SaveChangesAsync();
        }

        var queue = new RecordingIngestionQueue();
        await using var ctx = await factory.CreateDbContextAsync();
        var result = await Reindexer(sp, ctx, queue, new TextParser()).ReindexAsync(
            new ReindexOptions { DetectSettingsChanges = true, DocumentIds = ids.Select(i => i.ToString()).ToList() },
            CancellationToken.None);

        queue.Jobs.Select(j => j.DocumentId).Should().NotContain(ids[0].ToString());
        result.Documents.Should().Contain(d => d.DocumentId == ids[0].ToString() && d.Reason == ReindexReason.AlreadyQueued);
    }

    /// <summary>A different parser, by name, for chosen extensions.</summary>
    private sealed class Renamed(string name, int version, params string[] extensions) : IDocumentParser
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        public string Name => name;
        public int Version => version;

        public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken = default) =>
            new TextParser().ParseAsync(stream, fileName, cancellationToken);
    }

    /// <summary>The same reader under a newer version, for chosen extensions only.</summary>
    private sealed class Bumped(IDocumentParser inner, int version, params string[] extensions) : IDocumentParser
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        public string Name => inner.Name;
        public int Version => version;

        public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken = default) =>
            inner.ParseAsync(stream, fileName, cancellationToken);
    }
}
