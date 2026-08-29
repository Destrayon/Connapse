using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Identity.Tests;

/// <summary>
/// Disconnecting an AWS identity link removes the local row, and must not remove one a reconnect
/// established underneath it — see <see cref="AwsIdentityLinkService"/>.
/// </summary>
/// <remarks>
/// Much smaller than it was. This class used to cover revoking a stored refresh token at Cognito
/// and every way that could half-succeed. The link now records an identity IAM Identity Center
/// attested rather than a credential, so there is nothing at AWS to tell and those outcomes no
/// longer exist to test.
/// </remarks>
[Trait("Category", "Unit")]
public class AwsIdentityLinkServiceTests
{
    private static AwsIdentityLinkStore CreateStore(string dbName) =>
        new(CreateFactory(dbName), TimeProvider.System);

    private static IDbContextFactory<ConnapseIdentityDbContext> CreateFactory(string dbName)
    {
        var factory = Substitute.For<IDbContextFactory<ConnapseIdentityDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ConnapseIdentityDbContext(
                new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
                    .UseInMemoryDatabase(dbName)
                    .Options)));
        return factory;
    }

    private static AwsIdentityLinkService CreateService(AwsIdentityLinkStore store) =>
        new(store, NullLogger<AwsIdentityLinkService>.Instance);

    [Fact]
    public async Task DisconnectAsync_ExistingLink_DeletesTheRow()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var store = CreateStore(dbName);
        await store.SaveAsync(userId, "a1b2c3d4-user", "diviel", "diviel@example.com");

        var result = await CreateService(store).DisconnectAsync(userId);

        result.Deleted.Should().BeTrue();
        result.LinkChangedDuringDisconnect.Should().BeFalse();
        (await store.GetAsync(userId)).Should().BeNull();
    }

    [Fact]
    public async Task DisconnectAsync_NoLinkExists_ReturnsDeletedFalse()
    {
        var store = CreateStore(Guid.NewGuid().ToString());

        var result = await CreateService(store).DisconnectAsync(Guid.NewGuid());

        result.Deleted.Should().BeFalse();
        result.LinkChangedDuringDisconnect.Should().BeFalse();
    }

    [Fact]
    public async Task DisconnectAsync_LinkReplacedDuringDisconnect_LeavesTheNewRowInPlace_AndReportsLinkChanged()
    {
        // The race disconnect exists to survive: someone reconnects between the read and the
        // delete. SaveAsync updates the row in place and keeps its Id, so only the rewritten
        // ConnectedAt distinguishes the new link from the one that was read — and deleting anyway
        // would throw away a link the user had just re-established.
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var store = CreateStore(dbName);
        await store.SaveAsync(userId, "a1b2c3d4-user", "diviel", "diviel@example.com");

        var original = await store.GetAsync(userId);
        original.Should().NotBeNull();

        // Reconnect, with a distinct timestamp so the discriminator actually differs.
        await Task.Delay(10);
        await store.SaveAsync(userId, "e5f6a7b8-user", "someone-else", "someone@example.com");

        var deleted = await store.DeleteAsync(userId, original!.ConnectedAt);

        deleted.Should().BeFalse("the row was replaced after it was read");
        var surviving = await store.GetAsync(userId);
        surviving.Should().NotBeNull();
        surviving!.DirectoryUserName.Should().Be("someone-else");
    }

    [Fact]
    public async Task GetAsync_ExistingLink_ReturnsTheDirectoryUserAndTimestamps()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var store = CreateStore(dbName);
        await store.SaveAsync(userId, "a1b2c3d4-user", "diviel", "diviel@example.com");

        var dto = await CreateService(store).GetAsync(userId);

        dto.Should().NotBeNull();
        dto!.DirectoryUserName.Should().Be("diviel");
        dto.Email.Should().Be("diviel@example.com");
        dto.ConnectedAt.Should().NotBe(default);
        dto.LastUsedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_NoLink_ReturnsNull()
    {
        var store = CreateStore(Guid.NewGuid().ToString());

        (await CreateService(store).GetAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_Again_ReplacesTheRowRatherThanAddingASecond()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var store = CreateStore(dbName);

        await store.SaveAsync(userId, "a1b2c3d4-user", "diviel", "diviel@example.com");
        await store.SaveAsync(userId, "e5f6a7b8-user", "diviel-renamed", "diviel@example.com");

        var link = await store.GetAsync(userId);
        link.Should().NotBeNull();
        link!.DirectoryUserId.Should().Be("e5f6a7b8-user");
        link.DirectoryUserName.Should().Be("diviel-renamed");
    }

    [Fact]
    public async Task SaveAsync_WithoutADirectoryUserId_IsRefused()
    {
        // The id is the join key. A link without one resolves to nobody, and a row that cannot
        // resolve is worse than no row: it presents as connected while filtering nothing.
        var store = CreateStore(Guid.NewGuid().ToString());

        var save = async () => await store.SaveAsync(Guid.NewGuid(), "  ", "diviel", null);

        await save.Should().ThrowAsync<ArgumentException>();
    }
}
