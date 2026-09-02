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

        RolesAnywhereCredentialMaterial? material = await store.GetRolesAnywhereMaterialAsync(provider);
        material.Should().NotBeNull();
        material!.Config.Should().Be(Config);
        material.PrivateKeyPem.Should().Be("PRIVATE-KEY-PEM");
    }

    [Fact]
    public async Task Reads_WhenNothingStored_ReturnNull()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-none-{Guid.NewGuid():N}"[..16];

        (await store.GetRolesAnywhereAsync(provider)).Should().BeNull();
        (await store.GetRolesAnywhereMaterialAsync(provider)).Should().BeNull();
        (await store.GetStatusAsync(provider)).Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_AfterSave_ReportsCreatedAndUnverified()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-st-{Guid.NewGuid():N}"[..16];

        await store.SaveRolesAnywhereAsync(provider, Config, "PRIVATE-KEY-PEM", "connapse-role", null);

        ProviderCredentialStatus? status = await store.GetStatusAsync(provider);
        status.Should().NotBeNull();
        status!.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        status.VerifiedAt.Should().BeNull(); // never honoured yet
    }
}
