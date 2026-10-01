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

    /// <summary>Ingests a file and stores its bytes where a reindex will look for them.</summary>
    private static async Task<Guid> IngestAsync(IServiceProvider sp, Guid sourceId, string path, string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        await sp.GetRequiredService<IKnowledgeFileSystem>().SaveFileAsync(path, new MemoryStream(bytes));

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
