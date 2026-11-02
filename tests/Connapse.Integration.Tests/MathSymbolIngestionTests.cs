using System.Text;
using Connapse.Background.Jobs;
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
/// #334: a PDF of mathematics stayed Processing for ever. A chunk boundary split one of its
/// surrogate pairs, Npgsql could not encode the half left over, and saving the Failed status
/// quoting that text failed the same way. These run the real parser, chunker, job and lifecycle
/// against real PostgreSQL, where Npgsql's strict UTF-8 encoder and the NUL check actually apply.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MathSymbolIngestionTests(SharedWebAppFixture fixture)
{
    /// <summary>U+1D465 MATHEMATICAL ITALIC SMALL X: one character, two UTF-16 units.</summary>
    private const string MathX = "\U0001D465";

    [Fact]
    public async Task IngestAsync_PdfOfMathLettersWhoseChunksSplitASurrogatePair_ReachesReady()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var (containerId, documentId) = await SeedQueuedDocumentAsync(sp);

        var result = await sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(
            new MemoryStream(MathPdf()), Options(containerId, documentId));

        result.ChunkCount.Should().BeGreaterThan(1);
        var doc = await ReadAsync(sp, documentId);
        doc.IngestionStatus.Should().Be(DocumentStatus.Ready);
        doc.ErrorMessage.Should().BeNull();

        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await dbFactory.CreateDbContextAsync();
        List<string> chunks = await ctx.Chunks.Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.ChunkIndex).Select(c => c.Content).ToListAsync();
        chunks.Should().HaveCount(result.ChunkCount);
        chunks.Should().Contain(c => c.Contains("unknown in the quadratic equation"));
        chunks.Should().Contain(c => c.Contains(MathX + MathX), "the math letters are stored, not stripped");
        chunks.Should().NotContain(c => c.Contains('\0'));
        chunks.Should().Contain(c => c.StartsWith('�') || c.EndsWith('�'),
            "the chunker cut through a pair, and the half at the edge was replaced rather than failing the write");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IngestJob_FailureWhoseMessageHoldsALoneSurrogateAndNul_IsSavedAsFailed(bool permanent)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var (containerId, documentId) = await SeedQueuedDocumentAsync(sp);
        var lifecycle = sp.GetRequiredService<IDocumentLifecycle>();
        int generation = (await lifecycle.EnqueuedAsync(documentId, resetAttempts: true))!.Value;

        // A transient failure is final on the last attempt; the claim below makes it that one.
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (var ctx = await dbFactory.CreateDbContextAsync())
        {
            await ctx.Documents.Where(d => d.Id == documentId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.AttemptCount, IngestionJobs.MaxAttempts - 1));
        }

        string message = $"Embedding failed on \"{MathX}\uD835\" at\0 offset 3253";
        Exception failure = permanent ? new PermanentIngestionException(message) : new InvalidOperationException(message);
        var pipeline = ActivatorUtilities.CreateInstance<IngestionPipeline>(
            sp, (IEmbeddingProvider)new ThrowingEmbeddingProvider(failure));
        var jobs = ActivatorUtilities.CreateInstance<IngestionJobs>(
            sp, (IKnowledgeIngester)new StoredFileIngester(pipeline, MathPdf()));

        Func<Task> run = () => jobs.IngestAsync(
            documentId.ToString(), Options(containerId, documentId) with { Generation = generation }, CancellationToken.None);

        if (permanent)
            await run.Should().NotThrowAsync();
        else
            await run.Should().ThrowAsync<InvalidOperationException>("a final transient failure is rethrown for the dashboard");

        var doc = await ReadAsync(sp, documentId);
        doc.IngestionStatus.Should().Be(permanent ? DocumentStatus.FailedPermanent : DocumentStatus.FailedRetryable);
        doc.ErrorMessage.Should().Be($"Embedding failed on \"{MathX}�\" at offset 3253");
    }

    private static IngestionOptions Options(Guid containerId, Guid documentId) =>
        new(DocumentId: documentId.ToString(), FileName: "proof.pdf", ContentType: "application/pdf",
            ContainerId: containerId.ToString(), Path: "/proof.pdf", Strategy: ChunkingStrategy.Recursive)
        {
            StrategyIsExplicit = true,
        };

    private static async Task<(Guid ContainerId, Guid DocumentId)> SeedQueuedDocumentAsync(IServiceProvider sp)
    {
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid containerId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();

        await using var ctx = await dbFactory.CreateDbContextAsync();
        ctx.Containers.Add(new ContainerEntity
        {
            Id = containerId,
            Name = $"math-{containerId:N}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        ctx.Documents.Add(new DocumentEntity
        {
            Id = documentId,
            ContainerId = containerId,
            FileName = "proof.pdf",
            Path = "/proof.pdf",
            ContentHash = "",
            CreatedAt = DateTime.UtcNow,
            Metadata = [],
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

    /// <summary>
    /// A one-page PDF whose oblique font maps, through its ToUnicode table, code 01 to U+1D465 and
    /// code 03 to NUL, as LaTeX math fonts and broken glyph mappings do. One line reads as prose;
    /// the other is an "a" followed by a thousand math x's with no space, so the chunker has to cut
    /// inside the run, and the odd unit in front puts each cut between the halves of a pair.
    /// </summary>
    private static byte[] MathPdf()
    {
        const string cmap = """
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CMapName /Math def
            1 begincodespacerange
            <00> <FF>
            endcodespacerange
            3 beginbfchar
            <01> <D835DC65>
            <03> <0000>
            <04> <0061>
            endbfchar
            endcmap
            CMapName currentdict /CMap defineresource pop
            end
            end
            """;
        string run = "04" + string.Concat(Enumerable.Repeat("01", 1000));
        string content =
            "BT /F1 12 Tf 72 720 Td (Let ) Tj ET\n" +
            "BT /F2 12 Tf 100 720 Td <010301> Tj ET\n" +
            "BT /F1 12 Tf 140 720 Td (be the unknown in the quadratic equation.) Tj ET\n" +
            $"BT /F2 1 Tf 72 600 Td <{run}> Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /CMMI10 /FirstChar 1 /LastChar 4 /Widths [500 500 500 500] /FontDescriptor 8 0 R /ToUnicode 7 0 R >>",
            $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream",
            "<< /Type /FontDescriptor /FontName /CMMI10 /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle -14 /Ascent 750 /Descent -250 /CapHeight 700 /StemV 80 >>",
        };
        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(body.Length);
            body.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = body.Length;
        body.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) body.Append($"{offset:D10} 00000 n \n");
        body.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());
    }

    /// <summary>Hands the job's pipeline the file, as reading it from the container's storage would.</summary>
    private sealed class StoredFileIngester(IngestionPipeline pipeline, byte[] file) : IKnowledgeIngester
    {
        public Task<IngestionResult> IngestAsync(Stream content, IngestionOptions options, CancellationToken ct = default) =>
            pipeline.IngestAsync(content, options, ct);

        public Task<IngestionResult> IngestByIdAsync(string documentId, IngestionOptions options, CancellationToken ct = default) =>
            pipeline.IngestAsync(new MemoryStream(file), options, ct);
    }

    private sealed class ThrowingEmbeddingProvider(Exception failure) : IEmbeddingProvider
    {
        public int Dimensions => 768;
        public string ModelId => "throwing";

        public Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default) =>
            throw failure;

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(
            IEnumerable<string> texts, EmbeddingInputType inputType, CancellationToken ct = default) =>
            throw failure;
    }
}
