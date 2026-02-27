using System.Security.Cryptography;
using System.Text;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Connapse.Core;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class PatServiceTests
{
    private static ConnapseIdentityDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PatService CreateService(ConnapseIdentityDbContext db) =>
        new(db, NullLogger<PatService>.Instance);

    // ── Token format ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidRequest_TokenStartsWithCnpPrefix()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();

        var result = await sut.CreateAsync(userId, new PatCreateRequest("My Token", null, null));

        result.Token.Should().StartWith("cnp_");
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_TokenPrefixMatchesFirstTwelveChars()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();

        var result = await sut.CreateAsync(userId, new PatCreateRequest("My Token", null, null));

        result.Token[..12].Should().Be(result.Token[..12]);
        var stored = await db.PersonalAccessTokens.SingleAsync();
        stored.TokenPrefix.Should().Be(result.Token[..12]);
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_StoredHashIsSha256OfToken()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();

        var result = await sut.CreateAsync(userId, new PatCreateRequest("Test", null, null));

        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(result.Token)));
        var stored = await db.PersonalAccessTokens.SingleAsync();
        stored.TokenHash.Should().Be(expectedHash);
    }

    // ── List ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_MultipleUsers_ReturnsOnlyOwnersPats()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        await sut.CreateAsync(userId, new PatCreateRequest("Mine", null, null));
        await sut.CreateAsync(userId, new PatCreateRequest("Also Mine", null, null));
        await sut.CreateAsync(otherId, new PatCreateRequest("Not Mine", null, null));

        var list = await sut.ListAsync(userId);

        list.Should().HaveCount(2);
        list.Should().AllSatisfy(p => p.Name.Should().NotBe("Not Mine"));
    }

    // ── Revoke ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeAsync_ExistingActivePat_ReturnsTrueAndSetsRevokedAt()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();

        var created = await sut.CreateAsync(userId, new PatCreateRequest("Token", null, null));

        var result = await sut.RevokeAsync(userId, created.Id);

        result.Should().BeTrue();
        var stored = await db.PersonalAccessTokens.SingleAsync();
        stored.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRevoked_ReturnsFalse()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var userId = Guid.NewGuid();

        var created = await sut.CreateAsync(userId, new PatCreateRequest("Token", null, null));
        await sut.RevokeAsync(userId, created.Id);

        var secondRevoke = await sut.RevokeAsync(userId, created.Id);

        secondRevoke.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_WrongUserId_ReturnsFalse()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var ownerId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();

        var created = await sut.CreateAsync(ownerId, new PatCreateRequest("Token", null, null));

        var result = await sut.RevokeAsync(attackerId, created.Id);

        result.Should().BeFalse();
    }
}
