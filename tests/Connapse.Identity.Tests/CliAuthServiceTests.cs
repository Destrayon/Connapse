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
public class CliAuthServiceTests
{
    private static ConnapseIdentityDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PatService CreatePatService(ConnapseIdentityDbContext db) =>
        new(db, NullLogger<PatService>.Instance);

    private static CliAuthService CreateService(ConnapseIdentityDbContext db) =>
        new(db, CreatePatService(db), NullLogger<CliAuthService>.Instance);

    private static string ComputeS256Challenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    // ── Initiate ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InitiateAsync_ValidRequest_ReturnsNonEmptyCode()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var code = await sut.InitiateAsync(Guid.NewGuid(), "challenge", "http://localhost", "TestPC");

        code.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InitiateAsync_ValidRequest_PersistsEntityInDatabase()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();

        await sut.InitiateAsync(userId, "challenge", "http://localhost", "TestPC");

        var entity = await db.CliAuthCodes.SingleAsync();
        entity.UserId.Should().Be(userId);
        entity.MachineName.Should().Be("TestPC");
        entity.RedirectUri.Should().Be("http://localhost");
        entity.UsedAt.Should().BeNull();
    }

    // ── Exchange ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeAsync_ValidCodeAndPkce_ReturnsPatAndEmail()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();
        var codeVerifier = "test-verifier-string-long-enough";
        var codeChallenge = ComputeS256Challenge(codeVerifier);
        var redirectUri = "http://localhost:9999/callback";

        // Seed user so PAT creation can reference them
        db.Users.Add(new ConnapseUser
        {
            Id = userId,
            UserName = "test@example.com",
            Email = "test@example.com",
        });
        await db.SaveChangesAsync();

        var rawCode = await sut.InitiateAsync(userId, codeChallenge, redirectUri, "MyPC");

        var result = await sut.ExchangeAsync(rawCode, codeVerifier, redirectUri);

        result.Should().NotBeNull();
        result!.Value.Pat.Token.Should().StartWith("cnp_");
        result.Value.UserEmail.Should().Be("test@example.com");
    }

    [Fact]
    public async Task ExchangeAsync_InvalidCode_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var result = await sut.ExchangeAsync("bogus-code", "verifier", "http://localhost");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_WrongPkceVerifier_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();
        var codeChallenge = ComputeS256Challenge("correct-verifier");

        db.Users.Add(new ConnapseUser { Id = userId, UserName = "u@test.com", Email = "u@test.com" });
        await db.SaveChangesAsync();

        var rawCode = await sut.InitiateAsync(userId, codeChallenge, "http://localhost", "PC");

        var result = await sut.ExchangeAsync(rawCode, "wrong-verifier", "http://localhost");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_WrongRedirectUri_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();
        var verifier = "my-verifier";
        var challenge = ComputeS256Challenge(verifier);

        db.Users.Add(new ConnapseUser { Id = userId, UserName = "u@test.com", Email = "u@test.com" });
        await db.SaveChangesAsync();

        var rawCode = await sut.InitiateAsync(userId, challenge, "http://localhost:1111", "PC");

        var result = await sut.ExchangeAsync(rawCode, verifier, "http://localhost:9999");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_AlreadyUsedCode_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();
        var verifier = "my-verifier";
        var challenge = ComputeS256Challenge(verifier);
        var redirectUri = "http://localhost";

        db.Users.Add(new ConnapseUser { Id = userId, UserName = "u@test.com", Email = "u@test.com" });
        await db.SaveChangesAsync();

        var rawCode = await sut.InitiateAsync(userId, challenge, redirectUri, "PC");

        // First exchange succeeds
        await sut.ExchangeAsync(rawCode, verifier, redirectUri);

        // Second exchange fails (replay)
        var result = await sut.ExchangeAsync(rawCode, verifier, redirectUri);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAsync_ExpiredCode_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();
        var verifier = "my-verifier";
        var challenge = ComputeS256Challenge(verifier);
        var redirectUri = "http://localhost";

        db.Users.Add(new ConnapseUser { Id = userId, UserName = "u@test.com", Email = "u@test.com" });
        await db.SaveChangesAsync();

        var rawCode = await sut.InitiateAsync(userId, challenge, redirectUri, "PC");

        // Manually expire the code
        var entity = await db.CliAuthCodes.SingleAsync();
        entity.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var result = await sut.ExchangeAsync(rawCode, verifier, redirectUri);

        result.Should().BeNull();
    }
}
