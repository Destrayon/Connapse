using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Settings;
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
public class CloudEnforcementLatchTests
{
    private readonly ISettingsStore store = Substitute.For<ISettingsStore>();

    /// <summary>Reads the database successfully unless a test says otherwise.</summary>
    private readonly ISettingsReloader reloader = Substitute.For<ISettingsReloader>();

    private static SamlSignInSettings Complete() => new()
    {
        EntityId = "https://connapse.example.com/saml/connapse",
        AcsUrl = "https://connapse.example.com/api/v1/auth/cloud/aws/acs",
        IdpEntityId = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSingleSignOnUrl = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSigningCertificate = "MIIDBTCCAe2gAwIBAgIFEXAMPLE",
    };

    private static AzureAdSignInSettings CompleteAzureAd() => new()
    {
        TenantId = "tenant-1",
        ClientId = "client-1",
        RedirectUri = "https://connapse.example.com/api/v1/auth/cloud/azure/cb",
        ClientCertificatePath = "cert.pem",
    };

    private static IOptionsMonitor<T> Monitor<T>(T value) where T : class
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private (CloudEnforcementLatch Latch, EnforcementMigration Migration) Build(
        SamlSignInSettings signIn,
        AzureAdSignInSettings? azureAd = null,
        bool alreadyEnforcing = false,
        bool azureAlreadyEnforcing = false,
        bool settingsReadable = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);

        var migration = new EnforcementMigration();
        reloader.Reload().Returns(settingsReadable);

