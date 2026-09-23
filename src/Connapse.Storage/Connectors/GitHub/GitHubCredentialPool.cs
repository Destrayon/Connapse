using System.Collections.Concurrent;
using System.Globalization;

namespace Connapse.Storage.Connectors.GitHub;

/// <summary>
/// Which installations a read may use. A public repository can be read as any installation, so
/// the source's own is merely preferred; a private one must be read as the installation that
/// covers it, never another — borrowing a token would read through a different organisation's
/// grant.
/// </summary>
public sealed record GitHubAccess(long? PinnedInstallationId, long? PreferredInstallationId)
{
    public static GitHubAccess Public(long? preferredInstallationId) => new(null, preferredInstallationId);

    public static GitHubAccess Pinned(long installationId) => new(installationId, installationId);
}

/// <summary>One installation's token, for one request.</summary>
public sealed record GitHubLease(long InstallationId, string Token);

/// <summary>
/// Every GitHub installation's remaining budget, shared by the whole server, and the choice of
/// which to spend next.
/// <para>
/// Budgets are per installation (5,000 an hour and up), so spreading public reads across them
/// raises what one Connapse can sync. Each response's <c>x-ratelimit-*</c> headers keep the count
/// current; the installation with the most left is used, the source's own first when it has any.
/// A spent installation sits out until its reset; a refused one (its token rejected, the App
/// uninstalled) sits out for a few minutes and is tried again. When nothing is left the caller is
/// told when the earliest budget resets, and stops there — the same outcome as the anonymous
/// limit, only much later.
/// </para>
/// </summary>
public sealed class GitHubCredentialPool(ConnapseGitHubApp app, TimeProvider? clock = null)
{
    private static readonly TimeSpan InstallationListLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefusedCooldown = TimeSpan.FromMinutes(5);

    /// <summary>What an installation is assumed to have before its first response says otherwise.</summary>
    private const int AssumedBudget = 5000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<long, Budget> _budgets = new();
    private (IReadOnlyList<long> Ids, DateTimeOffset LoadedAt)? _installations;

    private sealed record Budget(int? Remaining, DateTimeOffset? ResetAt, DateTimeOffset? RefusedUntil);

    /// <summary>
    /// A token from the best installation <paramref name="access"/> allows, skipping any already
    /// tried for this request.
    /// </summary>
    /// <exception cref="GitHubRateLimitedException">Every allowed installation is spent or refused.</exception>
    public async Task<GitHubLease> AcquireAsync(
        GitHubAccess access, IReadOnlySet<long> alreadyTried, CancellationToken ct = default)
    {
        IReadOnlyList<long> allowed = access.PinnedInstallationId is { } pinned
            ? [pinned]
            : await InstallationsAsync(ct);

        DateTimeOffset now = _clock.GetUtcNow();
        var usable = allowed
            .Where(id => !alreadyTried.Contains(id))
            .Select(id => (Id: id, Left: Left(id, now)))
            .Where(c => c.Left > 0)
            .OrderByDescending(c => c.Id == access.PreferredInstallationId)
            .ThenByDescending(c => c.Left)
            .ToList();

        if (usable.Count == 0)
            throw new GitHubRateLimitedException(EarliestReset(allowed, now));

        var token = await app.GetInstallationTokenAsync(usable[0].Id, ct);
        return new GitHubLease(usable[0].Id, token.Token);
    }

    /// <summary>Records what a response says about its installation's budget.</summary>
    public void Observe(long installationId, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-ratelimit-remaining", out var remainingValues)
            || !int.TryParse(remainingValues.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out int remaining))
            return;

        DateTimeOffset? resetAt = response.Headers.TryGetValues("x-ratelimit-reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out long epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null;

        // A secondary limit says retry-after without the primary count reaching zero; treat the
        // installation as spent until then.
        if (response.Headers.RetryAfter?.Delta is { } wait)
        {
            remaining = 0;
            resetAt = _clock.GetUtcNow() + wait;
        }

        _budgets.AddOrUpdate(installationId,
            _ => new Budget(remaining, resetAt, null),
            (_, old) => old with { Remaining = remaining, ResetAt = resetAt });
    }

    /// <summary>GitHub refused this installation's token: drop it and rest the installation briefly.</summary>
    public void Refused(long installationId)
    {
        app.Forget(installationId);
        DateTimeOffset until = _clock.GetUtcNow() + RefusedCooldown;
        _budgets.AddOrUpdate(installationId,
            _ => new Budget(null, null, until),
            (_, old) => old with { RefusedUntil = until });
    }

    private int Left(long id, DateTimeOffset now)
    {
        if (!_budgets.TryGetValue(id, out var budget))
            return AssumedBudget;

        if (budget.RefusedUntil is { } refused && refused > now)
            return 0;

        // A budget past its reset is whole again, whatever the last response said.
        if (budget.ResetAt is { } reset && reset <= now)
            return AssumedBudget;

        return budget.Remaining ?? AssumedBudget;
    }

    private DateTimeOffset? EarliestReset(IReadOnlyList<long> allowed, DateTimeOffset now) =>
        allowed
            .Select(id => _budgets.TryGetValue(id, out var b) ? b.RefusedUntil > now ? b.RefusedUntil : b.ResetAt : null)
            .Where(t => t is not null)
            .Min();

    private async Task<IReadOnlyList<long>> InstallationsAsync(CancellationToken ct)
    {
        if (_installations is { } cached && _clock.GetUtcNow() - cached.LoadedAt < InstallationListLifetime)
            return cached.Ids;

        var ids = (await app.ListInstallationsAsync(ct)).Select(i => i.Id).ToList();
        _installations = (ids, _clock.GetUtcNow());
        return ids;
    }

    /// <summary>Forgets the installation list, so one added or removed on GitHub is seen at once.</summary>
    public void ForgetInstallations() => _installations = null;
}

/// <summary>What a GitHub connector reads as: the shared pool, and which installations this source may use.</summary>
public sealed class GitHubAuth(GitHubCredentialPool pool, GitHubAccess access)
{
    public GitHubAccess Access => access;

    public Task<GitHubLease> AcquireAsync(IReadOnlySet<long> alreadyTried, CancellationToken ct) =>
        pool.AcquireAsync(access, alreadyTried, ct);

    public void Observe(GitHubLease lease, HttpResponseMessage response) => pool.Observe(lease.InstallationId, response);

    public void Refused(GitHubLease lease) => pool.Refused(lease.InstallationId);
}
