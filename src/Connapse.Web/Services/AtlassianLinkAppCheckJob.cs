using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Web.Services;

/// <summary>
/// Hangfire's daily check of the stored Atlassian linking app. Without it the card says "Ready"
/// until someone's sign-in fails or an administrator presses Recheck, so a secret revoked in the
/// developer console goes unnoticed. Records the answer exactly as Recheck does.
/// </summary>
public sealed class AtlassianLinkAppCheckJob(AtlassianLinkAppService linkApp, ILogger<AtlassianLinkAppCheckJob> logger)
{
    public const string RecurringJobId = "atlassian-link-app-check";

    public async Task RunAsync(CancellationToken ct)
    {
        var check = await linkApp.ScheduledRecheckAsync(ct);
        switch (check?.Result)
        {
            case null:
                return;
            case AtlassianLinkAppCheck.Rejected:
                logger.LogWarning(
                    "Atlassian rejected the stored linking app ({Detail}); nobody new can link an account until it is replaced",
                    check.Detail);
                break;
            case AtlassianLinkAppCheck.Unknown:
                logger.LogWarning("Couldn't reach Atlassian to check the linking app ({Detail}); trying again tomorrow", check.Detail);
                break;
            default:
                logger.LogInformation("Atlassian accepted the stored linking app");
                break;
        }
    }
}
