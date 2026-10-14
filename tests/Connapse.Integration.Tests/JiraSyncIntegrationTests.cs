using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// A Jira project source created through the REST API and synced end to end with the app's own
/// connector factory, with Jira itself the in-memory <see cref="FakeJira"/>: what it indexes is
/// found by a user Jira lets browse the issue, and by no one else.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class JiraSyncIntegrationTests(SharedWebAppFixture fixture)
{
    private const string Term = "quorvantilux";
    private const string AllowedAccount = "557058:jira-allowed";
    private const string DeniedAccount = "557058:jira-denied";

    // A site of its own, so routes and cached answers never leak between tests.
    private readonly string _cloudId = Guid.NewGuid().ToString("D");

    private static async Task<Guid> SeedUserAsync(IServiceProvider sp, string atlassianAccount)
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
        await sp.GetRequiredService<AtlassianIdentityLinkStore>().SaveAsync(user.Id, atlassianAccount);
        return user.Id;
    }

    /// <summary>A site saved through its test and a Jira project source on it, both created as an admin would.</summary>
    private async Task<(Guid SourceId, Guid ConnectionId, FakeJira Jira)> CreateJiraSourceAsync()
    {
        var api = fixture.Atlassian;
        api.FailTokenWith(null);
        api.AnswerTokenWith(null);
        api.FailWith(null);
        api.MapJson(FakeAtlassianApi.TenantInfoPath, new { cloudId = _cloudId });
        var jira = new FakeJira(api, _cloudId).MapSiteTest();

        // Created as an admin would: the site through its test, the source through POST /api/sources.
        string host = $"jira-sync-{Guid.NewGuid():N}"[..20] + ".atlassian.net";
        var site = await fixture.AdminClient.PostAsJsonAsync("/api/v1/atlassian/sites",
            new { siteUrl = $"https://{host}", clientId = $"client-{_cloudId}", clientSecret = "jira-sync-secret", product = "jira" });
        site.StatusCode.Should().Be(HttpStatusCode.Created, await site.Content.ReadAsStringAsync());
        Guid connectionId = (await site.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var created = await fixture.AdminClient.PostAsJsonAsync("/api/sources", new
        {
            connectionId,
            scopeJson = JsonSerializer.Serialize(new { kind = "jira-project", projectId = FakeJira.ProjectId }),
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        Guid sourceId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (sourceId, connectionId, jira);
    }

    private static SourceSyncService NewSyncService(IServiceProvider sp, IIngestionQueue queue) => new(
        sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IConnectorFactory>(), queue,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

    [Fact]
    public async Task JiraSync_CreatedViaApi_IssuesSearchableByAllowedUser()
    {
        var (sourceId, connectionId, jira) = await CreateJiraSourceAsync();
        jira.MapSync().MapEnforcement();
        jira.AddProject(FakeJira.ProjectId, "ENG", "Engineering");
        jira.AddIssue(new FakeJiraIssue("30001", "ENG-1", $"The {Term} rollout", DateTimeOffset.UtcNow.AddMinutes(-5)));
        jira.Browse(AllowedAccount, "30001");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var source = (await sp.GetRequiredService<ISourceStore>().GetAsync(sourceId))!;
        var connections = sp.GetRequiredService<IConnectionStore>();
        var connection = (await connections.GetAsync(connectionId))!;

        var factory = sp.GetRequiredService<IConnectorFactory>();
        var queue = new RecordingIngestionQueue();
        var service = NewSyncService(sp, queue);

        (await service.SyncSourceAsync(source, connection, CancellationToken.None)).Error.Should().BeNull();
        var job = queue.Jobs.Single(j => j.Options.Path == "/issues/30001.md");

        var connector = factory.Create(source, connection, await connections.GetSecretAsync(connectionId));
        await using (var content = await connector.ReadFileAsync(job.Options.Path))
            await sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(content, job.Options, CancellationToken.None);

        async Task<List<SearchHit>> SearchAsUserAsync(string account) =>
            (await sp.GetRequiredService<IKnowledgeSearch>().SearchAsync(Term, new SearchOptions(
                TopK: 20, ContainerId: sourceId.ToString(), Mode: SearchMode.Keyword, UserId: await SeedUserAsync(sp, account)))).Hits;

        (await SearchAsUserAsync(AllowedAccount)).Select(h => h.DocumentId).Should().Contain(job.DocumentId);
        (await SearchAsUserAsync(DeniedAccount)).Should().BeEmpty("Jira does not let this account browse the issue");
    }

    /// <summary>
    /// An ordinary cycle after the first is incremental: no id listing, the changed issue re-queued,
    /// and the comment document it no longer has, made public, deleted through the delta's paths.
    /// </summary>
    [Fact]
    public async Task JiraSync_IncrementalCycle_RequeuesTheChangedIssueAndDeletesItsGoneComment()
    {
        var (sourceId, connectionId, jira) = await CreateJiraSourceAsync();
        jira.MapSync();
        jira.AddProject(FakeJira.ProjectId, "ENG", "Engineering");
        DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-5);
        var issue = jira.AddIssue(new FakeJiraIssue("30002", "ENG-2", $"The {Term} plan", created));
        issue.Comments.Add(new FakeJiraComment("41", "Bob", "Was secret", created,
            Visibility: new { type = "group", value = "admins", identifier = "6e87dc72-4f1f-421f-9382-2fee8b652487" }));
        jira.ServeComments(issue);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var sources = sp.GetRequiredService<ISourceStore>();
        var connection = (await sp.GetRequiredService<IConnectionStore>().GetAsync(connectionId))!;
        var queue = new RecordingIngestionQueue();
        var service = NewSyncService(sp, queue);

        (await service.SyncSourceAsync((await sources.GetAsync(sourceId))!, connection, CancellationToken.None)).Error.Should().BeNull();
        queue.Jobs.Select(j => j.Options.Path).Should().BeEquivalentTo("/issues/30002.md", "/issues/30002/comments/41.md");
        await using (var db = await sp.GetRequiredService<IDbContextFactory<Connapse.Storage.Data.KnowledgeDbContext>>().CreateDbContextAsync())
        {
            // Settled as the pipeline would leave them, so a changed signature is what re-queues a document.
            foreach (var job in queue.Jobs)
            {
                var document = await db.Documents.SingleAsync(d => d.Id == Guid.Parse(job.DocumentId));
                document.IngestionStatus = DocumentStatus.Ready;
                document.Metadata = new Dictionary<string, string>
                {
                    [SourceSyncService.RemoteLastModifiedKey] = job.Options.Metadata![SourceSyncService.RemoteLastModifiedKey],
                    [SourceSyncService.RemoteSizeKey] = job.Options.Metadata[SourceSyncService.RemoteSizeKey],
                };
            }
            await db.SaveChangesAsync();
        }
        queue.Jobs.Clear();

        // Made public, which moves the issue's updated time: the comment belongs inline now.
        DateTimeOffset edited = DateTimeOffset.UtcNow;
        issue.Comments[0] = issue.Comments[0] with { Updated = edited, Visibility = null };
        issue.Updated = edited;
        jira.Jqls.Clear();
        var second = await service.SyncSourceAsync((await sources.GetAsync(sourceId))!, connection, CancellationToken.None);

        second.Error.Should().BeNull();
        second.Deleted.Should().Be(1);
        jira.Jqls.Should().ContainSingle().Which.Should().Contain("updated >=", "an incremental cycle makes no id listing");
        queue.Jobs.Select(j => j.Options.Path).Should().Equal("/issues/30002.md");
        await using (var db = await sp.GetRequiredService<IDbContextFactory<Connapse.Storage.Data.KnowledgeDbContext>>().CreateDbContextAsync())
        {
            (await db.Documents.Where(d => d.SourceId == sourceId).Select(d => d.Path).ToListAsync())
                .Should().Equal("/issues/30002.md");
        }
        (await sources.GetAsync(sourceId))!.SyncCursor.Should().Contain("lastReconcileAt");
    }

    /// <summary>
    /// A Jira source whose requests are refused even with a fresh token says so in Jira's words,
    /// links Jira's troubleshooting entry, and records the failure at the site's Jira step.
    /// </summary>
    [Fact]
    public async Task JiraSync_RefusedWithAFreshToken_ReportsJiraNotConfluence()
    {
        var (sourceId, connectionId, jira) = await CreateJiraSourceAsync();
        jira.MapSync();
        jira.AddProject(FakeJira.ProjectId, "ENG", "Engineering");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var source = (await sp.GetRequiredService<ISourceStore>().GetAsync(sourceId))!;
        var connections = sp.GetRequiredService<IConnectionStore>();
        var connection = (await connections.GetAsync(connectionId))!;

        fixture.Atlassian.FailWith(HttpStatusCode.Unauthorized);
        SourceSyncResult failed;
        try
        {
            failed = await NewSyncService(sp, new RecordingIngestionQueue()).SyncSourceAsync(source, connection, CancellationToken.None);
        }
        finally
        {
            fixture.Atlassian.FailWith(null);
        }

        failed.Error.Should().StartWith(Connapse.Storage.Connectors.Atlassian.AtlassianSyncErrors.JiraFreshTokenRefused);
        Connapse.Storage.Connectors.Atlassian.AtlassianSyncErrors.TroubleshootingUrl(failed.Error!).Should().EndWith("#cant-read-jira");
        var health = (await connections.GetAsync(connectionId))!.Health!;
        health.Step.Should().Be(Connapse.Storage.ConnectionTesters.AtlassianConnectionTester.JiraIdentityStep);
        health.CredentialRejected.Should().BeFalse();
        Connapse.Storage.ConnectionTesters.AtlassianConnectionTester.StepLabel(health.Step).Should().Be("Reading Jira");
    }
}
