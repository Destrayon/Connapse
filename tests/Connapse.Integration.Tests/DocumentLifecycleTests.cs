using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Pipeline;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The document lifecycle's guarantees against real PostgreSQL: one winner per claim, no
/// completion for a superseded generation, and a chunk swap that either lands whole or not at all.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DocumentLifecycleTests(SharedWebAppFixture fixture)
{
    private async Task<(Guid ContainerId, Guid DocumentId)> SeedQueuedDocumentAsync(IServiceProvider sp)
    {
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid containerId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();

        await using var ctx = await dbFactory.CreateDbContextAsync();
        ctx.Containers.Add(new ContainerEntity
        {
            Id = containerId, Name = $"lc-{containerId:N}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        ctx.Documents.Add(new DocumentEntity
        {
            Id = documentId, ContainerId = containerId, FileName = "doc.md", Path = "/doc.md",
            ContentHash = "", CreatedAt = DateTime.UtcNow, Metadata = [],
        });
        await ctx.SaveChangesAsync();
        return (containerId, documentId);
    }

    private static async Task<DocumentEntity> ReadAsync(IServiceProvider sp, Guid documentId)
    {
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await dbFactory.CreateDbContextAsync();
        return await ctx.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
    }

    [Fact]
    public async Task TryClaimAsync_TwoWorkersRacing_ExactlyOneWins()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (_, documentId) = await SeedQueuedDocumentAsync(scope.ServiceProvider);
        var lifecycle = scope.ServiceProvider.GetRequiredService<IDocumentLifecycle>();
        int generation = (await lifecycle.EnqueuedAsync(documentId, resetAttempts: true))!.Value;

        bool[] claims = await Task.WhenAll(
            lifecycle.TryClaimAsync(documentId, generation),
            lifecycle.TryClaimAsync(documentId, generation));

        claims.Count(c => c).Should().Be(1);
        var doc = await ReadAsync(scope.ServiceProvider, documentId);
        doc.IngestionStatus.Should().Be(DocumentStatus.Processing);
        doc.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task CompleteAsync_AfterTheDocumentWasReEnqueued_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (_, documentId) = await SeedQueuedDocumentAsync(scope.ServiceProvider);
        var lifecycle = scope.ServiceProvider.GetRequiredService<IDocumentLifecycle>();

        int first = (await lifecycle.EnqueuedAsync(documentId, resetAttempts: true))!.Value;
        (await lifecycle.TryClaimAsync(documentId, first)).Should().BeTrue();

        // A new upload arrives while the first job works.
        int second = (await lifecycle.EnqueuedAsync(documentId, resetAttempts: true))!.Value;

        (await lifecycle.CompleteAsync(documentId, first)).Should().BeFalse();
        (await lifecycle.FailAsync(documentId, first, DocumentStatus.FailedRetryable, "late")).Should().BeFalse();
        (await lifecycle.TryClaimAsync(documentId, second)).Should().BeTrue();
        (await lifecycle.CompleteAsync(documentId, second)).Should().BeTrue();

        (await ReadAsync(scope.ServiceProvider, documentId)).IngestionStatus.Should().Be(DocumentStatus.Ready);
    }

    [Fact]
    public async Task EnqueuedAsync_KeepsTheAttemptBudgetUnlessToldToReset()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (_, documentId) = await SeedQueuedDocumentAsync(scope.ServiceProvider);
        var lifecycle = scope.ServiceProvider.GetRequiredService<IDocumentLifecycle>();

        int generation = (await lifecycle.EnqueuedAsync(documentId, resetAttempts: true))!.Value;
        await lifecycle.TryClaimAsync(documentId, generation);
        await lifecycle.FailAsync(documentId, generation, DocumentStatus.FailedRetryable, "down");

        await lifecycle.EnqueuedAsync(documentId, resetAttempts: false);
        (await ReadAsync(scope.ServiceProvider, documentId)).AttemptCount.Should().Be(1);

        await lifecycle.EnqueuedAsync(documentId, resetAttempts: true);
        var doc = await ReadAsync(scope.ServiceProvider, documentId);
        doc.AttemptCount.Should().Be(0);
        doc.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task IngestAsync_DocumentReEnqueuedWhileEmbedding_KeepsTheOldChunks()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var (containerId, documentId) = await SeedQueuedDocumentAsync(sp);
        var lifecycle = sp.GetRequiredService<IDocumentLifecycle>();
        var pipeline = sp.GetRequiredService<IKnowledgeIngester>();

        var v1 = new IngestionOptions(
            DocumentId: documentId.ToString(), FileName: "doc.md", ContentType: "text/markdown",
            ContainerId: containerId.ToString(), Path: "/doc.md");
        await pipeline.IngestAsync(new MemoryStream("# First\n\nThe first version of this document."u8.ToArray()), v1);

        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        List<string> before;
        await using (var ctx = await dbFactory.CreateDbContextAsync())
            before = await ctx.Chunks.Where(c => c.DocumentId == documentId).Select(c => c.Content).ToListAsync();
        before.Should().NotBeEmpty();

        // A job claims the second version; while it embeds, a third version is uploaded.
        int generation = (await lifecycle.EnqueuedAsync(documentId, resetAttempts: true))!.Value;
        (await lifecycle.TryClaimAsync(documentId, generation)).Should().BeTrue();

        var reEnqueuing = new ReEnqueuingEmbeddingProvider(
            sp.GetRequiredService<IEmbeddingProvider>(), () => lifecycle.EnqueuedAsync(documentId, resetAttempts: true));
        var racingPipeline = ActivatorUtilities.CreateInstance<IngestionPipeline>(sp, (IEmbeddingProvider)reEnqueuing);

        var result = await racingPipeline.IngestAsync(
            new MemoryStream("# Second\n\nA second version that must not land."u8.ToArray()),
            v1 with { Generation = generation });

        result.ChunkCount.Should().Be(0, "the work was superseded");
        await using (var ctx = await dbFactory.CreateDbContextAsync())
        {
            (await ctx.Chunks.Where(c => c.DocumentId == documentId).Select(c => c.Content).ToListAsync())
                .Should().BeEquivalentTo(before, "the old version stays searchable until a newer one commits");
        }
        (await ReadAsync(sp, documentId)).IngestionStatus.Should().Be(DocumentStatus.Queued);
    }

    [Fact]
    public async Task GetDocumentTextAsync_OverlappingChunks_AreJoinedWithoutRepeatingTheOverlap()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var (containerId, documentId) = await SeedQueuedDocumentAsync(sp);

        const string text = "alpha beta gamma delta";
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (var ctx = await dbFactory.CreateDbContextAsync())
        {
            // "alpha beta " [0,11) and "beta gamma delta" [6,22): "beta " is shared.
            ctx.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(), DocumentId = documentId, OwnerId = containerId, ChunkIndex = 0,
                Content = text[..11], StartOffset = 0, EndOffset = 11, TokenCount = 2, Metadata = [],
            });
            ctx.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(), DocumentId = documentId, OwnerId = containerId, ChunkIndex = 1,
                Content = text[6..], StartOffset = 6, EndOffset = 22, TokenCount = 3, Metadata = [],
            });
            await ctx.SaveChangesAsync();
        }

        string? rebuilt = await sp.GetRequiredService<IDocumentStore>().GetDocumentTextAsync(documentId.ToString());

        rebuilt.Should().Be(text);
    }

    /// <summary>Embeds normally, but re-enqueues the document first — a re-upload landing mid-embed.</summary>
    private sealed class ReEnqueuingEmbeddingProvider(IEmbeddingProvider inner, Func<Task> reEnqueue) : IEmbeddingProvider
    {
        public int Dimensions => inner.Dimensions;
        public string ModelId => inner.ModelId;

        public Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default) =>
            inner.EmbedAsync(text, inputType, ct);

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IEnumerable<string> texts, EmbeddingInputType inputType, CancellationToken ct = default)
        {
            await reEnqueue();
            return await inner.EmbedBatchAsync(texts, inputType, ct);
        }
    }
}
