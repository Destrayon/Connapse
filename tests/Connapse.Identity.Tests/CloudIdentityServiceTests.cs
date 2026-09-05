using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Connapse.Identity.Stores;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class CloudIdentityServiceTests
{
    private readonly ICloudIdentityStore _store = Substitute.For<ICloudIdentityStore>();
    private readonly IDataProtectionProvider _dpProvider;

    public CloudIdentityServiceTests()
    {
        _dpProvider = new EphemeralDataProtectionProvider();
    }

    private ICloudIdentityService CreateService() =>
        new CloudIdentityService(_store, _dpProvider, NullLogger<CloudIdentityService>.Instance);

    // ── Disconnect ────────────────────────────────────────────────────────

    [Fact]
    public async Task Disconnect_ExistingIdentity_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        _store.DeleteAsync(userId, CloudProvider.Azure, Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = CreateService();
        var result = await sut.DisconnectAsync(userId, CloudProvider.Azure);

        result.Should().BeTrue();
        await _store.Received(1).DeleteAsync(userId, CloudProvider.Azure, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disconnect_NonExistingIdentity_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        _store.DeleteAsync(userId, CloudProvider.AWS, Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = CreateService();
        var result = await sut.DisconnectAsync(userId, CloudProvider.AWS);

        result.Should().BeFalse();
    }

    // ── List ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_NoIdentities_ReturnsEmptyList()
    {
        var userId = Guid.NewGuid();
        _store.ListByUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserCloudIdentityEntity>());

        var sut = CreateService();
        var result = await sut.ListAsync(userId);

        result.Should().BeEmpty();
    }

    // ── Encrypt/Decrypt round-trip ────────────────────────────────────────

    [Fact]
    public async Task Get_AfterStore_DecryptsDataCorrectly()
    {
        var userId = Guid.NewGuid();
        var protector = _dpProvider.CreateProtector("CloudIdentity.v1");
        var identityData = new CloudIdentityData(null, null, "obj-123", "tenant-456", "Test User");
        var encrypted = protector.Protect(JsonSerializer.Serialize(identityData,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var entity = new UserCloudIdentityEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = CloudProvider.Azure,
            IdentityDataJson = encrypted,
            CreatedAt = DateTime.UtcNow
        };

        _store.GetByUserAndProviderAsync(userId, CloudProvider.Azure, Arg.Any<CancellationToken>())
            .Returns(entity);

        var sut = CreateService();
        var result = await sut.GetAsync(userId, CloudProvider.Azure);

        result.Should().NotBeNull();
        result!.Provider.Should().Be(CloudProvider.Azure);
        result.Data.ObjectId.Should().Be("obj-123");
        result.Data.TenantId.Should().Be("tenant-456");
        result.Data.DisplayName.Should().Be("Test User");
        result.Data.PrincipalArn.Should().BeNull();
        result.Data.AccountId.Should().BeNull();
    }

    [Fact]
    public async Task Get_CorruptedData_ReturnsEmptyDataInsteadOfThrowing()
    {
        var userId = Guid.NewGuid();
        var entity = new UserCloudIdentityEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = CloudProvider.AWS,
            IdentityDataJson = "not-encrypted-data",
            CreatedAt = DateTime.UtcNow
        };

        _store.GetByUserAndProviderAsync(userId, CloudProvider.AWS, Arg.Any<CancellationToken>())
            .Returns(entity);

        var sut = CreateService();
        var result = await sut.GetAsync(userId, CloudProvider.AWS);

        result.Should().NotBeNull();
        result!.Data.PrincipalArn.Should().BeNull();
        result.Data.AccountId.Should().BeNull();
    }

    [Fact]
    public async Task Get_NonExistent_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        _store.GetByUserAndProviderAsync(userId, CloudProvider.AWS, Arg.Any<CancellationToken>())
            .Returns((UserCloudIdentityEntity?)null);

        var sut = CreateService();
        var result = await sut.GetAsync(userId, CloudProvider.AWS);

        result.Should().BeNull();
    }
}
