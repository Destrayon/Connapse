using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
    public async Task GetRolesAnywhere_WhenOnlyAccessKeyStored_ReturnsNull()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-ak-{Guid.NewGuid():N}"[..16];

        await store.SaveAsync(provider, "AKIAEXAMPLE", "sekret", "connapse-reader", null);

        (await store.GetRolesAnywhereAsync(provider)).Should().BeNull();
        (await store.GetRolesAnywhereMaterialAsync(provider)).Should().BeNull();
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
        (await store.GetRolesAnywhereMaterialAsync(provider)).Should().BeNull();
        (await store.GetSecretAsync(provider)).Should().Be("new-secret");
    }

    [Fact]
    public async Task InsertingRow_WithBothAccessKeyAndRolesAnywhereShapes_ViolatesCheckConstraint()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        string provider = $"aws-mix-{Guid.NewGuid():N}"[..16];

        // A row claiming to be both shapes at once: a non-blank access key alongside a full Roles
        // Anywhere config. The CHECK must reject this even though nothing in application code would
        // ever construct it — it is the last line of defense against a bug or a manual write.
        Func<Task> act = async () => await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO provider_credentials (
                provider, public_id, secret_protected, created_at,
                trust_anchor_arn, profile_arn, role_arn, region, certificate_pem, private_key_protected)
            VALUES (
                {0}, 'AKIAMIXED', 'mixed-secret', now(),
                'arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta',
                'arn:aws:rolesanywhere:us-east-1:111:profile/pf',
                'arn:aws:iam::111:role/connapse', 'us-east-1', 'cert-pem', 'key-ciphertext')
            """, provider);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }
}
