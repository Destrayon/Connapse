using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Connapse.Identity.Stores;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class CloudIdentityServiceTests
{
    private readonly ICloudIdentityStore _store = Substitute.For<ICloudIdentityStore>();
    private readonly IDataProtectionProvider _dpProvider;
    private readonly IOptionsMonitor<AzureAdSettings> _azureAdOptions;
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    private readonly AzureAdSettings _azureAdSettings = new()
    {
        ClientId = "test-client-id",
        TenantId = "test-tenant-id",
        ClientSecret = "test-client-secret"
    };

    public CloudIdentityServiceTests()
    {
        _dpProvider = new EphemeralDataProtectionProvider();

        _azureAdOptions = Substitute.For<IOptionsMonitor<AzureAdSettings>>();
        _azureAdOptions.CurrentValue.Returns(_azureAdSettings);
    }

    private ICloudIdentityService CreateService() =>
        new CloudIdentityService(_store, _dpProvider, _azureAdOptions,
            _httpClientFactory, NullLogger<CloudIdentityService>.Instance);

    // ── IsAzureAdConfigured ───────────────────────────────────────────────

    [Fact]
    public void IsAzureAdConfigured_WithClientIdAndTenantId_ReturnsTrue()
    {
        var sut = CreateService();
        sut.IsAzureAdConfigured().Should().BeTrue();
    }

    [Fact]
    public void IsAzureAdConfigured_EmptyClientId_ReturnsFalse()
    {
        _azureAdSettings.ClientId = "";
        var sut = CreateService();
        sut.IsAzureAdConfigured().Should().BeFalse();
    }

    [Fact]
    public void IsAzureAdConfigured_EmptyTenantId_ReturnsFalse()
    {
        _azureAdSettings.TenantId = "";
        var sut = CreateService();
        sut.IsAzureAdConfigured().Should().BeFalse();
    }

    // ── GetAzureConnectUrl ────────────────────────────────────────────────

    [Fact]
    public void GetAzureConnectUrl_ReturnsValidUrl_WithCorrectParameters()
    {
        var sut = CreateService();
        var result = sut.GetAzureConnectUrl("https://connapse.local");

        result.AuthorizeUrl.Should().Contain("login.microsoftonline.com");
        result.AuthorizeUrl.Should().Contain(_azureAdSettings.TenantId);
        result.AuthorizeUrl.Should().Contain("client_id=test-client-id");
        result.AuthorizeUrl.Should().Contain("response_type=code");
        result.AuthorizeUrl.Should().Contain("scope=openid");
        result.AuthorizeUrl.Should().Contain("redirect_uri=");
        result.State.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetAzureConnectUrl_AutoDerivesRedirectUri()
    {
        var sut = CreateService();
        var result = sut.GetAzureConnectUrl("https://connapse.local");

        result.AuthorizeUrl.Should().Contain("connapse.local%2Fapi%2Fv1%2Fauth%2Fcloud%2Fazure%2Fcallback");
    }

    [Fact]
    public void GetAzureConnectUrl_IncludesPkceParameters()
    {
        var sut = CreateService();
        var result = sut.GetAzureConnectUrl("https://connapse.local");

        result.AuthorizeUrl.Should().Contain("code_challenge=");
        result.AuthorizeUrl.Should().Contain("code_challenge_method=S256");
        result.CodeVerifier.Should().NotBeNullOrEmpty();
        result.CodeVerifier.Length.Should().BeGreaterThanOrEqualTo(43);
    }

    [Fact]
    public void GetAzureConnectUrl_GeneratesUniqueState_EachCall()
    {
        var sut = CreateService();
        var result1 = sut.GetAzureConnectUrl("https://connapse.local");
        var result2 = sut.GetAzureConnectUrl("https://connapse.local");

        result1.State.Should().NotBe(result2.State);
    }

    [Fact]
    public void GetAzureConnectUrl_GeneratesUniqueCodeVerifier_EachCall()
    {
        var sut = CreateService();
        var result1 = sut.GetAzureConnectUrl("https://connapse.local");
        var result2 = sut.GetAzureConnectUrl("https://connapse.local");

        result1.CodeVerifier.Should().NotBe(result2.CodeVerifier);
    }

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
