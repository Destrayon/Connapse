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
    public async Task GetJsonAsync_Persistent401_ThrowsAuthExceptionAfterOneRetry()
    {
        _api.Map(Root + "/api/v2/spaces/1", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await FluentActions.Awaiting(() => NewClient().GetJsonAsync<JsonElement>("api/v2/spaces/1", default))
            .Should().ThrowAsync<AtlassianAuthException>();

        _api.TokenRequests.Should().Be(2);
        _api.Calls[Root + "/api/v2/spaces/1"].Should().Be(2);
    }

    [Fact]
    public async Task GetJsonAsync_TokenEndpointRefuses_ThrowsAuthException()
    {
        _api.FailTokenWith(HttpStatusCode.Unauthorized);

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

    private static HttpResponseMessage WithLink(HttpResponseMessage response, string link)
    {
        response.Headers.TryAddWithoutValidation("Link", link);
        return response;
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
