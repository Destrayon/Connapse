using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Web.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
                Id = Guid.NewGuid(),
                ContainerId = container.Id,
                FileName = name,
                Path = "/" + name,
                ResourceUri = uri,
                ContentHash = Guid.NewGuid().ToString("N"),
                IngestionStatus = DocumentStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            };
            db.Documents.Add(document);
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                OwnerId = container.Id,
                Content = $"the {Term} appears here",
                ChunkIndex = 0,
                Metadata = [],
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
            await sp.GetRequiredService<AtlassianIdentityLinkStore>().SaveAsync(user.Id, atlassianAccount);
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

    /// <summary>A problem body without its traceId, which differs on every request whatever the outcome.</summary>
    private static async Task<string> BodyAsync(HttpResponseMessage response)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body.Remove("traceId");
        return body.ToJsonString();
    }

    private static async Task<string> DocumentIdAsync(IServiceProvider sp, Guid containerId, string fileName)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        return (await db.Documents.AsNoTracking().SingleAsync(d => d.ContainerId == containerId && d.FileName == fileName)).Id.ToString();
    }

    /// <summary>A Viewer with a password and a bearer token, linked to <paramref name="atlassianAccount"/>.</summary>
    private async Task<HttpClient> ViewerClientAsync(IServiceProvider sp, string atlassianAccount)
    {
        string email = $"v-{Guid.NewGuid():N}@example.com";
        const string Password = "AtlViewerTest1!";
        var users = sp.GetRequiredService<UserManager<Connapse.Identity.Data.Entities.ConnapseUser>>();
        var user = new Connapse.Identity.Data.Entities.ConnapseUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = email,
            CreatedAt = DateTime.UtcNow,
        };
        (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
        await users.AddToRoleAsync(user, "Viewer");
        await sp.GetRequiredService<AtlassianIdentityLinkStore>().SaveAsync(user.Id, atlassianAccount);

        using var anon = fixture.Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/v1/auth/token", new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task McpGetDocument_DeniedAtlassianDoc_ReturnsNotFound()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid containerId = await SeedAsync(sp);
        string denied = await DocumentIdAsync(sp, containerId, "denied-page.md");
        string allowed = await DocumentIdAsync(sp, containerId, "allowed-page.md");
        Guid userId = await SeedUserAsync(sp, AllowedAccount);

        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
        };
        try
        {
            string deniedResult = await McpTools.GetDocument(sp, containerId.ToString(), denied);
            string missingResult = await McpTools.GetDocument(sp, containerId.ToString(), Guid.Empty.ToString());
            string allowedResult = await McpTools.GetDocument(sp, containerId.ToString(), allowed);

            deniedResult.Should().Be(missingResult.Replace(Guid.Empty.ToString(), denied));
            allowedResult.Should().NotContain("not found in this container",
                "the allowed page passes the guard (its file was never stored, so the read fails later)");
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public async Task RestGetDocument_DeniedAtlassianDoc_Returns404()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid containerId = await SeedAsync(sp);
        string denied = await DocumentIdAsync(sp, containerId, "denied-page.md");
        string allowed = await DocumentIdAsync(sp, containerId, "allowed-page.md");
        string upload = await DocumentIdAsync(sp, containerId, "upload.md");
        using var client = await ViewerClientAsync(sp, AllowedAccount);
        string Files(string id) => $"/api/containers/{containerId}/files/{id}";

        var deniedResponse = await client.GetAsync(Files(denied));
        var missingResponse = await client.GetAsync(Files(Guid.Empty.ToString()));
        deniedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await BodyAsync(deniedResponse)).Should()
            .Be((await BodyAsync(missingResponse)).Replace(Guid.Empty.ToString(), denied));
        (await client.GetAsync(Files(denied) + "/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await client.GetAsync(Files(allowed))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(Files(upload))).StatusCode.Should().Be(HttpStatusCode.OK,
            "a document with no address is readable as before");
    }

    [Fact]
    public async Task Sources_NonAdmin_DoesNotListConfluenceSources()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await SeedAsync(sp);
        var connection = (await sp.GetRequiredService<IConnectionStore>().ListAsync(take: int.MaxValue))
            .Single(c => c.Name == $"atl-{_cloudId}");
        var source = await sp.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"conf-{Guid.NewGuid():N}"[..20], connection.Id, """{"kind":"confluence-space","spaceKey":"ENG"}"""));
        using var viewer = await ViewerClientAsync(sp, AllowedAccount);

        (await viewer.GetAsync($"/api/sources/{source.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await viewer.GetStringAsync("/api/sources?take=100")).Should().NotContain(source.Id.ToString());
        (await fixture.AdminClient.GetAsync($"/api/sources/{source.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>A Confluence-space source holding an allowed and a denied page, as a sync would leave it.</summary>
    private async Task<Source> SeedConfluenceSourceAsync(IServiceProvider sp)
    {
        await SeedAsync(sp);
        var connection = (await sp.GetRequiredService<IConnectionStore>().ListAsync(take: int.MaxValue))
            .Single(c => c.Name == $"atl-{_cloudId}");
        var source = await sp.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
            $"conf-{Guid.NewGuid():N}"[..20], connection.Id, """{"kind":"confluence-space","spaceKey":"ENG"}""",
            Description: "secret-space-description"));

        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        foreach (var (name, pageId) in new[] { ("space-allowed.md", "101"), ("space-denied.md", "102") })
        {
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                SourceId = source.Id,
                FileName = name,
                Path = "/" + name,
                ResourceUri = AtlassianUri.ForPage(_cloudId, pageId),
                ContentHash = Guid.NewGuid().ToString("N"),
                IngestionStatus = DocumentStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            };
            db.Documents.Add(document);
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                OwnerId = source.Id,
                Content = $"the {Term} appears here",
                ChunkIndex = 0,
                Metadata = [],
            });
        }

        await db.SaveChangesAsync();
        return source;
    }

    /// <summary>Runs <paramref name="call"/> as a non-admin user linked to <paramref name="atlassianAccount"/>.</summary>
    private static async Task<T> AsUserAsync<T>(IServiceProvider sp, string? atlassianAccount, Func<Task<T>> call)
    {
        Guid userId = await SeedUserAsync(sp, atlassianAccount);
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
        };
        try
        {
            return await call();
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static Task<string> McpSearchAsync(IServiceProvider sp, string scope) =>
        McpTools.SearchKnowledge(sp, Term, scope, mode: "Keyword", topK: 20);

    [Fact]
    public async Task McpSearch_ConfluenceSourceById_LinkedAllowedNonAdmin_SeesPermittedPage()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var source = await SeedConfluenceSourceAsync(sp);

        string result = await AsUserAsync(sp, AllowedAccount, () => McpSearchAsync(sp, source.Id.ToString()));

        result.Should().Contain("space-allowed.md").And.NotContain("space-denied.md")
            .And.NotContain(source.Name).And.NotContain("secret-space-description");
    }

    [Theory]
    [InlineData(DeniedAccount)]
    [InlineData(null)]
    public async Task McpSearch_ConfluenceSourceById_DeniedOrUnlinkedNonAdmin_AnswersAsMissing(string? atlassianAccount)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var source = await SeedConfluenceSourceAsync(sp);
        string missingId = Guid.NewGuid().ToString();

        string result = await AsUserAsync(sp, atlassianAccount, () => McpSearchAsync(sp, source.Id.ToString()));
        string missing = await AsUserAsync(sp, atlassianAccount, () => McpSearchAsync(sp, missingId));

        result.Should().Be(missing.Replace(missingId, source.Id.ToString()),
            "a space the caller can read nothing in must not be told apart from one that does not exist");
        result.Should().NotContain("space-").And.NotContain(source.Name).And.NotContain("secret-space-description");
    }

    [Fact]
    public async Task Mcp_ConfluenceSource_StaysUndiscoverableToNonAdmin()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var source = await SeedConfluenceSourceAsync(sp);

        string list = await AsUserAsync(sp, AllowedAccount, () => McpTools.ContainerList(sp));
        string describe = await AsUserAsync(sp, AllowedAccount, () => McpTools.ContainerDescribe(sp, source.Id.ToString()));
        string byName = await AsUserAsync(sp, AllowedAccount, () => McpSearchAsync(sp, source.Name));

        list.Should().NotContain(source.Id.ToString()).And.NotContain(source.Name);
        describe.Should().Contain("not found").And.NotContain("secret-space-description");
        byName.Should().Contain("not found", "a hidden source is searchable by id only, so a name cannot probe for it")
            .And.NotContain("space-allowed.md");
    }

    [Fact]
    public async Task DirectRead_AzureNotEnforcing_ReadsAzblobDocs()
    {
        // The guard runs the whole composite, so Azure's verifier now also governs direct reads. When
        // Azure is not enforcing it must pass every document, as search does.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid containerId = await SeedAsync(sp);
        var azure = AzureVerifyEnforcementTests.BuildVerifier(sp.GetRequiredService<IDocumentStore>(), azureEnforcing: false, []);
        var atlassian = sp.GetServices<IPerSchemeResultVerifier>().OfType<AtlassianSearchResultVerifier>().Single();
        var documents = sp.GetRequiredService<IDocumentStore>();
        var guard = new Connapse.Web.Services.DocumentReadGuard(new CompositeSearchResultVerifier([azure, atlassian]),
            documents, Microsoft.Extensions.Logging.Abstractions.NullLogger<Connapse.Web.Services.DocumentReadGuard>.Instance);
        Guid user = AzureVerifyEnforcementTests.TestUser;

        async Task<Document> DocumentAsync(string name) =>
            (await documents.GetAsync(await DocumentIdAsync(sp, containerId, name)))!;

        foreach (string name in new[] { "azure-granted.md", "azure-ungranted.md", "upload.md" })
            (await guard.CanReadAsync(user, await DocumentAsync(name), default)).Should().BeTrue(name);
        (await guard.CanReadAsync(user, await DocumentAsync("allowed-page.md"), default))
            .Should().BeFalse("the user has no Atlassian link");
    }

    // ── Jira ──────────────────────────────────────────────────────────────

    private const string JiraGroup = "6e87dc72-4f1f-421f-9382-2fee8b652487";

    private int JiraCalls => fixture.Atlassian.Calls
        .Where(c => c.Key.StartsWith($"/ex/jira/{_cloudId}/", StringComparison.Ordinal)).Sum(c => c.Value);

    /// <summary>
    /// Issue 30001, a comment restricted to <see cref="JiraGroup"/> and an internal note.
    /// <see cref="AllowedAccount"/> browses the issue, is an agent and is in the group;
    /// <see cref="DeniedAccount"/> is a customer who only browses it.
    /// </summary>
    private async Task<Guid> SeedJiraAsync(IServiceProvider sp, params (string Name, string? Uri)[] more)
    {
        var jira = new Connapse.Core.Tests.Connectors.FakeJira(fixture.Atlassian, _cloudId).MapEnforcement();
        jira.Browse(AllowedAccount, "30001");
        jira.Agent(AllowedAccount, "30001");
        jira.Groups(AllowedAccount, ("engineering", JiraGroup));
        jira.Browse(DeniedAccount, "30001");
        // Jira still gives each comment the visibility its URI carries.
        jira.LiveComment("30001", "501", new { type = "group", identifier = JiraGroup });
        jira.LiveComment("30001", "502", jsdPublic: false);

        await sp.GetRequiredService<IConnectionStore>().CreateAsync(new CreateConnectionRequest(
            $"atl-{_cloudId}", ConnectionProvider.Atlassian,
            $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{_cloudId}}","clientId":"client-{{_cloudId}}","product":"jira"}""",
            "service-secret"), null);

        await using var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new (string, string?)[]
                 {
                     ("jira-issue.md", AtlassianUri.ForJiraIssue(_cloudId, "30001")),
                     ("jira-group-comment.md", AtlassianUri.ForJiraComment(_cloudId, "30001", "501", false,
                         new JiraRestriction(JiraRestrictionType.Group, JiraGroup))),
                     ("jira-internal-note.md", AtlassianUri.ForJiraComment(_cloudId, "30001", "502", true, null)),
                     ("upload.md", null),
                 }.Concat(more))
        {
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                ContainerId = container.Id,
                FileName = name,
                Path = "/" + name,
                ResourceUri = uri,
                ContentHash = Guid.NewGuid().ToString("N"),
                IngestionStatus = DocumentStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            };
            db.Documents.Add(document);
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                OwnerId = container.Id,
                Content = $"the {Term} appears here",
                ChunkIndex = 0,
                Metadata = [],
            });
        }

        await db.SaveChangesAsync();
        return container.Id;
    }

    private async Task<IEnumerable<string?>> SearchJiraAsync(string? atlassianAccount)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await SearchAsync(await SeedJiraAsync(scope.ServiceProvider), atlassianAccount);
    }

    [Fact]
    public async Task Search_JiraLinkedAllowed_SeesIssueRestrictedCommentAndNote()
    {
        var names = await SearchJiraAsync(AllowedAccount);

        names.Should().Contain(["jira-issue.md", "jira-group-comment.md", "jira-internal-note.md", "upload.md"]);
    }

    [Fact]
    public async Task Search_JiraCommentRestrictionChangedInJira_DeniedToTheOldGroup()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid containerId = await SeedJiraAsync(scope.ServiceProvider,
            ("jira-regrouped-comment.md", AtlassianUri.ForJiraComment(_cloudId, "30001", "504", false,
                new JiraRestriction(JiraRestrictionType.Group, JiraGroup))));
        // Restricted to another group in Jira since the last sync; the URI still names the old one.
        new Connapse.Core.Tests.Connectors.FakeJira(fixture.Atlassian, _cloudId)
            .LiveComment("30001", "504", new { type = "group", identifier = "0b5e1b8e-56b0-4a35-9b8a-3a6b3c0c9f11" });

        var names = await SearchAsync(containerId, AllowedAccount);

        names.Should().Contain(["jira-issue.md", "jira-group-comment.md"]).And.NotContain("jira-regrouped-comment.md");
    }

    [Fact]
    public async Task Search_JiraLinkedDenied_SeesOnlyTheIssue()
    {
        var names = await SearchJiraAsync(DeniedAccount);

        names.Should().Contain(["jira-issue.md", "upload.md"])
            .And.NotContain(["jira-group-comment.md", "jira-internal-note.md"]);
        JiraCalls.Should().BeGreaterThan(0, "a linked user's Jira hits are checked live");
    }

    [Fact]
    public async Task Search_JiraUnlinked_SeesNoneAndMakesNoCalls()
    {
        var names = await SearchJiraAsync(atlassianAccount: null);

        names.Should().NotContain(["jira-issue.md", "jira-group-comment.md", "jira-internal-note.md"])
            .And.Contain("upload.md");
        JiraCalls.Should().Be(0);
    }

    /// <summary>
    /// Syncs issue 30001 with <see cref="JiraProjectConnector"/> against a Jira of its own and returns
    /// the URIs it lists: a comment restricted to the role <c>QA/Ops Team</c>, an attachment linked
    /// from it, and an attachment no restricted comment links.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ConnectorJiraUrisAsync()
    {
        using var api = new Connapse.Core.Tests.Connectors.FakeAtlassianApi();
        var jira = new Connapse.Core.Tests.Connectors.FakeJira(api, _cloudId).MapSync();
        jira.AddProject(Connapse.Core.Tests.Connectors.FakeJira.ProjectId, "ENG", "Engineering");
        var issue = jira.AddIssue(new Connapse.Core.Tests.Connectors.FakeJiraIssue("30001", "ENG-1", "Rollout", DateTimeOffset.UtcNow));
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        jira.Attach(issue, new Connapse.Core.Tests.Connectors.FakeJiraAttachment("40001", "qa.txt", "qa notes"u8.ToArray(), at, null));
        jira.Attach(issue, new Connapse.Core.Tests.Connectors.FakeJiraAttachment("40002", "open.txt", "open notes"u8.ToArray(), at, null));
        issue.Comments.Add(new Connapse.Core.Tests.Connectors.FakeJiraComment("503", "Cy", "QA only", at,
            Visibility: new { type = "role", value = "QA/Ops Team", identifier = "QA/Ops Team" },
            RenderedBody: "<a href=\"https://acme.atlassian.net/rest/api/3/attachment/content/40001\">qa.txt</a>"));
        jira.ServeComments(issue);

        var site = new AtlassianSite("https://acme.atlassian.net", _cloudId, "client-" + _cloudId);
        string root = Path.Combine(Path.GetTempPath(), "jira-enforcement-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connector = new JiraProjectConnector(
                new JiraProjectConfig(site, Connapse.Core.Tests.Connectors.FakeJira.ProjectId, "ENG", root),
                new AtlassianApiClient(api.CreateClient(), new AtlassianTokenSource(new SingleHandlerFactory(api), TimeProvider.System),
                    site, "secret", product: AtlassianProduct.Jira),
                fileTypes: new TextFiles());
            var delta = await connector.GetChangesAsync(null);
            return delta.Upserted.ToDictionary(f => f.Path, f => f.ResourceUri!);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TextFiles : IFileTypeValidator
    {
        public bool IsSupported(string fileName) => fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".txt" };
    }

    /// <summary>The role comment and both attachments, under the URIs the connector gave them; the allowed account is in the role.</summary>
    private async Task<IEnumerable<string?>> SearchJiraRoleAndAttachmentsAsync(string atlassianAccount)
    {
        var uris = await ConnectorJiraUrisAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid containerId = await SeedJiraAsync(scope.ServiceProvider,
            ("jira-role-comment.md", uris["/issues/30001/comments/503.md"]),
            ("jira-role-attachment.txt", uris["/issues/30001/attachments/40001/qa.txt"]),
            ("jira-open-attachment.txt", uris["/issues/30001/attachments/40002/open.txt"]));
        var jira = new Connapse.Core.Tests.Connectors.FakeJira(fixture.Atlassian, _cloudId);
        jira.Role(Connapse.Core.Tests.Connectors.FakeJira.ProjectId, "10007", "QA/Ops Team",
            Connapse.Core.Tests.Connectors.FakeJira.UserActor(AllowedAccount));
        jira.LiveComment("30001", "503", new { type = "role", value = "QA/Ops Team" });
        jira.LiveIssueProject("30001", Connapse.Core.Tests.Connectors.FakeJira.ProjectId);
        return await SearchAsync(containerId, atlassianAccount);
    }

    [Fact]
    public async Task Search_JiraRoleMember_SeesRoleCommentAndItsAttachment()
    {
        var names = await SearchJiraRoleAndAttachmentsAsync(AllowedAccount);

        names.Should().Contain(["jira-role-comment.md", "jira-role-attachment.txt", "jira-open-attachment.txt"]);
    }

    [Fact]
    public async Task Search_JiraNotInRole_SeesOnlyTheUntiedAttachment()
    {
        var names = await SearchJiraRoleAndAttachmentsAsync(DeniedAccount);

        names.Should().Contain(["jira-issue.md", "jira-open-attachment.txt"])
            .And.NotContain(["jira-role-comment.md", "jira-role-attachment.txt"]);
    }

    [Fact]
    public async Task McpGetDocument_DeniedJiraComment_ReturnsNotFound()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid containerId = await SeedJiraAsync(sp);
        string denied = await DocumentIdAsync(sp, containerId, "jira-group-comment.md");
        string allowed = await DocumentIdAsync(sp, containerId, "jira-issue.md");

        var (deniedResult, missingResult, allowedResult) = await AsUserAsync(sp, DeniedAccount, async () => (
            await McpTools.GetDocument(sp, containerId.ToString(), denied),
            await McpTools.GetDocument(sp, containerId.ToString(), Guid.Empty.ToString()),
            await McpTools.GetDocument(sp, containerId.ToString(), allowed)));

        deniedResult.Should().Be(missingResult.Replace(Guid.Empty.ToString(), denied));
        allowedResult.Should().NotContain("not found in this container");
    }

    [Fact]
    public async Task RestGetDocument_DeniedJiraCommentAndNote_Return404()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid containerId = await SeedJiraAsync(sp);
        string comment = await DocumentIdAsync(sp, containerId, "jira-group-comment.md");
        string note = await DocumentIdAsync(sp, containerId, "jira-internal-note.md");
        string issue = await DocumentIdAsync(sp, containerId, "jira-issue.md");
        using var client = await ViewerClientAsync(sp, DeniedAccount);
        string Files(string id) => $"/api/containers/{containerId}/files/{id}";

        var deniedResponse = await client.GetAsync(Files(comment));
        var missingResponse = await client.GetAsync(Files(Guid.Empty.ToString()));
        deniedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await BodyAsync(deniedResponse)).Should()
            .Be((await BodyAsync(missingResponse)).Replace(Guid.Empty.ToString(), comment));
        (await client.GetAsync(Files(comment) + "/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync(Files(note))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await client.GetAsync(Files(issue))).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
