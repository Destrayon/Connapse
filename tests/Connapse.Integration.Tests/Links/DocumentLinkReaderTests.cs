using System.Net;
using System.Net.Http.Json;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Links;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests.Links;

/// <summary>A file's links as the file browser shows them (CON-214): what it links to, what links to it, and only what the reader may open.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DocumentLinkReaderTests(SharedWebAppFixture fixture)
{
    private const string AuditLog = """
        namespace Acme.Audit;

        public class AuditLog
        {
            public static void Write(object entry) { }
        }
        """;

    private const string Ledger = """
        using Acme.Audit;

        namespace Acme.Billing;

        public class Ledger : AuditLog
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
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"links-{Guid.NewGuid():N}" };
        db.Containers.Add(container);
        await db.SaveChangesAsync();
        return container.Id;
    }

    private static async Task<string> IngestAsync(IServiceProvider sp, Guid container, string path, string text)
    {
        var result = await sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(text)),
            new IngestionOptions(FileName: Path.GetFileName(path), ContentType: "text/plain", Path: path)
            {
                Owner = OwnerRef.ForContainer(container),
            });
        return result.DocumentId;
    }

    private static async Task<string> RecordAsync(IServiceProvider sp, Guid container, string path)
    {
        await using KnowledgeDbContext db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            ContainerId = container,
            FileName = Path.GetFileName(path),
            Path = path,
            ContentHash = Guid.NewGuid().ToString("N"),
            IngestionStatus = DocumentStatus.Ready,
            CreatedAt = DateTime.UtcNow,
            Metadata = [],
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document.Id.ToString();
    }

    [Fact]
    public async Task ReadAsync_CallAndBaseInAnotherFile_ListsEachSideOnce()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        Guid container = await SeedContainerAsync(sp);
        string audit = await IngestAsync(sp, container, "/acme/AuditLog.cs", AuditLog);
        string ledger = await IngestAsync(sp, container, "/acme/billing/Ledger.cs", Ledger);
        IDocumentLinkReader links = sp.GetRequiredService<IDocumentLinkReader>();

        DocumentLinkSet fromLedger = await links.ReadAsync(null, container, ledger);
        DocumentLinkSet fromAudit = await links.ReadAsync(null, container, audit);

        LinkedDocument target = fromLedger.LinksTo.Should().ContainSingle().Subject;
        target.Should().BeEquivalentTo(new LinkedDocument(audit, "AuditLog.cs", "/acme/AuditLog.cs", ["calls", "inherits"]));
        fromLedger.LinkedFrom.Should().BeEmpty();
        fromAudit.LinksTo.Should().BeEmpty("a file's own definitions are not links to other files");
        fromAudit.LinkedFrom.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new LinkedDocument(ledger, "Ledger.cs", "/acme/billing/Ledger.cs", ["calls", "inherits"]));
    }

    [Fact]
    public async Task ReadAsync_FileWithNoLinks_IsEmpty()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        Guid container = await SeedContainerAsync(sp);
        string notes = await IngestAsync(sp, container, "/notes.txt", "Nothing here points anywhere.");

        DocumentLinkSet answer = await sp.GetRequiredService<IDocumentLinkReader>().ReadAsync(null, container, notes);

        answer.Should().BeEquivalentTo(DocumentLinkSet.Empty);
    }

    [Fact]
    public async Task ReadAsync_TargetInAnotherContainer_IsLeftOut()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        Guid here = await SeedContainerAsync(sp), elsewhere = await SeedContainerAsync(sp);
        string a = await RecordAsync(sp, here, "/a.md"), b = await RecordAsync(sp, here, "/b.md"), c = await RecordAsync(sp, elsewhere, "/c.md");
        string run = Guid.NewGuid().ToString("N");
        string na = $"a-{run}", nb = $"b-{run}", nc = $"c-{run}";
        await sp.GetRequiredService<PostgresLinkStore>().ReplaceAsync([
            new DocumentLinks(a, na, [new LinkFact(na, LinkFactField.Name, "a")],
                [new LinkAssertion($"ab-{run}", na, nb, "mentions"), new LinkAssertion($"ac-{run}", na, nc, "mentions")]),
            new DocumentLinks(b, nb, [new LinkFact(nb, LinkFactField.Name, "b")], []),
            new DocumentLinks(c, nc, [new LinkFact(nc, LinkFactField.Name, "c")], []),
        ]);

        DocumentLinkSet answer = await sp.GetRequiredService<IDocumentLinkReader>().ReadAsync(null, here, a);

        answer.LinksTo.Select(d => d.Id).Should().Equal([b], "the other container's document is not this reader's to open here, and isn't counted");
        answer.LinkedFrom.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFileLinks_OverRest_AnswersTheLinksAndHidesAFileFromAnotherContainer()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        Guid container = await SeedContainerAsync(sp), other = await SeedContainerAsync(sp);
        string audit = await IngestAsync(sp, container, "/acme/AuditLog.cs", AuditLog);
        string ledger = await IngestAsync(sp, container, "/acme/billing/Ledger.cs", Ledger);
        string stranger = await RecordAsync(sp, other, "/stranger.md");

        HttpResponseMessage ok = await fixture.AdminClient.GetAsync($"/api/containers/{container}/files/{ledger}/links");
        HttpResponseMessage back = await fixture.AdminClient.GetAsync($"/api/containers/{container}/files/{audit}/links");
        HttpResponseMessage foreign = await fixture.AdminClient.GetAsync($"/api/containers/{container}/files/{stranger}/links");

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        DocumentLinkSet? body = await ok.Content.ReadFromJsonAsync<DocumentLinkSet>();
        body!.LinksTo.Should().ContainSingle().Which.Id.Should().Be(audit);
        back.StatusCode.Should().Be(HttpStatusCode.OK);
        (await back.Content.ReadFromJsonAsync<DocumentLinkSet>())!.LinkedFrom.Should().ContainSingle().Which.Id.Should().Be(ledger);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound, "a file of another container is answered as missing");
    }
}
