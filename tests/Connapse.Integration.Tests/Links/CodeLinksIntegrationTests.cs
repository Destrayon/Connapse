using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Pipeline;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Links;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests.Links;

/// <summary>Code files ingested end to end assert their definitions and calls as links (CON-109).</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CodeLinksIntegrationTests(SharedWebAppFixture fixture)
{
    private const string AuditLog = """
        namespace Acme.Audit;

        public static class AuditLog
        {
            public static void Write(object entry) { }
        }
        """;

    private const string Ledger = """
        using Acme.Audit;

        namespace Acme.Billing;

        public class Ledger
        {
            public void Post(object entry)
            {
                AuditLog.Write(entry);
            }
        }
        """;

    private static async Task<Guid> SeedContainerAsync(IServiceProvider sp)
    {
        await using KnowledgeDbContext db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"code-{Guid.NewGuid():N}" };
        db.Containers.Add(container);
        await db.SaveChangesAsync();
        return container.Id;
    }

    private static async Task<string> IngestAsync(IServiceProvider sp, Guid container, string path, string text, string? documentId = null)
    {
        var result = await sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(text)),
            new IngestionOptions(FileName: Path.GetFileName(path), ContentType: "text/plain", Path: path)
            {
                Owner = OwnerRef.ForContainer(container),
                DocumentId = documentId,
            });
        return result.DocumentId;
    }

    private static async Task<LinkExpansion> ExpandAsync(IServiceProvider sp, string fromDocument, params string[] readable)
    {
        ILookup<string, string> nodes = await sp.GetRequiredService<PostgresLinkStore>().NodesOfAsync([fromDocument]);
        var access = new DocumentAccessStub();
        Guid reader = Guid.NewGuid();
        access.Grant(reader, readable);
        return await new PostgresLinkReader(sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>(), access)
            .ExpandAsync(new LinkExpandRequest(reader, [.. nodes[fromDocument]], MaxHops: 1, MaxNodes: 500));
    }

    [Fact]
    public async Task Ingest_CallInOneFile_LinksToTheDefinitionInAnother()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        Guid container = await SeedContainerAsync(sp);
        string audit = await IngestAsync(sp, container, "/acme/AuditLog.cs", AuditLog);
        string ledger = await IngestAsync(sp, container, "/acme/Ledger.cs", Ledger);

        LinkExpansion answer = await ExpandAsync(sp, ledger, audit, ledger);

        VisibleEdge call = answer.Edges.Should().ContainSingle(e => e.Type == "calls").Subject;
        call.FromId.Should().EndWith(":csharp:Acme.Billing.Ledger.Post");
        call.ToId.Should().EndWith(":csharp:Acme.Audit.AuditLog.Write");
        call.Confidence.Should().BeApproximately(0.7, 0.001);
        call.Evidence.Should().ContainSingle().Which.Text.Should().Be("acme/Ledger.cs:9 (namespace in scope) AuditLog.Write(entry);");
        answer.Nodes.Should().NotContain(n => n.Id.EndsWith("Acme.Billing.AuditLog.Write"), "no file defines that candidate");

        await using KnowledgeDbContext db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        DocumentEntity document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == Guid.Parse(ledger));
        document.Metadata[IngestionPipeline.MetadataKeyChunkingStrategy].Should().Be(CodeChunker.StrategyName);
        List<Dictionary<string, string>> chunks = await db.Chunks.AsNoTracking().Where(c => c.DocumentId == Guid.Parse(ledger))
            .Select(c => c.Metadata).ToListAsync();
        chunks.Select(c => c.GetValueOrDefault(CodeChunker.MetadataKeySymbol)).Should().Contain("Acme.Billing.Ledger");
    }

    [Fact]
    public async Task Ingest_CalleeNotReadable_HidesTheCall()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        Guid container = await SeedContainerAsync(sp);
        await IngestAsync(sp, container, "/acme/AuditLog.cs", AuditLog);
        string ledger = await IngestAsync(sp, container, "/acme/Ledger.cs", Ledger);

        LinkExpansion answer = await ExpandAsync(sp, ledger, ledger);

        answer.Edges.Should().NotContain(e => e.Type == "calls");
    }

    [Fact]
    public async Task Reingest_ChangedFile_ReplacesTheLinksItAsserted()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        Guid container = await SeedContainerAsync(sp);
        string audit = await IngestAsync(sp, container, "/acme/AuditLog.cs", AuditLog);
        string ledger = await IngestAsync(sp, container, "/acme/Ledger.cs", Ledger);

        await IngestAsync(sp, container, "/acme/Ledger.cs", Ledger.Replace("AuditLog.Write(entry);", "_ = entry;"), ledger);

        LinkExpansion answer = await ExpandAsync(sp, ledger, audit, ledger);
        answer.Edges.Should().NotContain(e => e.Type == "calls");
        answer.Edges.Should().Contain(e => e.Type == "defines" && e.ToId.EndsWith("Acme.Billing.Ledger.Post"));
    }

    [Fact]
    public async Task ExpandAsync_Confidence_IsTheHighestAReadableDocumentGave()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        string run = Guid.NewGuid().ToString("N");
        string a = $"a-{run}", b = $"b-{run}", low = $"low-{run}", high = $"high-{run}", zero = $"zero-{run}";
        await scope.ServiceProvider.GetRequiredService<PostgresLinkStore>().ReplaceAsync([
            new DocumentLinks(low, null, [new LinkFact(a, LinkFactField.Name, "a"), new LinkFact(b, LinkFactField.Name, "b")],
                [new LinkAssertion($"l-{run}", a, b, "calls", "guess", 0.4)]),
            new DocumentLinks(high, null, [], [new LinkAssertion($"l-{run}", a, b, "calls", "sure", 0.9)]),
            new DocumentLinks(zero, null, [], [new LinkAssertion($"z-{run}", b, a, "calls", "none", 0)]),
        ]);
        var access = new DocumentAccessStub();
        Guid reader = Guid.NewGuid();
        access.Grant(reader, [low, zero]);

        LinkExpansion answer = await new PostgresLinkReader(
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>(), access)
            .ExpandAsync(new LinkExpandRequest(reader, [a], MaxHops: 1));

        answer.Edges.Single(e => e.Id == $"l-{run}").Confidence.Should().BeApproximately(0.4, 0.001,
            "the 0.9 came from a document this reader cannot open");
        answer.Edges.Single(e => e.Id == $"z-{run}").Confidence.Should().Be(0, "a confidence of 0 is stored as written");
    }
}
