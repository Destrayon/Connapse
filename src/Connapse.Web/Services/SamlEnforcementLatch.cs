using Connapse.Core;
using Connapse.Core.Interfaces;

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
/// Enforcement is now its own stored flag, which fixes that going forward but leaves an upgrade
/// hole: an installation that configured SAML before the flag existed has no flag, and would come
/// back up unfiltered. That is the very failure being fixed, arrived at through the release notes
/// instead of a text box. So this runs at startup and writes the flag for anybody whose stored
/// settings say they had it working.
/// </para>
/// <para>
/// It only ever turns enforcement on, and only when the stored configuration is complete. Switching
/// it off is an administrator's decision and has its own path through the UI.
/// </para>
/// </remarks>
public sealed class SamlEnforcementLatch(
    IServiceScopeFactory scopes,
    ILogger<SamlEnforcementLatch> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

            // The stored row rather than the merged options view. IOptionsMonitor combines
            // appsettings, environment and database, so persisting it would copy values this
            // deployment holds elsewhere into its own settings row and freeze them there.
            var stored = await settings.GetAsync<SamlSignInSettings>("samlsignin", cancellationToken);

            // Nothing to carry forward. A deployment that never set this up stays unrestricted,
            // which is the documented default and the only legitimate one.
            if (stored is null || !stored.IsConfigured || stored.EnforcementEnabled)
                return;

            stored.EnforcementEnabled = true;

            // The store reloads the options monitor itself, so nothing here has to.
            await settings.SaveAsync("samlsignin", stored, cancellationToken);

            logger.LogInformation(
                "Per-user search permissions were already configured; recorded that this deployment enforces them");
        }
        catch (Exception ex)
        {
            // Never block startup. The consequence of failing here is that an administrator saves
            // the sign-in settings once to set the flag, which the setup page already asks for —
            // whereas a crash loop takes the whole deployment down over a bookkeeping write.
            logger.LogError(ex, "Could not record that per-user permissions are enforced");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
