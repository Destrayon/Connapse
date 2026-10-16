using Connapse.Core.Interfaces;
using Connapse.Identity.Data.Entities;
using Connapse.Storage.Connectors.Custom;
using Hangfire;
using Microsoft.AspNetCore.Identity;

namespace Connapse.Web.Services;

/// <summary>
/// Drains the unlink calls owed to custom providers for deleted users. Each entry is retried with growing
/// waits until its middle man acknowledges; one still failing a day after the user was deleted is reported
/// once, as an error and an audit event, and keeps being retried. Entries still waiting for a deletion that
/// was never confirmed — a crash between the deletion and its confirmation — are settled by asking whether
/// the user still exists.
/// </summary>
public sealed class CustomLinkRemovalJob(
    ICustomLinkRemovalStore removals,
    CustomLinkService links,
    UserManager<ConnapseUser> users,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<CustomLinkRemovalJob> logger)
{
    public const string RecurringJobId = "custom-link-removals";
    public const string StuckEvent = "custom_provider.unlink_stuck";
    public static readonly TimeSpan AlertAfter = TimeSpan.FromDays(1);

    /// <summary>How long an entry may wait for its deletion to be confirmed before the job checks the user itself.</summary>
    public static readonly TimeSpan SettleWaitingAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);
    private const int BatchSize = 100;

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(CancellationToken ct)
    {
        await SettleWaitingAsync(time.GetUtcNow().UtcDateTime, ct);

        // Read after settling, so entries it just activated are due in this run.
        DateTime now = time.GetUtcNow().UtcDateTime;
        foreach (CustomLinkRemoval removal in await removals.DueAsync(now, BatchSize, ct))
        {
            try
            {
                await links.UnlinkQueuedAsync(removal, ct);
                await removals.CompleteAsync(removal.Id, ct);
                logger.LogInformation("Custom provider {ProviderId} removed the link of deleted user {UserId}", removal.ProviderId, removal.UserId);
            }
            catch (Exception ex) when (ex is CustomSourceException or HttpRequestException)
            {
                int attempts = removal.Attempts + 1;
                TimeSpan wait = WaitAfter(attempts);
                bool alert = removal.AlertedAt is null && now - removal.CreatedAt >= AlertAfter;
                if (alert)
                {
                    logger.LogError(ex,
                        "Custom provider {ProviderId} has not removed the link of deleted user {UserId} after {Attempts} attempts over a day; still retrying",
                        removal.ProviderId, removal.UserId, attempts);
                    await audit.LogAsync(StuckEvent, "custom_provider", removal.ProviderId.ToString(),
                        new { removal.UserId, Attempts = attempts }, ct);
                }
                else
                {
                    logger.LogWarning(ex, "Custom provider {ProviderId} did not remove the link of deleted user {UserId}; retrying in {Wait}",
                        removal.ProviderId, removal.UserId, wait);
                }

                await removals.RetryLaterAsync(removal.Id, now + wait, ex.Message, alert, ct);
            }
        }
    }

    // A user still present means the deletion failed or never ran: nothing is owed. A user gone means it ran.
    private async Task SettleWaitingAsync(DateTime now, CancellationToken ct)
    {
        foreach (Guid userId in await removals.WaitingUsersAsync(now - SettleWaitingAfter, ct))
        {
            if (await users.FindByIdAsync(userId.ToString()) is null)
                await removals.ActivateForUserAsync(userId, ct);
            else
                await removals.CancelForUserAsync(userId, ct);
        }
    }

    /// <summary>2, 4, 8 … minutes after each failed attempt, at most an hour.</summary>
    public static TimeSpan WaitAfter(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Min(attempts, 10)), MaxWait.TotalMinutes));
}
