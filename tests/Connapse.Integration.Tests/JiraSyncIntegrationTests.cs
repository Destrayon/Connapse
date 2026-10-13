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

    [Fact]
    public async Task JiraSync_CreatedViaApi_IssuesSearchableByAllowedUser()
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
            new { siteUrl = $"https://{host}", clientId = $"client-{_cloudId}", clientSecret = "jira-sync-secret" });
        site.StatusCode.Should().Be(HttpStatusCode.Created, await site.Content.ReadAsStringAsync());
        Guid connectionId = (await site.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var created = await fixture.AdminClient.PostAsJsonAsync("/api/sources", new
        {
            connectionId,
            scopeJson = JsonSerializer.Serialize(new { kind = "jira-project", projectId = FakeJira.ProjectId }),
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        Guid sourceId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

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
        var service = new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(), factory, queue,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

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
}
