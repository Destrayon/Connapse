using System.Text.Json;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class OAuthClientServiceTests
{
    private static ConnapseIdentityDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OAuthClientService CreateService(ConnapseIdentityDbContext db, HttpClient? httpClient = null) =>
        new(db, httpClient ?? new HttpClient(), NullLogger<OAuthClientService>.Instance);

    // -- Dynamic Registration --

    [Fact]
    public async Task RegisterAsync_ValidRequest_ReturnsClientWithId()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var result = await sut.RegisterAsync(
            "Test Client", ["http://127.0.0.1:3000/callback"], "native");

        result.ClientId.Should().NotBeNullOrWhiteSpace();
        result.ClientName.Should().Be("Test Client");
        result.ApplicationType.Should().Be("native");
    }

    [Fact]
    public async Task RegisterAsync_PersistsToDatabase()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var result = await sut.RegisterAsync(
            "Test Client", ["http://127.0.0.1:3000/callback"], "native");

        var entity = await db.OAuthClients.SingleAsync();
        entity.ClientId.Should().Be(result.ClientId);
        entity.ClientName.Should().Be("Test Client");
    }

    [Fact]
    public async Task RegisterAsync_WithIp_PersistsIp()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        await sut.RegisterAsync(
            "Test Client", ["http://127.0.0.1:3000/callback"], "native", "203.0.113.42");

        var entity = await db.OAuthClients.SingleAsync();
        entity.RegisteredFromIp.Should().Be("203.0.113.42");
    }

    [Fact]
    public async Task RegisterAsync_WithoutIp_PersistsNullIp()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        await sut.RegisterAsync(
            "Test Client", ["http://127.0.0.1:3000/callback"], "native");

        var entity = await db.OAuthClients.SingleAsync();
        entity.RegisteredFromIp.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_InvalidRedirectUri_Throws()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var act = () => sut.RegisterAsync(
            "Bad Client", ["ftp://evil.com/callback"], "native");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RegisterAsync_NativeClientHttpsRedirect_Throws()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var act = () => sut.RegisterAsync(
            "Bad Client", ["https://example.com/callback"], "native");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RegisterAsync_WebClientOnlyLoopbackRedirects_RecordedAsNative()
    {
        // A loopback redirect can only be received by an app on the user's own machine (RFC 8252 §7.3).
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var result = await sut.RegisterAsync("App", ["http://127.0.0.1:3000/callback"], "web");

        result.ApplicationType.Should().Be("native");
        (await db.OAuthClients.SingleAsync()).ApplicationType.Should().Be("native");
    }

    [Theory]
    [InlineData("http://localhost:53682/callback")]
    [InlineData("http://localhost/callback")]
    [InlineData("http://127.0.0.1:8765/callback")]
    [InlineData("http://[::1]:8765/callback")]
    public async Task RegisterAsync_NoApplicationTypeLoopbackRedirect_RegistersAsNative(string redirect)
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var result = await sut.RegisterAsync("App", [redirect], null);

        result.ApplicationType.Should().Be("native");
        result.RedirectUris.Should().Equal(redirect);
    }

    [Fact]
    public async Task RegisterAsync_NoApplicationTypeHttpsRedirect_RegistersAsWeb()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var result = await sut.RegisterAsync("App", ["https://claude.ai/api/mcp/auth_callback"], null);

        result.ApplicationType.Should().Be("web");
    }

    [Theory]
    [InlineData(null, "http://divielserver/callback")]
    [InlineData(null, "http://192.168.1.20:8080/callback")]
    [InlineData(null, "http://localhost.example.com/callback")]
    [InlineData("web", "http://divielserver/callback")]
    [InlineData("native", "http://192.168.1.20/callback")]
    [InlineData("native", "http://127.0.0.2/callback")]
    [InlineData("native", "https://localhost/callback")]
    public async Task RegisterAsync_NonLoopbackHttpRedirect_Throws(string? applicationType, string redirect)
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var act = () => sut.RegisterAsync("App", [redirect], applicationType);

        await act.Should().ThrowAsync<ArgumentException>();
        (await db.OAuthClients.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("web")]
    public async Task RegisterAsync_HttpsAndLoopbackRedirectsMixed_Throws(string? applicationType)
    {
        // Not all loopback, so it is a web client, and a web client's redirects are all https.
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var act = () => sut.RegisterAsync(
            "App", ["https://app.example.com/callback", "http://127.0.0.1/callback"], applicationType);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Web clients must use https*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Native")]
    [InlineData("service")]
    public async Task RegisterAsync_UnknownApplicationType_Throws(string applicationType)
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var act = () => sut.RegisterAsync("App", ["https://app.example.com/callback"], applicationType);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*application_type*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("native")]
    public async Task RegisterAsync_NoRedirectUris_Throws(string? applicationType)
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var act = () => sut.RegisterAsync("App", [], applicationType);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*redirect_uri*");
    }

    [Fact]
    public async Task RegisterAsync_UnparseableRedirectWithoutType_Throws()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var act = () => sut.RegisterAsync("App", ["not a uri"], null);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("Invalid redirect URI*");
    }

    // -- Lookup --

    [Fact]
    public async Task GetByClientIdAsync_Registered_ReturnsClient()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var registered = await sut.RegisterAsync(
            "Test", ["http://127.0.0.1:3000/callback"], "native");

        var found = await sut.GetByClientIdAsync(registered.ClientId);

        found.Should().NotBeNull();
        found!.ClientName.Should().Be("Test");
    }

    [Fact]
    public async Task GetByClientIdAsync_Unknown_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var found = await sut.GetByClientIdAsync("nonexistent");

        found.Should().BeNull();
    }

    // -- Redirect URI validation --

    [Fact]
    public async Task ValidateRedirectUri_Registered_MatchingUri_ReturnsTrue()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        await sut.RegisterAsync("Test", ["http://127.0.0.1:3000/callback"], "native");
        var client = (await db.OAuthClients.SingleAsync());

        var result = sut.ValidateRedirectUri(client, "http://127.0.0.1:3000/callback");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateRedirectUri_Registered_NonMatchingUri_ReturnsFalse()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        await sut.RegisterAsync("Test", ["http://127.0.0.1:3000/callback"], "native");
        var client = (await db.OAuthClients.SingleAsync());

        var result = sut.ValidateRedirectUri(client, "http://127.0.0.1:9999/callback");

        result.Should().BeFalse();
    }

    [Fact]
    public void ValidateRedirectUri_LoopbackDifferentPort_ReturnsTrue()
    {
        var client = new OAuthClientInfo("test", "Test", ["http://127.0.0.1/callback"], "native");

        var result = OAuthClientService.ValidateRedirectUri(client, "http://127.0.0.1:54321/callback");

        result.Should().BeTrue();
    }

    [Fact]
    public void ValidateRedirectUri_LoopbackDifferentPath_ReturnsFalse()
    {
        var client = new OAuthClientInfo("test", "Test", ["http://127.0.0.1/callback"], "native");

        var result = OAuthClientService.ValidateRedirectUri(client, "http://127.0.0.1:54321/evil");

        result.Should().BeFalse();
    }

    [Fact]
    public void ValidateRedirectUri_Ipv6LoopbackDifferentPort_ReturnsTrue()
    {
        var client = new OAuthClientInfo("test", "Test", ["http://[::1]/callback"], "native");

        OAuthClientService.ValidateRedirectUri(client, "http://[::1]:54321/callback").Should().BeTrue();
    }

    [Theory]
    [InlineData("http://localhost:1/x", true)]
    [InlineData("http://127.0.0.1/x", true)]
    [InlineData("http://[::1]:9/x", true)]
    [InlineData("https://localhost/x", false)]
    [InlineData("http://127.0.0.2/x", false)]
    [InlineData("http://localhost.example.com/x", false)]
    [InlineData("http://divielserver/x", false)]
    public void IsLoopbackHttp_ClassifiesHosts(string url, bool expected) =>
        OAuthClientService.IsLoopbackHttp(new Uri(url)).Should().Be(expected);

    [Theory]
    [InlineData("https://divielserver/oauth/clients/cli.json", true)]
    [InlineData("http://localhost:5001/oauth/clients/cli.json", true)]
    [InlineData("http://divielserver/oauth/clients/cli.json", false)]
    [InlineData("ftp://example.com/cli.json", false)]
    public void IsMetadataDocumentClientId_HttpsOrLoopbackHttp(string url, bool expected) =>
        OAuthClientService.IsMetadataDocumentClientId(new Uri(url)).Should().Be(expected);

    // -- First-party CLI client --

    [Theory]
    [InlineData("http://divielserver:8080/oauth/clients/cli.json", "http://divielserver:8080", true)]
    [InlineData("https://kb.example.com/oauth/clients/cli.json", "https://kb.example.com", true)]
    [InlineData("http://other:8080/oauth/clients/cli.json", "http://divielserver:8080", false)]
    [InlineData("http://divielserver/oauth/clients/cli.json", "http://divielserver:8080", false)]
    [InlineData("https://divielserver:8080/oauth/clients/cli.json", "http://divielserver:8080", false)]
    [InlineData("http://divielserver:8080/oauth/clients/other.json", "http://divielserver:8080", false)]
    [InlineData("http://divielserver:8080/oauth/clients/cli.json?x=1", "http://divielserver:8080", false)]
    [InlineData("http://divielserver:8080/oauth/clients/cli.json", null, false)]
    [InlineData("http://divielserver:8080/oauth/clients/cli.json", "", false)]
    public void IsFirstPartyCliClientId_OnlyThisServersExactDocument(string clientId, string? serverBaseUrl, bool expected) =>
        OAuthClientService.IsFirstPartyCliClientId(clientId, serverBaseUrl).Should().Be(expected);

    [Fact]
    public async Task ResolveClientAsync_ThisServersCliOverHttp_ResolvesWithoutFetching()
    {
        using var db = CreateDbContext();
        var handler = new RecordingHandler();
        var sut = CreateService(db, new HttpClient(handler));
        const string clientId = "http://divielserver:8080/oauth/clients/cli.json";

        var client = await sut.ResolveClientAsync(clientId, "http://divielserver:8080");

        client.Should().NotBeNull();
        client!.ClientId.Should().Be(clientId);
        client.ClientName.Should().Be(OAuthClientService.FirstPartyCliName);
        client.RedirectUris.Should().Equal("http://127.0.0.1/callback");
        client.ApplicationType.Should().Be("native");
        handler.Requests.Should().Be(0, "the server answers for its own CLI document instead of fetching it");
        OAuthClientService.ValidateRedirectUri(client, "http://127.0.0.1:49152/callback").Should().BeTrue();
        OAuthClientService.ValidateRedirectUri(client, "http://divielserver:8080/callback").Should().BeFalse();
    }

    [Theory]
    [InlineData("http://evil.lan/oauth/clients/cli.json", "http://divielserver:8080")]
    [InlineData("http://divielserver:8080/oauth/clients/cli.json", null)]
    public async Task ResolveClientAsync_HttpCliOfAnotherServer_IsUnknownAndNotFetched(string clientId, string? serverBaseUrl)
    {
        using var db = CreateDbContext();
        var handler = new RecordingHandler();
        var sut = CreateService(db, new HttpClient(handler));

        var client = await sut.ResolveClientAsync(clientId, serverBaseUrl);

        client.Should().BeNull();
        handler.Requests.Should().Be(0);
    }

    [Fact]
    public async Task ResolveClientAsync_HttpsMetadataUrl_StillFetchesTheDocument()
    {
        using var db = CreateDbContext();
        const string clientId = "https://app.example.com/client.json";
        var handler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            client_id = clientId,
            client_name = "App",
            redirect_uris = new[] { "http://127.0.0.1/callback" },
        }));
        var sut = CreateService(db, new HttpClient(handler));

        var client = await sut.ResolveClientAsync(clientId, "http://divielserver:8080");

        client!.ClientName.Should().Be("App");
        handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task ResolveClientAsync_RegisteredClient_FoundByIdWithServerBaseUrl()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var registered = await sut.RegisterAsync("App", ["http://localhost:53682/callback"], null);

        var client = await sut.ResolveClientAsync(registered.ClientId, "http://divielserver:8080");

        client!.ApplicationType.Should().Be("native");
        client.RedirectUris.Should().Equal("http://localhost:53682/callback");
    }

    private sealed class RecordingHandler(string? body = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
