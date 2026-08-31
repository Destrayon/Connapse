using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Web.Tests.Services;

/// <summary>
/// Carrying an existing deployment's per-user permissions across the upgrade that separated
/// "enforcing" from "configured".
/// </summary>
/// <remarks>
/// The first version of this read the stored settings row rather than the effective configuration,
/// to avoid copying appsettings and environment values into a database row that would then shadow
/// them. Avoiding that was right; deciding from the row was not — a deployment configured entirely
/// through environment variables has no row, so it read as "never configured" and came back
/// unrestricted. These tests exist because that version passed everything else.
/// </remarks>
[Trait("Category", "Unit")]
public class SamlEnforcementLatchTests
{
    private readonly ISettingsStore store = Substitute.For<ISettingsStore>();

    private static SamlSignInSettings Complete() => new()
    {
        EntityId = "https://connapse.example.com/saml/connapse",
        AcsUrl = "https://connapse.example.com/api/v1/auth/cloud/aws/acs",
        IdpEntityId = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSingleSignOnUrl = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSigningCertificate = "MIIDBTCCAe2gAwIBAgIFEXAMPLE",
    };

    private static IOptionsMonitor<T> Monitor<T>(T value) where T : class
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private (SamlEnforcementLatch Latch, EnforcementMigration Migration) Build(
        SamlSignInSettings signIn, bool alreadyEnforcing = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);

        var migration = new EnforcementMigration();

        return (new SamlEnforcementLatch(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Monitor(signIn),
            Monitor(new PermissionEnforcementSettings { IsEnforcing = alreadyEnforcing }),
            migration,
            NullLogger<SamlEnforcementLatch>.Instance), migration);
    }

    private Task<PermissionEnforcementSettings?> SavedMarker()
    {
        var calls = store.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ISettingsStore.SaveAsync))
            .Select(c => c.GetArguments()[1] as PermissionEnforcementSettings)
            .Where(v => v is not null)
            .ToList();

        return Task.FromResult(calls.LastOrDefault());
    }

    [Fact]
    public async Task ConfiguredThroughEnvironmentOnly_StillLatches()
    {
        // The bug. IOptionsMonitor merges appsettings, environment and database; this deployment
        // was enforcing before the marker existed and has no settings row at all. Reading the row
        // instead of the merged view brought it back unrestricted.
        store.GetAsync<SamlSignInSettings>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((SamlSignInSettings?)null);

        var (latch, migration) = Build(Complete());
        await latch.StartAsync(CancellationToken.None);

        (await SavedMarker())!.IsEnforcing.Should().BeTrue();
        migration.Determined.Should().BeTrue();
    }

    [Fact]
    public async Task NeverConfigured_DoesNotLatch()
    {
        // A deployment that never set this up stays unrestricted, which is the documented default.
        var (latch, migration) = Build(new SamlSignInSettings());
        await latch.StartAsync(CancellationToken.None);

        (await SavedMarker()).Should().BeNull();
        migration.Determined.Should().BeTrue("a fresh install's state is known, not unknown");
    }

    [Fact]
    public async Task AlreadyEnforcing_WritesNothing()
    {
        var (latch, migration) = Build(Complete(), alreadyEnforcing: true);
        await latch.StartAsync(CancellationToken.None);

        (await SavedMarker()).Should().BeNull();
        migration.Determined.Should().BeTrue();
    }

    [Fact]
    public async Task WhenThePersistFails_LeavesTheStateUndetermined()
    {
        // Which makes the resolver deny. The first version logged this and carried on with
        // enforcement off, so one transient database error at boot opened the whole corpus.
        store.SaveAsync(Arg.Any<string>(), Arg.Any<PermissionEnforcementSettings>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("database unavailable"));

        var (latch, migration) = Build(Complete());
        await latch.StartAsync(CancellationToken.None);

        migration.Determined.Should().BeFalse();
    }

    [Fact]
    public async Task WhenThePersistFails_StartupStillCompletes()
    {
        // Refusing to answer is the version of "never block startup" that does not fail open. The
        // deployment comes up, and searches deny until somebody looks.
        store.SaveAsync(Arg.Any<string>(), Arg.Any<PermissionEnforcementSettings>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("database unavailable"));

        var (latch, _) = Build(Complete());

        await latch.Awaiting(l => l.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task ItWritesOnlyTheMarker_NeverTheSignInSettings()
    {
        // Recording enforcement must not rewrite a SAML value. The database outranks the
        // environment, so a row written here would shadow environment configuration permanently.
        var (latch, _) = Build(Complete());
        await latch.StartAsync(CancellationToken.None);

        await store.DidNotReceive().SaveAsync(
            Arg.Any<string>(), Arg.Any<SamlSignInSettings>(), Arg.Any<CancellationToken>());

        await store.Received(1).SaveAsync(
            PermissionEnforcementSettings.Category,
            Arg.Any<PermissionEnforcementSettings>(),
            Arg.Any<CancellationToken>());
    }
}
