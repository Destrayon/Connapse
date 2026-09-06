using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Connapse.Identity.Tests;

/// <summary>
/// Storing, reading, and disconnecting a user's Entra identity link, end to end through
/// <see cref="AzureIdentityLinkService"/>, <see cref="AzureIdentityLinkStore"/>, and the
/// <see cref="IAzureIdentityLinkReader"/> it also implements.
/// </summary>
[Trait("Category", "Unit")]
public class AzureIdentityLinkServiceTests
{
    private static AzureIdentityLinkStore CreateStore(string dbName) =>
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

    private static AzureIdentityLinkService CreateService(AzureIdentityLinkStore store) => new(store);

    [Fact]
    public async Task Store_Get_Disconnect_RoundTrips()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var store = CreateStore(dbName);
        var svc = CreateService(store);

        await svc.StoreAsync(userId, "oid-1", "tid-1", "Ada Lovelace", default);

        var dto = await svc.GetAsync(userId, default);
        dto.Should().NotBeNull();
        dto!.ObjectId.Should().Be("oid-1");
        dto.TenantId.Should().Be("tid-1");
        dto.DisplayName.Should().Be("Ada Lovelace");

        IAzureIdentityLinkReader reader = store;
        (await reader.GetLinkAsync(userId, default)).Should().Be(new AzureIdentityRef("oid-1", "tid-1"));

        (await svc.DisconnectAsync(userId, default)).Should().BeTrue();
        (await svc.GetAsync(userId, default)).Should().BeNull();
        (await reader.GetLinkAsync(userId, default)).Should().BeNull();
    }

    [Fact]
    public async Task DisconnectAsync_NoLinkExists_ReturnsFalse()
    {
        var store = CreateStore(Guid.NewGuid().ToString());

        (await CreateService(store).DisconnectAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task StoreAsync_Again_ReplacesTheRowRatherThanAddingASecond()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var store = CreateStore(dbName);
        var svc = CreateService(store);

        await svc.StoreAsync(userId, "oid-1", "tid-1", "Ada Lovelace", default);
        await svc.StoreAsync(userId, "oid-2", "tid-2", "Ada Renamed", default);

        var link = await store.GetAsync(userId);
        link.Should().NotBeNull();
        link!.ObjectId.Should().Be("oid-2");
        link.TenantId.Should().Be("tid-2");
        link.DisplayName.Should().Be("Ada Renamed");
    }

    [Fact]
    public async Task SaveAsync_WithoutAnObjectId_IsRefused()
    {
        // oid+tid together are the join key. A link without one resolves to nobody, and a row
        // that cannot resolve is worse than no row: it presents as connected while filtering
        // nothing.
        var store = CreateStore(Guid.NewGuid().ToString());

        var save = async () => await store.SaveAsync(Guid.NewGuid(), "  ", "tid-1", "Ada Lovelace");

        await save.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetAsync_NoLink_ReturnsNull()
    {
        var store = CreateStore(Guid.NewGuid().ToString());

        (await CreateService(store).GetAsync(Guid.NewGuid())).Should().BeNull();
    }
}
