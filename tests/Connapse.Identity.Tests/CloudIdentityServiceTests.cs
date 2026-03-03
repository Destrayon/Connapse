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
    private readonly IOptionsMonitor<AwsSsoSettings> _awsSsoOptions;
    private readonly IAwsSsoClientRegistrar _awsSsoRegistrar = Substitute.For<IAwsSsoClientRegistrar>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    private readonly AzureAdSettings _azureAdSettings = new()
    {
        ClientId = "test-client-id",
        TenantId = "test-tenant-id",
        ClientSecret = "test-client-secret"
    };

    private readonly AwsSsoSettings _awsSsoSettings = new()
    {
        IssuerUrl = "https://d-123456.awsapps.com/start",
        Region = "us-east-1",
        ClientId = "test-sso-client-id",
        ClientSecret = "test-sso-client-secret",
        ClientSecretExpiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds()
    };

    public CloudIdentityServiceTests()
    {
        _dpProvider = new EphemeralDataProtectionProvider();

        _azureAdOptions = Substitute.For<IOptionsMonitor<AzureAdSettings>>();
        _azureAdOptions.CurrentValue.Returns(_azureAdSettings);

        _awsSsoOptions = Substitute.For<IOptionsMonitor<AwsSsoSettings>>();
        _awsSsoOptions.CurrentValue.Returns(_awsSsoSettings);

        _awsSsoRegistrar.EnsureRegisteredAsync(Arg.Any<AwsSsoSettings>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<AwsSsoSettings>());
    }

    private ICloudIdentityService CreateService() =>
        new CloudIdentityService(_store, _dpProvider, _azureAdOptions, _awsSsoOptions,
            _awsSsoRegistrar, _httpClientFactory, NullLogger<CloudIdentityService>.Instance);

    // ── IsAwsSsoConfigured ────────────────────────────────────────────────

    [Fact]
    public void IsAwsSsoConfigured_WithIssuerUrlAndRegion_ReturnsTrue()
    {
        var sut = CreateService();
        sut.IsAwsSsoConfigured().Should().BeTrue();
    }

    [Fact]
    public void IsAwsSsoConfigured_MissingIssuerUrl_ReturnsFalse()
    {
        _awsSsoSettings.IssuerUrl = "";
        var sut = CreateService();
        sut.IsAwsSsoConfigured().Should().BeFalse();
    }

    [Fact]
    public void IsAwsSsoConfigured_MissingRegion_ReturnsFalse()
    {
        _awsSsoSettings.Region = "";
        var sut = CreateService();
        sut.IsAwsSsoConfigured().Should().BeFalse();
    }

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

    // ── StartAwsDeviceAuthAsync ───────────────────────────────────────────

    [Fact]
    public async Task StartAwsDeviceAuthAsync_CallsEnsureRegistered()
    {
        _awsSsoRegistrar.StartDeviceAuthorizationAsync(Arg.Any<AwsSsoSettings>(), Arg.Any<CancellationToken>())
            .Returns(new AwsDeviceAuthorizationResult("device-code", "USER-CODE", "https://device.sso.us-east-1.amazonaws.com", "https://device.sso.us-east-1.amazonaws.com/?user_code=USER-CODE", 600, 5));

        var sut = CreateService();
        await sut.StartAwsDeviceAuthAsync();

        await _awsSsoRegistrar.Received(1).EnsureRegisteredAsync(
            Arg.Any<AwsSsoSettings>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAwsDeviceAuthAsync_ReturnsDeviceAuthResult()
    {
        _awsSsoRegistrar.StartDeviceAuthorizationAsync(Arg.Any<AwsSsoSettings>(), Arg.Any<CancellationToken>())
            .Returns(new AwsDeviceAuthorizationResult("device-code", "ABCD-EFGH", "https://device.sso.us-east-1.amazonaws.com", "https://device.sso.us-east-1.amazonaws.com/?user_code=ABCD-EFGH", 600, 5));

        var sut = CreateService();
        var result = await sut.StartAwsDeviceAuthAsync();

        result.UserCode.Should().Be("ABCD-EFGH");
        result.DeviceCode.Should().Be("device-code");
        result.VerificationUri.Should().Contain("device.sso");
        result.ExpiresInSeconds.Should().Be(600);
        result.IntervalSeconds.Should().Be(5);
    }

    [Fact]
    public async Task PollAwsDeviceAuthAsync_Pending_ReturnsNull()
    {
        _awsSsoRegistrar.PollForTokenAsync(Arg.Any<AwsSsoSettings>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var sut = CreateService();
        var result = await sut.PollAwsDeviceAuthAsync(Guid.NewGuid(), "device-code");

        result.Should().BeNull();
    }

    [Fact]
    public async Task PollAwsDeviceAuthAsync_Complete_StoresIdentityAndReturnsDto()
    {
        var userId = Guid.NewGuid();
        _awsSsoRegistrar.PollForTokenAsync(Arg.Any<AwsSsoSettings>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("access-token-123");
        _awsSsoRegistrar.ListUserAccountsAsync(Arg.Any<AwsSsoSettings>(), "access-token-123", Arg.Any<CancellationToken>())
            .Returns(new AwsSsoUserInfo("111222333444", "111222333444", "Test Account"));

        _store.GetByUserAndProviderAsync(userId, CloudProvider.AWS, Arg.Any<CancellationToken>())
            .Returns((UserCloudIdentityEntity?)null);
        _store.CreateAsync(Arg.Any<UserCloudIdentityEntity>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var e = callInfo.Arg<UserCloudIdentityEntity>();
                e.Id = Guid.NewGuid();
                e.CreatedAt = DateTime.UtcNow;
                return e;
            });

        var sut = CreateService();
        var result = await sut.PollAwsDeviceAuthAsync(userId, "device-code");

        result.Should().NotBeNull();
        result!.Provider.Should().Be(CloudProvider.AWS);
        result.Data.AccountId.Should().Be("111222333444");
        result.Data.DisplayName.Should().Be("Test Account");
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
