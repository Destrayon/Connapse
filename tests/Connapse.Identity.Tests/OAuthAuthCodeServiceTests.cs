using System.Security.Cryptography;
using System.Text;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class OAuthAuthCodeServiceTests
{
    private static ConnapseIdentityDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OAuthAuthCodeService CreateService(ConnapseIdentityDbContext db) =>
        new(db, NullLogger<OAuthAuthCodeService>.Instance);

    private static string ComputeS256Challenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static async Task<Guid> SeedUser(ConnapseIdentityDbContext db, string email = "test@example.com")
    {
        var userId = Guid.NewGuid();
        db.Users.Add(new ConnapseUser { Id = userId, UserName = email, Email = email });
        await db.SaveChangesAsync();
        return userId;
    }

    [Fact]
    public async Task CreateAsync_ReturnsNonEmptyCode()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var code = await sut.CreateAsync(
            Guid.NewGuid(), "client123", "challenge", "http://127.0.0.1:9999/callback", "knowledge:read");

        code.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateAsync_PersistsEntity()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();

        await sut.CreateAsync(userId, "client123", "challenge", "http://127.0.0.1:9999/callback", "knowledge:read");

        var entity = await db.OAuthAuthCodes.SingleAsync();
        entity.UserId.Should().Be(userId);
        entity.ClientId.Should().Be("client123");
        entity.Scope.Should().Be("knowledge:read");
        entity.UsedAt.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_ValidCodeAndPkce_ReturnsUserId()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = await SeedUser(db);
        var verifier = "test-verifier-string-long-enough";
        var challenge = ComputeS256Challenge(verifier);
        var redirectUri = "http://127.0.0.1:9999/callback";
        var clientId = "client123";

        var rawCode = await sut.CreateAsync(userId, clientId, challenge, redirectUri, "knowledge:read");

        var result = await sut.ExchangeAsync(rawCode, verifier, redirectUri, clientId);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(userId);
        result.Scope.Should().Be("knowledge:read");
    }

    [Fact]
    public async Task ExchangeAsync_InvalidCode_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var result = await sut.ExchangeAsync("bogus", "verifier", "http://127.0.0.1:9999", "client123");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_WrongPkceVerifier_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = await SeedUser(db);
        var challenge = ComputeS256Challenge("correct-verifier");

        var rawCode = await sut.CreateAsync(userId, "c", challenge, "http://127.0.0.1:9999", "knowledge:read");

        var result = await sut.ExchangeAsync(rawCode, "wrong-verifier", "http://127.0.0.1:9999", "c");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_WrongRedirectUri_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = await SeedUser(db);
        var verifier = "my-verifier";
        var challenge = ComputeS256Challenge(verifier);

        var rawCode = await sut.CreateAsync(userId, "c", challenge, "http://127.0.0.1:1111", "knowledge:read");

        var result = await sut.ExchangeAsync(rawCode, verifier, "http://127.0.0.1:9999", "c");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_WrongClientId_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = await SeedUser(db);
        var verifier = "my-verifier";
        var challenge = ComputeS256Challenge(verifier);
        var redirectUri = "http://127.0.0.1:9999";

        var rawCode = await sut.CreateAsync(userId, "client-a", challenge, redirectUri, "knowledge:read");

        var result = await sut.ExchangeAsync(rawCode, verifier, redirectUri, "client-b");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_AlreadyUsedCode_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = await SeedUser(db);
        var verifier = "my-verifier";
        var challenge = ComputeS256Challenge(verifier);
        var redirectUri = "http://127.0.0.1:9999";

        var rawCode = await sut.CreateAsync(userId, "c", challenge, redirectUri, "knowledge:read");

        await sut.ExchangeAsync(rawCode, verifier, redirectUri, "c");
        var result = await sut.ExchangeAsync(rawCode, verifier, redirectUri, "c");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_ExpiredCode_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = await SeedUser(db);
        var verifier = "my-verifier";
        var challenge = ComputeS256Challenge(verifier);
        var redirectUri = "http://127.0.0.1:9999";

        var rawCode = await sut.CreateAsync(userId, "c", challenge, redirectUri, "knowledge:read");

        var entity = await db.OAuthAuthCodes.SingleAsync();
        entity.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var result = await sut.ExchangeAsync(rawCode, verifier, redirectUri, "c");

        result.Should().BeNull();
    }
}
