using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Services;

/// <summary>
/// Remembers assertion ids in this process for as long as they could still be replayed.
/// </summary>
/// <remarks>
/// Entries expire when the assertion does, so the store stays the size of recent sign-ins rather
/// than growing with every sign-in ever. An assertion is only worth replaying inside its own
/// validity window; past that the validator rejects it on age regardless of what is remembered
/// here.
/// <para>
/// Single-process, which matches how Connapse is deployed. Several instances behind a load balancer
/// would each keep their own set, and an assertion accepted by one could be posted to another
/// within its window — so a deployment that scales out needs a shared store here, not a bigger
/// cache.
/// </para>
/// </remarks>
public sealed class MemorySamlReplayGuard(IMemoryCache cache) : ISamlReplayGuard
{
    private const string KeyPrefix = "saml-assertion:";

    /// <inheritdoc />
    public bool TryRegister(string assertionId, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assertionId);

        string key = KeyPrefix + assertionId;
        if (cache.TryGetValue(key, out _))
            return false;

        // A floor under the retention window. An assertion whose stated expiry has already passed
        // would otherwise be cached for no time at all and could be posted repeatedly in the
        // moments before the age check starts refusing it.
        DateTimeOffset until = expiresAt > DateTimeOffset.UtcNow
            ? expiresAt
            : DateTimeOffset.UtcNow.AddMinutes(5);

        cache.Set(key, true, until);
        return true;
    }
}
