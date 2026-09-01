using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class RolesAnywhereCredentialStoreIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly RolesAnywhereConfig Config = new(
        CertificatePem: "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----",
        TrustAnchorArn: "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
        ProfileArn: "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
        RoleArn: "arn:aws:iam::111:role/connapse",
        Region: "us-east-1");

    [Fact]
    public async Task SaveRolesAnywhere_ThenGet_RoundTripsConfigAndDecryptsPrivateKey()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-ra-{Guid.NewGuid():N}"[..16];

        await store.SaveRolesAnywhereAsync(provider, Config, "PRIVATE-KEY-PEM", "connapse-role", null);

        RolesAnywhereConfig? read = await store.GetRolesAnywhereAsync(provider);
        read.Should().Be(Config);
        (await store.GetRolesAnywherePrivateKeyAsync(provider)).Should().Be("PRIVATE-KEY-PEM");
    }

    [Fact]
    public async Task GetRolesAnywhere_WhenOnlyAccessKeyStored_ReturnsNull()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-ak-{Guid.NewGuid():N}"[..16];

        await store.SaveAsync(provider, "AKIAEXAMPLE", "sekret", "connapse-reader", null);

        (await store.GetRolesAnywhereAsync(provider)).Should().BeNull();
        (await store.GetRolesAnywherePrivateKeyAsync(provider)).Should().BeNull();
    }

    [Fact]
    public async Task SaveRolesAnywhere_ClearsAnyPriorAccessKey()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-sw-{Guid.NewGuid():N}"[..16];

        await store.SaveAsync(provider, "AKIAOLD", "old-secret", "connapse-reader", null);
        await store.SaveRolesAnywhereAsync(provider, Config, "PRIVATE-KEY-PEM", "connapse-role", null);

        (await store.GetSecretAsync(provider)).Should().BeNullOrEmpty(); // access-key secret cleared
        (await store.GetRolesAnywhereAsync(provider)).Should().Be(Config);
    }

    [Fact]
    public async Task SaveAccessKey_AfterRolesAnywhere_ClearsRolesAnywhereConfig()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-rev-{Guid.NewGuid():N}"[..16];

        await store.SaveRolesAnywhereAsync(provider, Config, "PRIVATE-KEY-PEM", "connapse-role", null);
        await store.SaveAsync(provider, "AKIANEW", "new-secret", "connapse-reader", null);

        (await store.GetRolesAnywhereAsync(provider)).Should().BeNull();
        (await store.GetRolesAnywherePrivateKeyAsync(provider)).Should().BeNullOrEmpty();
        (await store.GetSecretAsync(provider)).Should().Be("new-secret");
    }
}