        return (new CloudEnforcementLatch(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Monitor(signIn),
            Monitor(azureAd ?? new AzureAdSignInSettings()),
            Monitor(new PermissionEnforcementSettings
            {
                IsEnforcing = alreadyEnforcing,
                AzureEnforcing = azureAlreadyEnforcing,
            }),
            migration,
            reloader,
            NullLogger<CloudEnforcementLatch>.Instance), migration);
    }

    /// <summary>What the latch asked the store to hold, computed the way the store would: the merge
    /// function it passed, applied to an empty row.</summary>
    private Task<PermissionEnforcementSettings?> SavedMarker()
    {
        var calls = store.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ISettingsStore.UpdateAsync))
            .Select(c => c.GetArguments()[1] as Func<PermissionEnforcementSettings?, PermissionEnforcementSettings>)
            .Where(f => f is not null)
            .Select(f => f!(null))
            .ToList();

        return Task.FromResult(calls.LastOrDefault());
    }

    [Fact]
    public async Task TheMarkerIsMerged_SoALatchAlreadyStoredIsNeverSwitchedOff()
    {
        // Azure was latched by somebody else — the Providers page, another process — and this
        // deployment only configures SAML. Writing the record from this snapshot would turn Azure
        // back off; the merge keeps whatever is already on.
        var (latch, _) = Build(Complete());
        await latch.StartAsync(CancellationToken.None);

        var merge = store.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ISettingsStore.UpdateAsync))
            .Select(c => c.GetArguments()[1] as Func<PermissionEnforcementSettings?, PermissionEnforcementSettings>)
            .Single(f => f is not null)!;

        var merged = merge(new PermissionEnforcementSettings { IsEnforcing = false, AzureEnforcing = true });
        merged.IsEnforcing.Should().BeTrue();
        merged.AzureEnforcing.Should().BeTrue();
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
    public async Task Latches_When_OnlyAzureAd_Configured()
    {
        // SAML not configured, Azure AD configured, not yet marked → the latch turns Azure enforcement
        // on. Crucially it latches Azure INDEPENDENTLY: SAML stays off, so the AWS resolver keeps its
        // corpus unfiltered rather than denying every S3 document.
        var (latch, migration) = Build(new SamlSignInSettings(), azureAd: CompleteAzureAd());
        await latch.StartAsync(CancellationToken.None);

        PermissionEnforcementSettings marker = (await SavedMarker())!;
        marker.AzureEnforcing.Should().BeTrue();
        marker.IsEnforcing.Should().BeFalse("configuring Azure AD must not latch SAML/AWS enforcement");
        migration.Determined.Should().BeTrue();
    }

    [Fact]
    public async Task Latches_Saml_Independently_LeavingAzureOff()
    {
        // The mirror of the Azure-only case: SAML configured, Azure AD blank → SAML latches, Azure
        // stays off so the Azure resolver keeps its corpus unfiltered.
        var (latch, _) = Build(Complete());
        await latch.StartAsync(CancellationToken.None);

        PermissionEnforcementSettings marker = (await SavedMarker())!;
        marker.IsEnforcing.Should().BeTrue();
        marker.AzureEnforcing.Should().BeFalse("configuring SAML must not latch Azure enforcement");
    }

    [Fact]
    public async Task Latches_BothProviders_WhenBothConfigured()
    {
        var (latch, _) = Build(Complete(), azureAd: CompleteAzureAd());
        await latch.StartAsync(CancellationToken.None);

        PermissionEnforcementSettings marker = (await SavedMarker())!;
        marker.IsEnforcing.Should().BeTrue();
        marker.AzureEnforcing.Should().BeTrue();
    }

    [Fact]
    public async Task AlreadySamlEnforcing_AddingAzure_LatchesAzureWithoutClearingSaml()
    {
        // A deployment that was already SAML-enforcing later configures Azure AD. The old early-out
        // ("already enforcing → return") would have skipped Azure entirely; the per-provider latch
        // must record Azure while leaving SAML latched.
        var (latch, _) = Build(Complete(), azureAd: CompleteAzureAd(), alreadyEnforcing: true);
        await latch.StartAsync(CancellationToken.None);

        PermissionEnforcementSettings marker = (await SavedMarker())!;
        marker.IsEnforcing.Should().BeTrue();
        marker.AzureEnforcing.Should().BeTrue();
    }

    [Fact]
    public async Task AlreadyEnforcingBoth_WritesNothing()
    {
        // Steady-state boot: both markers already set and both providers configured → no change, no
        // write (never copy a sign-in value into a shadowing row).
        var (latch, migration) = Build(
            Complete(), azureAd: CompleteAzureAd(), alreadyEnforcing: true, azureAlreadyEnforcing: true);
        await latch.StartAsync(CancellationToken.None);

        (await SavedMarker()).Should().BeNull();
        migration.Determined.Should().BeTrue();
    }

    [Fact]
    public async Task AzureOnly_KeepsSamlGateUnfiltered_EndToEnd()
    {
        // The end-to-end proof for the regression: run the real latch for an Azure-configured /
        // SAML-unconfigured deployment, then feed the marker it produced into both resolver gates.
        // AWS/SAML must read NotEnforcing (S3 stays visible — completeness), Azure must enforce.
        var (latch, _) = Build(new SamlSignInSettings(), azureAd: CompleteAzureAd());
        await latch.StartAsync(CancellationToken.None);

        PermissionEnforcementSettings marker = (await SavedMarker())!;
        marker.StateFor(new SamlSignInSettings()).Should().Be(EnforcementState.NotEnforcing);
        marker.StateForAzure(azureConfigured: true).Should().Be(EnforcementState.Enforcing);
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
    public async Task WhenTheSettingsCannotBeRead_LeavesTheStateUndetermined()
    {
        // The configuration is first loaded before the host starts, and a database that is
        // unreachable at that moment leaves every stored value at its default without an error. A
        // deployment that had been enforcing then reads as "never configured", which the branch for
        // fresh installs would have recorded as unrestricted. Undetermined denies instead.
        var (latch, migration) = Build(new SamlSignInSettings(), settingsReadable: false);
        await latch.StartAsync(CancellationToken.None);

        migration.Determined.Should().BeFalse();
        (await SavedMarker()).Should().BeNull();
    }

    [Fact]
    public async Task ReadsTheStoredSettingsAgainBeforeDeciding()
    {
        // Migrations run between the first configuration load and this service, so the values
        // it decides from must come from a load that happened after them.
        var (latch, _) = Build(Complete());
        await latch.StartAsync(CancellationToken.None);

        reloader.Received(1).Reload();
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
        store.UpdateAsync(Arg.Any<string>(), Arg.Any<Func<PermissionEnforcementSettings?, PermissionEnforcementSettings>>(), Arg.Any<CancellationToken>())
            .Returns<Task<PermissionEnforcementSettings>>(_ => throw new InvalidOperationException("database unavailable"));

        var (latch, migration) = Build(Complete());
        await latch.StartAsync(CancellationToken.None);

        migration.Determined.Should().BeFalse();
    }

    [Fact]
    public async Task WhenThePersistFails_StartupStillCompletes()
    {
        // Refusing to answer is the version of "never block startup" that does not fail open. The
        // deployment comes up, and searches deny until somebody looks.
        store.UpdateAsync(Arg.Any<string>(), Arg.Any<Func<PermissionEnforcementSettings?, PermissionEnforcementSettings>>(), Arg.Any<CancellationToken>())
            .Returns<Task<PermissionEnforcementSettings>>(_ => throw new InvalidOperationException("database unavailable"));

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
        await store.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Func<SamlSignInSettings?, SamlSignInSettings>>(), Arg.Any<CancellationToken>());

        await store.Received(1).UpdateAsync(
            PermissionEnforcementSettings.Category,
            Arg.Any<Func<PermissionEnforcementSettings?, PermissionEnforcementSettings>>(),
            Arg.Any<CancellationToken>());
    }
}
