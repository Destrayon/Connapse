using System.Net;
using System.Text.Json;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public sealed class AtlassianApiClientTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string Root = "/ex/confluence/" + CloudId + "/wiki";

    private readonly FakeAtlassianApi _api = new();
    private readonly ManualClock _clock = new();
    private readonly AtlassianSite _site = new("https://acme.atlassian.net", CloudId, "client-1");

    public void Dispose() => _api.Dispose();

    private AtlassianApiClient NewClient(AtlassianTokenSource? tokens = null) =>
        new(_api.CreateClient(), tokens ?? NewTokens(), _site, "secret", _clock);

    private AtlassianTokenSource NewTokens() => new(new FakeFactory(_api), _clock);

    [Fact]
    public async Task GetJsonAsync_FirstCall_FetchesTokenOnce()
    {
        _api.MapJson(Root + "/api/v2/spaces/1", new { id = "1" });
        var client = NewClient();

        await client.GetJsonAsync<JsonElement>("api/v2/spaces/1", default);
        await client.GetJsonAsync<JsonElement>("api/v2/spaces/1", default);

        _api.TokenRequests.Should().Be(1);
        _api.BearerTokens.Should().AllBe("token-1");
        _api.Requests.Last().Host.Should().Be("api.atlassian.com");
    }

    [Fact]
    public async Task GetJsonAsync_ConcurrentFirstCalls_ShareOneTokenFetch()
    {
        _api.MapJson(Root + "/api/v2/spaces/1", new { id = "1" });
        var client = NewClient();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetJsonAsync<JsonElement>("api/v2/spaces/1", default)));

        _api.TokenRequests.Should().Be(1);
    }

    [Fact]
    public async Task GetJsonAsync_TokenNearExpiry_Refreshes()
    {
        _api.TokenExpiresIn = 400; // usable for 100 s once the 300 s margin is taken off
        _api.MapJson(Root + "/api/v2/spaces/1", new { id = "1" });
        var client = NewClient();

        await client.GetJsonAsync<JsonElement>("api/v2/spaces/1", default);
        _clock.Advance(TimeSpan.FromSeconds(50));
        await client.GetJsonAsync<JsonElement>("api/v2/spaces/1", default);
        _api.TokenRequests.Should().Be(1);

        _clock.Advance(TimeSpan.FromSeconds(60));
        await client.GetJsonAsync<JsonElement>("api/v2/spaces/1", default);

        _api.TokenRequests.Should().Be(2);
        _api.BearerTokens.Last().Should().Be("token-2");
    }

    [Fact]
    public async Task GetJsonAsync_401ThenOk_RetriesOnceWithFreshToken()
    {
        _api.MapJson(Root + "/api/v2/spaces/1", new { id = "1" });
        var client = NewClient();
        await client.GetJsonAsync<JsonElement>("api/v2/spaces/1", default);

        _api.RevokeTokens();
        var result = await client.GetJsonAsync<JsonElement>("api/v2/spaces/1", default);

        result.GetProperty("id").GetString().Should().Be("1");
        _api.TokenRequests.Should().Be(2);
        _api.BearerTokens.Should().Equal("token-1", "token-1", "token-2");
    }

    [Fact]
    public async Task Invalidate_StaleCallerResumesAfterRefresh_KeepsTheFreshToken()
    {
        var tokens = NewTokens();
        string first = await tokens.GetTokenAsync(_site, "secret", default);

        // A stale 401 handler matches token-1 and then stalls; meanwhile another handler drops
        // token-1 and refreshes to token-2. When the stale one resumes it must not clear token-2.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim();
        int calls = 0;
        tokens.BetweenInvalidateCompareAndClear = () =>
        {
            if (Interlocked.Increment(ref calls) != 1)
                return;
            reached.SetResult();
            proceed.Wait(TimeSpan.FromSeconds(30));
        };

        Task stale = Task.Run(() => tokens.Invalidate(_site, first));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        tokens.Invalidate(_site, first);
        string second = await tokens.GetTokenAsync(_site, "secret", default);
        second.Should().Be("token-2");

        proceed.Set();
        await stale.WaitAsync(TimeSpan.FromSeconds(30));

        (await tokens.GetTokenAsync(_site, "secret", default)).Should().Be("token-2");
        _api.TokenRequests.Should().Be(2);
    }

    [Fact]
    public async Task GetJsonAsync_Persistent401_ThrowsAuthExceptionAfterOneRetry()
    {
        _api.Map(Root + "/api/v2/spaces/1", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await FluentActions.Awaiting(() => NewClient().GetJsonAsync<JsonElement>("api/v2/spaces/1", default))
            .Should().ThrowAsync<AtlassianAuthException>();

        _api.TokenRequests.Should().Be(2);
        _api.Calls[Root + "/api/v2/spaces/1"].Should().Be(2);
    }

    [Theory]
    [InlineData(AtlassianProduct.Confluence, "/ex/confluence/" + CloudId + "/wiki/rest/api/x", AtlassianSyncErrors.FreshTokenRefused)]
    [InlineData(AtlassianProduct.Jira, "/ex/jira/" + CloudId + "/rest/api/x", AtlassianSyncErrors.JiraFreshTokenRefused)]
    public async Task GetJsonAsync_Persistent401_MessageNamesTheClientsProduct(AtlassianProduct product, string path, string message)
    {
        _api.Map(path, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new AtlassianApiClient(_api.CreateClient(), NewTokens(), _site, "secret", _clock, product: product);

        (await FluentActions.Awaiting(() => client.GetJsonAsync<JsonElement>("rest/api/x", default))
            .Should().ThrowAsync<AtlassianAuthException>()).Which.Message.Should().Be(message);
    }

    [Fact]
    public async Task GetJsonAsync_TokenEndpointRefuses_ThrowsAuthException()
    {
        _api.FailTokenWith(HttpStatusCode.Unauthorized);

        await FluentActions.Awaiting(() => NewClient().GetJsonAsync<JsonElement>("api/v2/spaces/1", default))
            .Should().ThrowAsync<AtlassianAuthException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task GetJsonAsync_TokenEndpointRedirects_ThrowsAuthException(HttpStatusCode status)
    {
        _api.FailTokenWith(status);

        await FluentActions.Awaiting(() => NewClient().GetJsonAsync<JsonElement>("api/v2/spaces/1", default))
            .Should().ThrowAsync<AtlassianAuthException>();
    }

    [Fact]
    public async Task GetJsonAsync_429_ThrowsRateLimitedWithRetryAfter()
    {
        _api.MapJson(Root + "/api/v2/spaces/1", new { id = "1" });
        _api.RateLimitNext = true;

        var thrown = await FluentActions.Awaiting(() => NewClient().GetJsonAsync<JsonElement>("api/v2/spaces/1", default))
            .Should().ThrowAsync<AtlassianRateLimitedException>();

        thrown.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task PageAsync_FollowsNextLinks_UntilAbsent()
    {
        _api.Map(Root + "/api/v2/spaces", request => request.RequestUri!.Query.Contains("cursor=b")
            ? FakeAtlassianApi.Json(new { results = new[] { new { id = "3" } } })
            : request.RequestUri.Query.Contains("cursor=a")
                // Page 2 advertises its successor only in the Link header.
                ? WithLink(FakeAtlassianApi.Json(new { results = new[] { new { id = "2" } } }), "</wiki/api/v2/spaces?cursor=b>; rel=\"next\"")
                : FakeAtlassianApi.Json(new { results = new[] { new { id = "1" } }, _links = new { next = "/wiki/api/v2/spaces?cursor=a" } }));

        var ids = new List<string>();
        await foreach (string id in NewClient().PageAsync("api/v2/spaces",
            root => root.GetProperty("results").EnumerateArray().Select(e => e.GetProperty("id").GetString()!), default))
            ids.Add(id);

        ids.Should().Equal("1", "2", "3");
        _api.Calls[Root + "/api/v2/spaces"].Should().Be(3);
    }

    [Fact]
    public async Task PageAsync_RepeatedNextLink_Throws()
    {
        // Page b points back at page a: an endless walk unless the client notices.
        _api.Map(Root + "/api/v2/spaces", request => request.RequestUri!.Query.Contains("cursor=b")
            ? FakeAtlassianApi.Json(new { results = new[] { new { id = "2" } }, _links = new { next = "/wiki/api/v2/spaces?cursor=a" } })
            : FakeAtlassianApi.Json(new { results = new[] { new { id = "1" } }, _links = new { next = "/wiki/api/v2/spaces?cursor=b" } }));

        var ids = new List<string>();
        Func<Task> walk = async () =>
        {
            await foreach (string id in NewClient().PageAsync("api/v2/spaces",
                root => root.GetProperty("results").EnumerateArray().Select(e => e.GetProperty("id").GetString()!), default))
                ids.Add(id);
        };

        await walk.Should().ThrowAsync<InvalidOperationException>().WithMessage("*repeated*");
        _api.Calls[Root + "/api/v2/spaces"].Should().Be(3);
    }

    [Fact]
    public async Task PageAsync_V1RelativeNext_ResolvesAgainstTheWiki()
    {
        _api.Map(Root + "/rest/api/search", request => request.RequestUri!.Query.Contains("cursor=z")
            ? FakeAtlassianApi.Json(new { results = new[] { new { id = "2" } } })
            : FakeAtlassianApi.Json(new
            {
                results = new[] { new { id = "1" } },
                _links = new { @base = "https://acme.atlassian.net/wiki", next = "/rest/api/search?cursor=z" },
            }));

        var ids = new List<string>();
        await foreach (string id in NewClient().PageAsync("rest/api/search",
            root => root.GetProperty("results").EnumerateArray().Select(e => e.GetProperty("id").GetString()!), default))
            ids.Add(id);

        ids.Should().Equal("1", "2");
        _api.Requests.Should().OnlyContain(u => u.Host == "api.atlassian.com" || u.Host == "auth.atlassian.com");
    }

    [Fact]
    public async Task PageAsync_NextLinkOnForeignHost_Throws()
    {
        _api.MapJson(Root + "/api/v2/spaces", new
        {
            results = new[] { new { id = "1" } },
            _links = new { next = "https://evil.example/wiki/api/v2/spaces?cursor=a" },
        });

        var ids = new List<string>();
        Func<Task> walk = async () =>
        {
            await foreach (string id in NewClient().PageAsync("api/v2/spaces",
                root => root.GetProperty("results").EnumerateArray().Select(e => e.GetProperty("id").GetString()!), default))
                ids.Add(id);
        };

        await walk.Should().ThrowAsync<InvalidOperationException>();
        _api.Requests.Should().NotContain(u => u.Host == "evil.example");
    }

    [Fact]
    public async Task GetJsonAsync_TokenRequest_IsFormUrlEncoded()
    {
        _api.MapJson(Root + "/api/v2/spaces/1", new { id = "1" });

        await NewClient().GetJsonAsync<JsonElement>("api/v2/spaces/1", default);

        _api.TokenContentTypes.Should().Equal("application/x-www-form-urlencoded");
    }

    [Theory]
    [InlineData("/wiki/api/v2/spaces?cursor=a")]
    [InlineData("/rest/api/search?cursor=a")]
    public async Task PageAsync_RelativeNext_IsFollowedOnAnyOs(string next)
    {
        _api.Map(Root + "/api/v2/spaces", _ => FakeAtlassianApi.Json(new { results = new[] { 1 }, _links = new { next } }));
        string followed = next.StartsWith("/wiki/") ? Root + "/api/v2/spaces" : Root + "/rest/api/search";
        _api.Map(followed, request => request.RequestUri!.Query.Contains("cursor=a")
            ? FakeAtlassianApi.Json(new { results = new[] { 2 } })
            : FakeAtlassianApi.Json(new { results = new[] { 1 }, _links = new { next } }));

        var items = new List<int>();
        await foreach (int i in NewClient().PageAsync("api/v2/spaces",
            root => root.GetProperty("results").EnumerateArray().Select(e => e.GetInt32()), default))
            items.Add(i);

        items.Should().Equal(1, 2);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("\\\\evil\\x")]
    public async Task PageAsync_HostNamingNext_Throws(string next)
    {
        _api.MapJson(Root + "/api/v2/spaces", new { results = new[] { 1 }, _links = new { next } });

        Func<Task> walk = async () =>
        {
            await foreach (int _ in NewClient().PageAsync("api/v2/spaces",
                root => root.GetProperty("results").EnumerateArray().Select(e => e.GetInt32()), default)) { }
        };

        await walk.Should().ThrowAsync<InvalidOperationException>();
        _api.Requests.Should().OnlyContain(u => u.Host == "api.atlassian.com" || u.Host == "auth.atlassian.com");
    }

    [Theory]
    [InlineData(" http://api.atlassian.com" + Root + "/api/v2/spaces?cursor=a")]
    [InlineData("\thttp://api.atlassian.com" + Root + "/api/v2/spaces?cursor=a")]
    [InlineData(" https://api.atlassian.com:8443" + Root + "/api/v2/spaces?cursor=a")]
    [InlineData("http://api.atlassian.com" + Root + "/api/v2/spaces?cursor=a")]
    [InlineData("https://api.atlassian.com:8443" + Root + "/api/v2/spaces?cursor=a")]
    public async Task PageAsync_NextDowngradesSchemeOrPort_ThrowsWithoutSending(string next)
    {
        _api.Map(Root + "/api/v2/spaces", request => request.RequestUri!.Query.Contains("cursor=a")
            ? FakeAtlassianApi.Json(new { results = new[] { 2 } })
            : FakeAtlassianApi.Json(new { results = new[] { 1 }, _links = new { next } }));

        Func<Task> walk = async () =>
        {
            await foreach (int _ in NewClient().PageAsync("api/v2/spaces",
                root => root.GetProperty("results").EnumerateArray().Select(e => e.GetInt32()), default)) { }
        };

        await walk.Should().ThrowAsync<InvalidOperationException>();
        _api.Requests.Should().OnlyContain(u => u.Scheme == "https" && u.IsDefaultPort);
        _api.Calls[Root + "/api/v2/spaces"].Should().Be(1);
    }

    [Fact]
    public async Task PostAsync_ReturnsTheResponseForTheCallerToInspect()
    {
        _api.Map(Root + "/api/v2/check", _ => FakeAtlassianApi.Json(new { hasPermission = false }, HttpStatusCode.Forbidden));

        using var response = await NewClient().PostAsync("api/v2/check", new { subject = "x" }, default);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetStreamAsync_ReadsTheBody()
    {
        _api.Map(Root + "/download", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello") });

        await using var stream = await NewClient().GetStreamAsync("download", default);
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Be("hello");
    }

    [Theory]
    [InlineData("""{"siteUrl":"https://acme.atlassian.net","cloudId":"11111111-2222-3333-4444-555555555555","clientId":"c"}""", true)]
    [InlineData("""{"siteUrl":"https://acme.atlassian.net/","cloudId":"11111111-2222-3333-4444-555555555555","clientId":"c"}""", true)]
    [InlineData("""{"siteUrl":"https://acme.example.com","cloudId":"11111111-2222-3333-4444-555555555555","clientId":"c"}""", false)]
    [InlineData("""{"siteUrl":"https://acme.atlassian.net.evil.com","cloudId":"11111111-2222-3333-4444-555555555555","clientId":"c"}""", false)]
    [InlineData("""{"siteUrl":"http://acme.atlassian.net","cloudId":"11111111-2222-3333-4444-555555555555","clientId":"c"}""", false)]
    [InlineData("""{"siteUrl":"https://acme.atlassian.net","cloudId":"not-a-guid","clientId":"c"}""", false)]
    [InlineData("""{"siteUrl":"https://acme.atlassian.net","cloudId":"11111111-2222-3333-4444-555555555555"}""", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void FromConfigJson_Variants_ValidatesSiteAndCloudId(string? json, bool valid) =>
        (AtlassianSite.FromConfigJson(json) is not null).Should().Be(valid);

    [Fact]
    public void FromConfigJson_NonAtlassianHost_ReturnsNull() =>
        AtlassianSite.FromConfigJson($$"""{"siteUrl":"https://evil.example","cloudId":"{{CloudId}}","clientId":"c"}""")
            .Should().BeNull();

    [Fact]
    public void FromConfigJson_NoProduct_IsConfluence()
    {
        AtlassianSite.FromConfigJson($$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"c"}""")!
            .Product.Should().Be(AtlassianProduct.Confluence);
        AtlassianSite.FromConfigJson($$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"c","product":"jira"}""")!
            .Product.Should().Be(AtlassianProduct.Jira);
        AtlassianSite.FromConfigJson($$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"c","product":"confluence"}""")!
            .Product.Should().Be(AtlassianProduct.Confluence);
    }

    [Theory]
    [InlineData("\"bitbucket\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void FromConfigJson_UnknownProduct_Null(string product) =>
        AtlassianSite.FromConfigJson(
                $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"c","product":{{product}}}""")
            .Should().BeNull();

    private static HttpResponseMessage WithLink(HttpResponseMessage response, string link)
    {
        response.Headers.TryAddWithoutValidation("Link", link);
        return response;
    }

    [Fact]
    public async Task DownloadAsync_UsesTheDownloadClientForEveryHop()
    {
        _api.Confluence.Upsert(new FakeConfluencePage("1", "4001", "Page"));
        _api.Confluence.AddAttachment(new FakeConfluenceAttachment(
            "501", "1", "a.txt", "bytes"u8.ToArray(), DateTimeOffset.UnixEpoch));
        var counting = new CountingHandler(_api);
        var client = new AtlassianApiClient(
            _api.CreateClient(), NewTokens(), _site, "secret", _clock, downloads: new HttpClient(counting));

        await using var stream = await client.DownloadAsync("rest/api/content/1/child/attachment/att501/download", default);
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Be("bytes");
        counting.Hosts.Should().Equal("api.atlassian.com", FakeConfluence.MediaHost);
    }

    private const string JiraRoot = "/ex/jira/" + CloudId;

    private AtlassianApiClient NewJiraClient() =>
        new(_api.CreateClient(), NewTokens(), _site, "secret", _clock, product: AtlassianProduct.Jira);

    [Fact]
    public async Task Jira_RelativePath_PinnedToJiraSite()
    {
        _api.MapJson(JiraRoot + "/rest/api/3/myself", new { accountId = "a1" });

        await NewJiraClient().GetJsonAsync<JsonElement>("rest/api/3/myself", default);

        _api.Requests.Last().AbsolutePath.Should().Be(JiraRoot + "/rest/api/3/myself");
    }

    [Fact]
    public async Task Jira_AbsoluteLinkToConfluenceSite_Refused()
    {
        var client = NewJiraClient();

        Func<Task> act = () => client.GetJsonAsync<JsonElement>(
            "https://api.atlassian.com/ex/confluence/" + CloudId + "/wiki/api/v2/spaces", default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Jira_AbsoluteLinkToOtherCloud_Refused()
    {
        var client = NewJiraClient();

        Func<Task> act = () => client.GetJsonAsync<JsonElement>(
            "https://api.atlassian.com/ex/jira/99999999-2222-3333-4444-555555555555/rest/api/3/myself", default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Confluence_DefaultProduct_Unchanged()
    {
        _api.MapJson(Root + "/api/v2/spaces/1", new { id = "1" });
        var client = NewClient();

        await client.GetJsonAsync<JsonElement>("/wiki/api/v2/spaces/1", default);

        client.Product.Should().Be(AtlassianProduct.Confluence);
        _api.Requests.Last().AbsolutePath.Should().Be(Root + "/api/v2/spaces/1");
    }

    [Fact]
    public async Task PageByToken_FollowsTokenUntilAbsent()
    {
        _api.Map(JiraRoot + "/rest/api/3/search/jql", request =>
        {
            string query = request.RequestUri!.Query;
            return query.Contains("nextPageToken=t2")
                ? FakeAtlassianApi.Json(new { issues = new[] { new { id = "3" } }, isLast = true })
                : query.Contains("nextPageToken=t1")
                    ? FakeAtlassianApi.Json(new { issues = new[] { new { id = "2" } }, nextPageToken = "t2", isLast = false })
                    : FakeAtlassianApi.Json(new { issues = new[] { new { id = "1" } }, nextPageToken = "t1", isLast = false });
        });

        var ids = new List<string>();
        await foreach (string id in NewJiraClient().PageByTokenAsync(
            "rest/api/3/search/jql?jql=x", Issues, default))
            ids.Add(id);

        ids.Should().Equal("1", "2", "3");
        _api.Requests.Count(r => r.AbsolutePath.EndsWith("/search/jql")).Should().Be(3);
    }

    [Fact]
    public async Task PageByToken_RepeatedToken_Throws()
    {
        _api.MapJson(JiraRoot + "/rest/api/3/search/jql",
            new { issues = new[] { new { id = "1" } }, nextPageToken = "same", isLast = false });

        Func<Task> act = async () =>
        {
            await foreach (string _ in NewJiraClient().PageByTokenAsync("rest/api/3/search/jql", Issues, default)) { }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PageByOffset_StopsOnIsLast()
    {
        _api.Map(JiraRoot + "/rest/api/3/issue/K-1/comment", request => request.RequestUri!.Query.Contains("startAt=2")
            ? FakeAtlassianApi.Json(new { startAt = 2, maxResults = 2, total = 3, isLast = true, comments = new[] { new { id = "c3" }, new { id = "c4" } } })
            : FakeAtlassianApi.Json(new { startAt = 0, maxResults = 2, total = 4, isLast = false, comments = new[] { new { id = "c1" }, new { id = "c2" } } }));

        var ids = new List<string>();
        await foreach (string id in NewJiraClient().PageByOffsetAsync(
            "rest/api/3/issue/K-1/comment", "comments", e => e.GetProperty("id").GetString(), 2, default))
            ids.Add(id);

        ids.Should().Equal("c1", "c2", "c3", "c4");
        _api.Requests.Count(r => r.AbsolutePath.EndsWith("/comment")).Should().Be(2);
    }

    [Fact]
    public async Task PageByOffset_StopsOnEmptyPage()
    {
        _api.Map(JiraRoot + "/rest/api/3/project/search", request =>
        {
            string query = request.RequestUri!.Query;
            return query.Contains("startAt=3")
                ? FakeAtlassianApi.Json(new { values = Array.Empty<object>() })
                : query.Contains("startAt=2")
                    ? FakeAtlassianApi.Json(new { values = new[] { new { id = "p3" } } })
                    : FakeAtlassianApi.Json(new { values = new[] { new { id = "p1" }, new { id = "p2" } } });
        });

        var ids = new List<string>();
        await foreach (string id in NewJiraClient().PageByOffsetAsync(
            "rest/api/3/project/search?expand=x", "values", e => e.GetProperty("id").GetString(), 2, default))
            ids.Add(id);

        ids.Should().Equal("p1", "p2", "p3");
        _api.Requests.Count(r => r.AbsolutePath.EndsWith("/project/search")).Should().Be(3);
    }

    [Fact]
    public async Task PageByOffset_ServerCapsPageSize_ReadsEverything()
    {
        _api.Map(JiraRoot + "/rest/api/3/project/search", request =>
        {
            string query = request.RequestUri!.Query;
            int startAt = int.Parse(System.Text.RegularExpressions.Regex.Match(query, @"startAt=(\d+)").Groups[1].Value);
            var values = Enumerable.Range(startAt, Math.Min(50, 120 - startAt)).Select(i => new { id = "p" + i }).ToArray();
            return FakeAtlassianApi.Json(new { startAt, maxResults = 50, total = 120, values });
        });

        var ids = new List<string>();
        await foreach (string id in NewJiraClient().PageByOffsetAsync(
            "rest/api/3/project/search", "values", e => e.GetProperty("id").GetString(), 100, default))
            ids.Add(id);

        ids.Should().HaveCount(120);
        ids.Should().OnlyHaveUniqueItems();
        _api.Requests.Count(r => r.AbsolutePath.EndsWith("/project/search")).Should().Be(3);
    }

    [Fact]
    public async Task PageByOffset_NonPositivePageSize_Throws()
    {
        Func<Task> act = async () =>
        {
            await foreach (string _ in NewJiraClient().PageByOffsetAsync(
                "rest/api/3/project/search", "values", e => e.GetProperty("id").GetString(), 0, default)) { }
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static IEnumerable<string> Issues(JsonElement root) =>
        root.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("id").GetString()!);

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hosts.Add(request.RequestUri!.Host);
            return base.SendAsync(request, ct);
        }
    }

    private sealed class FakeFactory(FakeAtlassianApi api) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => api.CreateClient();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
