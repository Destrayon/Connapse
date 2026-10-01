using System.Net;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Confluence documents through the real search pipeline (composite resolver, composite verifier,
/// HybridSearchService): a linked user sees only what the fake Confluence allows their account,
/// an unlinked user sees none of it and causes no Confluence calls, and other documents are untouched.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AtlassianDocumentEnforcementTests(SharedWebAppFixture fixture)
{
    private const string Term = "zorblaxquent";
    private const string AllowedAccount = "557058:allowed";
    private const string DeniedAccount = "557058:denied";

    // Each test gets its own site, so routes and cached answers never leak between tests.
    private readonly string _cloudId = Guid.NewGuid().ToString("D");

    private string CheckPath(string contentId) =>
        $"/ex/confluence/{_cloudId}/wiki/rest/api/content/{contentId}/permission/check";

    private int CheckCalls => fixture.Atlassian.Calls
        .Where(c => c.Key.StartsWith($"/ex/confluence/{_cloudId}/", StringComparison.Ordinal)).Sum(c => c.Value);

    /// <summary>Page 101 (and its attachment) is readable by <see cref="AllowedAccount"/> only; page 102 by nobody.</summary>
    private async Task<Guid> SeedAsync(IServiceProvider sp)
    {
        fixture.Atlassian.Map(CheckPath("101"), request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            string? who = body.RootElement.GetProperty("subject").GetProperty("identifier").GetString();
            return Connapse.Core.Tests.Connectors.FakeAtlassianApi.Json(new { hasPermission = who == AllowedAccount });
        });
        fixture.Atlassian.MapJson(CheckPath("102"), new { hasPermission = false });

        await sp.GetRequiredService<IConnectionStore>().CreateAsync(new CreateConnectionRequest(
            $"atl-{_cloudId}", ConnectionProvider.Atlassian,
            $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{_cloudId}}","clientId":"client-{{_cloudId}}"}""",
            "service-secret"), null);

        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new (string, string?)[]
                 {
                     ("allowed-page.md", AtlassianUri.ForPage(_cloudId, "101")),
                     ("allowed-attachment.pdf", AtlassianUri.ForAttachment(_cloudId, "101", "9001")),
                     ("denied-page.md", AtlassianUri.ForPage(_cloudId, "102")),
                     ("azure-granted.md", "azblob://acct/docs/azure.md"),
                     ("azure-ungranted.md", "azblob://acct/secret/azure.md"),
                     ("upload.md", null),
                 })
        {
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(), ContainerId = container.Id, FileName = name, Path = "/" + name,
                ResourceUri = uri, ContentHash = Guid.NewGuid().ToString("N"), IngestionStatus = DocumentStatus.Ready,
                CreatedAt = DateTime.UtcNow, Metadata = [],
            };
            db.Documents.Add(document);
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(), DocumentId = document.Id, OwnerId = container.Id,
                Content = $"the {Term} appears here", ChunkIndex = 0, Metadata = [],
            });
        }

        await db.SaveChangesAsync();
        return container.Id;
    }

    private static async Task<Guid> SeedUserAsync(IServiceProvider sp, string? atlassianAccount)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        var user = new Connapse.Identity.Data.Entities.ConnapseUser
        {
            Id = Guid.NewGuid(),
            UserName = $"u-{Guid.NewGuid():N}@example.com",
            Email = $"u-{Guid.NewGuid():N}@example.com",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        if (atlassianAccount is not null)
            await sp.GetRequiredService<AtlassianIdentityLinkStore>().SaveAsync(user.Id, atlassianAccount, "Someone", null);
        return user.Id;
    }

    private async Task<IEnumerable<string?>> SearchAsync(string? atlassianAccount)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await SearchAsync(await SeedAsync(scope.ServiceProvider), atlassianAccount);
    }

    private async Task<IEnumerable<string?>> SearchAsync(Guid containerId, string? atlassianAccount)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid userId = await SeedUserAsync(sp, atlassianAccount);

        var result = await sp.GetRequiredService<IKnowledgeSearch>().SearchAsync(Term,
            new SearchOptions(TopK: 20, ContainerId: containerId.ToString(), Mode: SearchMode.Keyword, UserId: userId));
        return result.Hits.Select(h => h.Metadata.GetValueOrDefault("fileName"));
    }

    [Fact]
    public async Task Search_LinkedAllowed_Sees()
    {
        var names = await SearchAsync(AllowedAccount);

        names.Should().Contain(["allowed-page.md", "allowed-attachment.pdf"])
            .And.NotContain("denied-page.md");
        fixture.Atlassian.Calls.GetValueOrDefault(CheckPath("9001")).Should().Be(0, "an attachment is checked against its page");
    }

    [Fact]
    public async Task Search_LinkedDenied_DoesNotSee()
    {
        var names = await SearchAsync(DeniedAccount);

        names.Should().NotContain(["allowed-page.md", "allowed-attachment.pdf", "denied-page.md"])
            .And.Contain("upload.md");
        CheckCalls.Should().BeGreaterThan(0, "a linked user's hits are checked live");
    }

    [Fact]
    public async Task Search_Unlinked_DoesNotSeeAndMakesNoCalls()
    {
        var names = await SearchAsync(atlassianAccount: null);

        names.Should().NotContain(["allowed-page.md", "allowed-attachment.pdf", "denied-page.md"])
            .And.Contain("upload.md");
        CheckCalls.Should().Be(0);
    }

    [Fact]
    public async Task Search_AzureDocsUnaffected()
    {
        // The Azure verifier with a real document store and an RBAC grant on azblob://acct/docs/, as
        // AzureVerifyEnforcementTests builds it, run alone and then under the composite beside the
        // real Atlassian verifier: Azure's decisions must come out the same.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid containerId = await SeedAsync(sp);
        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        SearchHit[] ranked = [.. (await db.Documents.AsNoTracking().Where(d => d.ContainerId == containerId)
                .OrderBy(d => d.FileName).ToListAsync())
            .Select(d => new SearchHit("c-" + d.Id, d.Id.ToString(), "", 1f, new() { ["fileName"] = d.FileName }))];

        var azure = AzureVerifyEnforcementTests.BuildVerifier(
            sp.GetRequiredService<IDocumentStore>(), azureEnforcing: true, [new AzureScope("azblob://acct/docs/")]);
        var atlassian = sp.GetServices<IPerSchemeResultVerifier>().OfType<AtlassianSearchResultVerifier>().Single();
        Guid user = AzureVerifyEnforcementTests.TestUser; // has no Atlassian link

        var alone = await azure.VerifyAsync(ranked, user, ranked.Length);
        var composite = await new CompositeSearchResultVerifier([azure, atlassian]).VerifyAsync(ranked, user, ranked.Length);

        static IEnumerable<string> Azure(IEnumerable<SearchHit> hits) =>
            hits.Select(h => h.Metadata["fileName"]).Where(n => n.StartsWith("azure", StringComparison.Ordinal));
        Azure(alone).Should().Equal("azure-granted.md");
        Azure(composite).Should().Equal(Azure(alone));
        composite.Select(h => h.Metadata["fileName"]).Should().Equal("azure-granted.md", "upload.md");
    }

    [Fact]
    public async Task Search_ConfluenceUnavailable_DeniesEveryConfluenceHit()
    {
        fixture.Atlassian.FailWith(HttpStatusCode.ServiceUnavailable);
        try
        {
            var names = await SearchAsync(AllowedAccount);

            names.Should().NotContain(["allowed-page.md", "allowed-attachment.pdf", "denied-page.md"])
                .And.Contain("upload.md");
        }
        finally
        {
            fixture.Atlassian.FailWith(null);
        }
    }
}
