using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope.RolesAnywhere;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// The credential every AWS client in Connapse uses: the identity an administrator gave it, or
/// whatever its environment provides.
/// </summary>
/// <remarks>
/// A <see cref="RefreshingAWSCredentials"/> rather than a resolved <c>AWSCredentials</c> handed
/// round, because the two AWS best practices for this pull against each other here.
/// <para>
/// The SDK's guidance is to reuse clients: creating one per call re-fetches credentials, rebuilds
/// the HTTP connection pool each time, and can hit rate limits. But a long-lived client caches
/// whatever credential it was built with — and Connapse's credential changes while it runs, because
/// an administrator can set or rotate it in the UI. A cached client would go on using a key that had
/// been replaced, which quietly defeats the point of offering rotation at all.
/// </para>
/// <para>
/// Refreshing resolves both: the client can live as long as it likes, because the credential object
/// re-reads itself. A rotation takes effect within <see cref="RefreshWindow"/> with no restart.
/// </para>
/// <para>
/// It is also the one place the fallback order is decided, so discovery, the connector and the
/// connection tester cannot drift apart on what identity they use — which they had already begun to
/// do.
/// </para>
/// </remarks>
public class ConnapseAwsCredentials(
    IServiceScopeFactory scopeFactory,
    ILogger<ConnapseAwsCredentials> logger) : RefreshingAWSCredentials
{
    /// <summary>Key the AWS credential is stored under.</summary>
    public const string ProviderKey = "aws";

    /// <summary>Name of the named HttpClient used for Roles Anywhere CreateSession calls.</summary>
    public const string RolesAnywhereHttpClientName = "RolesAnywhere";

    /// <summary>
    /// How long a resolved credential is held before the store is consulted again.
    /// </summary>
    /// <remarks>
    /// Short, because its job is to bound how long a rotation takes to take effect rather than to
    /// save work. The read is a single indexed row and a decrypt; five minutes of staleness is the
    /// cost of not doing that per request.
    /// </remarks>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The refresh time for a resolved credential: now + <see cref="RefreshWindow"/>, but never later than
    /// the credential's own expiry (Roles Anywhere sessions expire; a static key passes null).
    /// </summary>
    public static DateTime ClampRefreshExpiry(DateTime nowUtc, TimeSpan window, DateTime? sessionExpirationUtc)
    {
        DateTime expiry = nowUtc.Add(window);
        if (sessionExpirationUtc is DateTime exp && exp < expiry)
            expiry = exp;
        return expiry;
    }

    protected override CredentialsRefreshState GenerateNewCredentials()
    {
        // The SDK's refresh hook is synchronous, and there is no async form to override.
        ResolvedCredentials? resolved = ResolveStored();

        if (resolved is not null)
        {
            DateTime expiry = ClampRefreshExpiry(DateTime.UtcNow, RefreshWindow, resolved.Expiration);
            return new CredentialsRefreshState(resolved.Credentials, expiry);
        }

        // Nothing configured: whatever the environment provides — an instance role in production,
        // a mounted profile in development. Refreshed on the same window so that configuring a
        // credential later takes effect without a restart either.
        var ambient = Amazon.Runtime.Credentials.DefaultAWSCredentialsIdentityResolver
            .GetCredentials(new Amazon.S3.AmazonS3Config());

        return new CredentialsRefreshState(
            ambient.GetCredentials(), DateTime.UtcNow.Add(RefreshWindow));
    }

    /// <summary>
    /// The stored credential, or null to fall back.
    /// </summary>
    /// <remarks>
    /// A configured identity outranks the ambient chain deliberately. The chain may resolve an
    /// administrator's personal profile from a mounted home directory, and preferring that would
    /// make Connapse's reach depend on whose machine it happens to run beside.
    /// </remarks>
    /// <summary>
    /// Reads the stored credential, in its own scope.
    /// </summary>
    /// <remarks>
    /// A scope per refresh rather than holding the store: this class is a singleton, because
    /// <c>ConnectorFactory</c> is one and consumes it, while the store reaches the database through
    /// a <c>DbContextFactory</c> that this application registers as scoped.
    /// </remarks>
    private async Task<ResolvedCredentials?> ReadStoredAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

        // A configured role outranks a static key, which outranks the ambient chain. A single read
        // of the material doubles as the presence gate: two separate calls (a presence check, then
        // the material) left a window where a mode switch between them could resolve a stale answer.
        try
        {
            RolesAnywhereCredentialMaterial? material =
                await credentialStore.GetRolesAnywhereMaterialAsync(ProviderKey);
            if (material is not null)
            {
                // Once a Roles Anywhere config exists, this identity is what an administrator asked
                // for. Any failure producing it — missing key, unreadable cert/key, a failed
                // CreateSession — must fail closed rather than let the caller silently fall through
                // to the ambient chain below, so the whole production is wrapped and rethrown as one
                // exception type.
                if (string.IsNullOrEmpty(material.PrivateKeyPem))
                {
                    throw new InvalidOperationException(
                        "Roles Anywhere is configured but its private key is missing.");
                }

                var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                using X509Certificate2 certificate = X509Certificate2.CreateFromPem(
                    material.Config.CertificatePem, material.PrivateKeyPem);
                var client = new RolesAnywhereClient(httpClientFactory.CreateClient(RolesAnywhereHttpClientName));
                var parameters = new RolesAnywhereParameters(
                    material.Config.TrustAnchorArn, material.Config.ProfileArn,
                    material.Config.RoleArn, material.Config.Region);

                RolesAnywhereSession session = await client.CreateSessionAsync(
                    certificate, parameters, DateTimeOffset.UtcNow);

                return new ResolvedCredentials(session.Credentials, session.Expiration.UtcDateTime);
            }
        }
        catch (Exception ex)
        {
            throw new RolesAnywhereCredentialException(ProviderKey, ex);
        }

        var info = await credentialStore.GetAsync(ProviderKey);
        if (info is null) return null;

        string? secret = await credentialStore.GetSecretAsync(ProviderKey);
        if (string.IsNullOrEmpty(secret)) return null;

        return new ResolvedCredentials(new ImmutableCredentials(info.PublicId, secret, null), null);
    }

    private ResolvedCredentials? ResolveStored()
    {
        try
        {
            // Task.Run, and it is load-bearing rather than cargo cult.
            //
            // GenerateNewCredentials is synchronous — the SDK offers no async hook to override —
            // so reading the database here has to block. Blocking directly deadlocked: Blazor
            // Server runs component code on a renderer synchronization context, and waiting on a
            // task whose continuation needs that same context waits forever. The page sat on
            // "Checking providers…" and never finished, while SourceSyncService called the same
            // code happily, because a background service has no synchronization context at all.
            //
            // Task.Run moves the work to the thread pool, where there is no context to deadlock
            // against. It costs a thread for the length of one query, once every RefreshWindow.
            return Task.Run(ReadStoredAsync).GetAwaiter().GetResult();
        }
        catch (AggregateException ex) when (ex.InnerException is ProviderCredentialUnavailableException inner)
        {
            // Task.Run surfaces the original through GetAwaiter().GetResult(), but a faulted task
            // observed any other way wraps it — caught both ways so the distinction survives.
            logger.LogError(inner, "The stored AWS credential could not be decrypted");
            throw inner;
        }
        catch (AggregateException ex) when (ex.InnerException is RolesAnywhereCredentialException raInner)
        {
            // Same wrapping hazard as above, mirrored for the Roles Anywhere fail-closed exception:
            // caught both ways so a wrapped throw can never slip past this into the generic
            // catch (Exception) below and get treated as "nothing configured" (ambient).
            logger.LogError(raInner, "The stored Roles Anywhere credential could not be used");
            throw raInner;
        }
        catch (ProviderCredentialUnavailableException ex)
        {
            // Stored and unreadable — the key ring that encrypted it is gone. Falling back to the
            // environment would silently run as a different identity than the one configured, so
            // this stays a hard failure rather than becoming a quiet substitution.
            logger.LogError(ex, "The stored AWS credential could not be decrypted");
            throw;
        }
        catch (RolesAnywhereCredentialException ex)
        {
            // A Roles Anywhere config exists but could not be turned into a credential (missing key,
            // bad cert/key, or CreateSession failed/timed out). Falling back to the ambient chain
            // would silently run as a different identity than the one configured, so this stays a
            // hard failure exactly like the decrypt failure above.
            logger.LogError(ex, "The stored Roles Anywhere credential could not be used");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the stored AWS credential; using the environment");
            return null;
        }
    }

    /// <summary>A resolved credential and, for Roles Anywhere, when it expires (null for a static key).</summary>
    private sealed record ResolvedCredentials(ImmutableCredentials Credentials, DateTime? Expiration);
}
