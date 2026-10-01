using System.Net;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public sealed class ConfluencePermissionCheckerTests : IDisposable
{
    internal const string CloudId = "11111111-2222-3333-4444-555555555555";
    internal const string CheckPath = "/ex/confluence/" + CloudId + "/wiki/rest/api/content/42/permission/check";
    private const string Account = "557058:abc";

    private readonly FakeAtlassianApi _api = new();
    private readonly RecordingCache _cache = new();
    private readonly ConfluencePermissionChecker _checker;

    public ConfluencePermissionCheckerTests()
    {
        _checker = NewChecker(_api, _cache, AtlassianConnectionStore());
    }

    public void Dispose()
    {
        _api.Dispose();
        _cache.Dispose();
    }

    internal static IConnectionStore AtlassianConnectionStore(params string[] cloudIds)
    {
        if (cloudIds.Length == 0) cloudIds = [CloudId];
        var store = Substitute.For<IConnectionStore>();
        var connections = new List<Connection>();
        foreach (string cloudId in cloudIds)
        {
            var id = Guid.NewGuid();
            connections.Add(new Connection(id, "site-" + cloudId, ConnectionProvider.Atlassian,
                $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{cloudId}}","clientId":"client-{{cloudId}}"}""",
                null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true));
            store.GetSecretAsync(id, Arg.Any<CancellationToken>()).Returns("secret");
        }
        store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(connections);
        return store;
    }

    internal static ConfluencePermissionChecker NewChecker(HttpMessageHandler handler, IMemoryCache cache, IConnectionStore connections)
    {
        var factory = new HandlerFactory(handler);
        var scopes = new ServiceCollection().AddSingleton(connections).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new ConfluencePermissionChecker(factory, new AtlassianTokenSource(factory, TimeProvider.System),
            scopes, cache, NullLogger<ConfluencePermissionChecker>.Instance);
    }

    private Task<bool> CheckAsync(string account = Account, string cloudId = CloudId) =>
        _checker.CanReadAsync(cloudId, "42", account, default);

    private int Checks => _api.Calls.GetValueOrDefault(CheckPath);

    [Fact]
    public async Task Check_200True_Allows()
    {
        string? body = null;
        _api.Map(CheckPath, request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeAtlassianApi.Json(new { hasPermission = true });
        });

        (await CheckAsync()).Should().BeTrue();

        body.Should().Be("""{"subject":{"type":"user","identifier":"557058:abc"},"operation":"read"}""");
        _cache.TtlOf($"atl:{CloudId}:{Account}:42").Should().Be(ConfluencePermissionChecker.AllowTtl);
    }

    [Fact]
    public async Task Check_200False_Denies()
    {
        _api.MapJson(CheckPath, new { hasPermission = false });

        (await CheckAsync()).Should().BeFalse();
        _cache.TtlOf($"atl:{CloudId}:{Account}:42").Should().Be(ConfluencePermissionChecker.AllowTtl);
    }

    [Fact]
    public async Task Check_403_Denies()
    {
        _api.Map(CheckPath, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        (await CheckAsync()).Should().BeFalse();
        _cache.TtlOf($"atl:{CloudId}:{Account}:42").Should().Be(ConfluencePermissionChecker.FailureTtl);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Check_OtherNon200_Denies(HttpStatusCode status)
    {
        _api.Map(CheckPath, _ => new HttpResponseMessage(status));

        (await CheckAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Check_429_DeniesAndCachesBriefly()
    {
        _api.MapJson(CheckPath, new { hasPermission = true });
        _api.RateLimitNext = true;

        (await CheckAsync()).Should().BeFalse();
        (await CheckAsync()).Should().BeFalse("the failure is cached, not retried at once");

        _cache.TtlOf($"atl:{CloudId}:{Account}:42").Should().Be(ConfluencePermissionChecker.FailureTtl);
        Checks.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Check_EmptyAccountId_DeniesWithoutCall(string account)
    {
        _api.MapJson(CheckPath, new { hasPermission = true });

        (await CheckAsync(account)).Should().BeFalse();
        _api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("ANONYMOUS")]
    [InlineData("Anonymous")]
    public async Task Check_Anonymous_DeniesWithoutCall(string account)
    {
        _api.MapJson(CheckPath, new { hasPermission = true });

        (await CheckAsync(account)).Should().BeFalse();
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_UnknownCloudId_Denies()
    {
        _api.MapJson(CheckPath, new { hasPermission = true });

        (await CheckAsync(cloudId: "99999999-2222-3333-4444-555555555555")).Should().BeFalse();
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_UnreadableSecret_Denies()
    {
        _api.MapJson(CheckPath, new { hasPermission = true });
        var store = AtlassianConnectionStore();
        store.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new System.Security.Cryptography.CryptographicException("key ring gone"));

        (await NewChecker(_api, _cache, store).CanReadAsync(CloudId, "42", Account, default)).Should().BeFalse();
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_TokenExchangeFails_Denies()
    {
        _api.MapJson(CheckPath, new { hasPermission = true });
        _api.FailTokenWith(HttpStatusCode.Unauthorized);

        (await CheckAsync()).Should().BeFalse();
        Checks.Should().Be(0);
    }

    [Fact]
    public async Task Check_AllowCachedForOneAccount_DoesNotAllowAnother()
    {
        _api.Map(CheckPath, request =>
        {
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeAtlassianApi.Json(new { hasPermission = body.Contains($"\"{Account}\"") });
        });

        (await CheckAsync(Account)).Should().BeTrue();
        (await CheckAsync("557058:other")).Should().BeFalse();

        Checks.Should().Be(2, "each account is asked about separately");
    }

    [Fact]
    public async Task Check_AllowCachedForOneSite_DoesNotAllowAnother()
    {
        const string OtherCloud = "99999999-2222-3333-4444-555555555555";
        _api.MapJson(CheckPath, new { hasPermission = true });
        _api.MapJson($"/ex/confluence/{OtherCloud}/wiki/rest/api/content/42/permission/check", new { hasPermission = false });
        var checker = NewChecker(_api, _cache, AtlassianConnectionStore(CloudId, OtherCloud));

        (await checker.CanReadAsync(CloudId, "42", Account, default)).Should().BeTrue();
        (await checker.CanReadAsync(OtherCloud, "42", Account, default)).Should().BeFalse(
            "the same content id on another site is different content");
    }

    [Fact]
    public async Task Check_SecondCallWithinTtl_UsesCache()
    {
        _api.MapJson(CheckPath, new { hasPermission = true });

        (await CheckAsync()).Should().BeTrue();
        (await CheckAsync()).Should().BeTrue();

        Checks.Should().Be(1);
    }

    [Theory]
    [InlineData("""{"hasPermission":"true"}""")]
    [InlineData("""{"hasPermission":1}""")]
    [InlineData("""{"hasPermission":null}""")]
    [InlineData("""{}""")]
    [InlineData("""[true]""")]
    [InlineData("""not json""")]
    public async Task Check_HasPermissionNotBoolean_Denies(string json)
    {
        _api.Map(CheckPath, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        (await CheckAsync()).Should().BeFalse();
        _cache.TtlOf($"atl:{CloudId}:{Account}:42").Should().Be(ConfluencePermissionChecker.FailureTtl);
    }

    [Theory]
    [InlineData("../42")]
    [InlineData("42?x=1")]
    [InlineData("")]
    public async Task Check_NonNumericContentId_DeniesWithoutCall(string contentId)
    {
        (await _checker.CanReadAsync(CloudId, contentId, Account, default)).Should().BeFalse();
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_CallerCancelled_ThrowsAndCachesNothing()
    {
        _api.MapJson(CheckPath, new { hasPermission = true });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => _checker.CanReadAsync(CloudId, "42", Account, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        _cache.TtlOf($"atl:{CloudId}:{Account}:42").Should().BeNull();
    }

    internal sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A real cache that remembers the lifetime each key was last set with.</summary>
    internal sealed class RecordingCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(new MemoryCacheOptions());
        private readonly Dictionary<object, ICacheEntry> _entries = [];

        public TimeSpan? TtlOf(string key)
        {
            lock (_entries) return _entries.TryGetValue(key, out var entry) ? entry.AbsoluteExpirationRelativeToNow : null;
        }

        public ICacheEntry CreateEntry(object key)
        {
            var entry = _inner.CreateEntry(key);
            lock (_entries) _entries[key] = entry;
            return entry;
        }

        public void Remove(object key) => _inner.Remove(key);

        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

        public void Dispose() => _inner.Dispose();
    }
}
