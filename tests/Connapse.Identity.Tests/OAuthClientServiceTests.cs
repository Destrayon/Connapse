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
    public async Task RegisterAsync_WebClientLocalhostRedirect_Throws()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var act = () => sut.RegisterAsync(
            "Bad Client", ["http://127.0.0.1:3000/callback"], "web");

        await act.Should().ThrowAsync<ArgumentException>();
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
}
