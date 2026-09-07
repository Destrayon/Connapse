using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Settings;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>
/// Records, once, that a deployment which already had per-user permissions working is enforcing
/// them.
/// </summary>
/// <remarks>
/// Filtering used to be switched on by the SAML settings being complete, which meant the same five
/// fields answered both "is sign-in set up" and "are permissions enforced". Blanking one of them —
/// clearing the signing certificate to paste a rotated one, say — therefore turned a filtered
/// deployment into an unfiltered one, silently, at the same moment sign-in broke.
/// <para>
/// Enforcement is now its own stored marker, which fixes that going forward but leaves an upgrade
/// hole: an installation that configured SAML before the marker existed has none, and would come
/// back up unfiltered. That is the very failure being fixed, arriving through the release notes
/// instead of a text box. So this runs at startup and writes the marker for anybody whose
/// configuration says they had it working.
/// </para>
/// <para>
/// <b>Read from the merged configuration, not the database row.</b> An earlier version of this
/// examined only the stored row, to avoid copying appsettings and environment values into a
/// database row that would then shadow them. Avoiding that was right; deciding from the row was
/// not. A deployment configured entirely through environment variables — the ordinary way to run a
/// container — has no row, so it read as "never configured" and came back unrestricted. Splitting
/// the marker into its own category removed the reason to read the row at all: the question is
/// answered from the effective configuration, and the answer is written somewhere that disturbs
/// nothing.
/// </para>
/// <para>
/// It only ever turns enforcement on, and only when the effective configuration is complete.
/// Switching it off is an administrator's decision and has its own path through the UI.
/// </para>
/// <para>
/// Enforcement is opt-in via SAML or Azure AD, either one. A deployment that configured either
/// identity provider had per-user permissions working, so either configuration latches it.
/// </para>
/// </remarks>
public sealed class CloudEnforcementLatch(
    IServiceScopeFactory scopes,
    IOptionsMonitor<SamlSignInSettings> signIn,
    IOptionsMonitor<AzureAdSignInSettings> azureAd,
    IOptionsMonitor<PermissionEnforcementSettings> enforcement,
    EnforcementMigration migration,
    ISettingsReloader settingsReloader,
    ILogger<CloudEnforcementLatch> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Read the stored settings again, now that migrations have run, and refuse to decide
            // from a load that did not reach the database. The configuration is first loaded before
            // the host starts, and a database that is briefly unreachable at that moment leaves
            // every stored value at its default without any error: a deployment that had been
            // enforcing then looks exactly like one that never set sign-in up, and the branch below
            // would record it as unrestricted. Left undetermined instead, searches are refused
            // until the next successful reload -- the same posture as the catch below.
            if (!settingsReloader.Reload())
            {
                logger.LogError(
                    "Could not read the stored settings, so whether per-user permissions are enforced is unknown; searches will be refused until the database can be read");
                return;
            }

            // Enforcement is opt-in and latches PER identity provider, independently. Each provider
            // that is configured now, or was already marked as enforcing, stays enforcing; a provider
            // that was never configured stays unfiltered. The two markers must not be conflated:
            // sharing one bit would make configuring Azure AD flip SAML/AWS into "enforcing but
            // unusable", hiding every S3 document (and vice versa).
            PermissionEnforcementSettings current = enforcement.CurrentValue;
            bool samlLatched = current.IsEnforcing || signIn.CurrentValue.IsConfigured;
            bool azureLatched = current.AzureEnforcing || azureAd.CurrentValue.IsConfigured;

            // Nothing new to latch (a fresh install, or a steady-state boot): leave the stored row
            // untouched so no SAML/Azure value is ever copied into a shadowing row.
            if (samlLatched == current.IsEnforcing && azureLatched == current.AzureEnforcing)
            {
                migration.Complete();
                return;
            }

            // Configured but unmarked: filtering was in force under the old rule, so it stays in
            // force. Only the markers are written -- no sign-in value is copied anywhere.
            await using var scope = scopes.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

            await settings.SaveAsync(
                PermissionEnforcementSettings.Category,
                new PermissionEnforcementSettings { IsEnforcing = samlLatched, AzureEnforcing = azureLatched },
                cancellationToken);

            migration.Complete();

            logger.LogInformation(
                "Per-user search permissions were already configured; recorded that this deployment enforces them (SAML={SamlEnforcing}, Azure={AzureEnforcing})",
                samlLatched, azureLatched);
        }
        catch (Exception ex)
        {
            // Deliberately does not call Complete(), and deliberately does not rethrow. The state is
            // now unknown, and unknown enforces: searches are refused until somebody looks, rather
            // than answered without filtering.
            //
            // The first version of this logged and carried on with the flag false, on the grounds
            // that startup must never be blocked. Startup still is not blocked -- but on a
            // deployment that had been filtering, that choice turned one transient database error
            // at boot into the whole corpus being readable, with a single log line to show for it.
            logger.LogError(ex,
                "Could not establish whether per-user permissions are enforced; searches will be refused until this is resolved");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
