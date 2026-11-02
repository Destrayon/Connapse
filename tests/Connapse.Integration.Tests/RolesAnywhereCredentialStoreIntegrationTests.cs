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

    [Fact]
    public async Task ReplaceRolesAnywhere_AfterAnotherSaveLandedFirst_WritesNothing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-rp-{Guid.NewGuid():N}"[..16];
        await store.SaveRolesAnywhereAsync(provider, Config, "KEY-1", null, null);
        var theirs = Config with { RoleArn = "arn:aws:iam::111:role/theirs" };
        var mine = Config with { RoleArn = "arn:aws:iam::111:role/mine" };

        // Both read Config; theirs lands first, so mine was checked against a credential that is gone.
        (await store.ReplaceRolesAnywhereAsync(provider, Config, theirs, "KEY-2", null, null)).Should().NotBeNull();
        (await store.ReplaceRolesAnywhereAsync(provider, Config, mine, "KEY-3", null, null)).Should().BeNull();

        RolesAnywhereCredentialMaterial? material = await store.GetRolesAnywhereMaterialAsync(provider);
        material!.Config.Should().Be(theirs);
        material.PrivateKeyPem.Should().Be("KEY-2");
    }

    [Fact]
    public async Task ReplaceRolesAnywhere_ExpectingNoneWhileOneIsStored_WritesNothing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-rn-{Guid.NewGuid():N}"[..16];

        (await store.ReplaceRolesAnywhereAsync(provider, null, Config, "KEY-1", null, null)).Should().NotBeNull();
        (await store.ReplaceRolesAnywhereAsync(provider, null, Config with { Region = "eu-west-2" }, "KEY-2", null, null)).Should().BeNull();

        (await store.GetRolesAnywhereAsync(provider)).Should().Be(Config);
    }

    [Fact]
    public async Task DeleteRolesAnywhere_AfterAnotherSaveLandedFirst_RemovesNothing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-dl-{Guid.NewGuid():N}"[..16];
        await store.SaveRolesAnywhereAsync(provider, Config, "KEY-1", null, null);
        var theirs = Config with { RoleArn = "arn:aws:iam::111:role/theirs" };
        await store.SaveRolesAnywhereAsync(provider, theirs, "KEY-2", null, null);

        (await store.DeleteRolesAnywhereAsync(provider, Config)).Should().BeFalse();
        (await store.GetRolesAnywhereAsync(provider)).Should().Be(theirs);

        (await store.DeleteRolesAnywhereAsync(provider, theirs)).Should().BeTrue();
        (await store.GetStatusAsync(provider)).Should().BeNull();
    }
}
