using Amazon.Runtime;
using Connapse.Core.Interfaces;
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

    /// <summary>
    /// How long a resolved credential is held before the store is consulted again.
    /// </summary>
    /// <remarks>
    /// Short, because its job is to bound how long a rotation takes to take effect rather than to
    /// save work. The read is a single indexed row and a decrypt; five minutes of staleness is the
    /// cost of not doing that per request.
    /// </remarks>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);

    protected override CredentialsRefreshState GenerateNewCredentials()
    {
        // The SDK's refresh hook is synchronous, and there is no async form to override.
        var stored = ResolveStored();

        if (stored is not null)
        {
            return new CredentialsRefreshState(stored, DateTime.UtcNow.Add(RefreshWindow));
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
    private ImmutableCredentials? ResolveStored()
    {
        try
        {
            // A scope per refresh, rather than holding the store.
            //
            // This is a singleton — ConnectorFactory is one and consumes it, so it cannot be
            // anything else — while the store reaches the database through a DbContextFactory that
            // this application registers as scoped. Taking it in the constructor made it captive,
            // which the container tolerates in Production and the integration tests refuse.
            //
            // A scope every RefreshWindow is not a cost worth avoiding.
            using var scope = scopeFactory.CreateScope();
            var credentialStore = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

            var info = credentialStore.GetAsync(ProviderKey).GetAwaiter().GetResult();
            if (info is null) return null;

            string? secret = credentialStore.GetSecretAsync(ProviderKey).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(secret)) return null;

            return new ImmutableCredentials(info.PublicId, secret, null);
        }
        catch (ProviderCredentialUnavailableException ex)
        {
            // Stored and unreadable — the key ring that encrypted it is gone. Falling back to the
            // environment would silently run as a different identity than the one configured, so
            // this stays a hard failure rather than becoming a quiet substitution.
            logger.LogError(ex, "The stored AWS credential could not be decrypted");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the stored AWS credential; using the environment");
            return null;
        }
    }
}
