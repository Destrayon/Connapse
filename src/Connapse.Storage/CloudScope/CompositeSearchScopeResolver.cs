using Connapse.Core;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Unions the AWS and Azure scope resolvers into one, per cloud and per URI scheme. Each cloud
/// governs its own scheme (<c>s3://</c>, <c>azblob://</c>); non-cloud documents (resource_uri NULL)
/// are always admitted by the store's existing fallback. A cloud that is not enforcing contributes
/// its scheme wildcard (its docs visible); a cloud that denies or fails contributes nothing (its
/// docs hidden), and one cloud's failure never denies the other's or non-cloud docs. Only when both
/// clouds are unrestricted is the result globally unrestricted.
/// </summary>
public sealed class CompositeSearchScopeResolver(
    AwsSearchScopeResolver aws,
    AzureSearchScopeResolver azure) : ISearchScopeResolver
{
    private static readonly Combiner Combine = new();

    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        SearchScopes awsScopes = await SafeResolveAsync(aws, userId, ct);
        SearchScopes azureScopes = await SafeResolveAsync(azure, userId, ct);
        return Combine.Combine(awsScopes, azureScopes);
    }

    // A throw from one cloud fails only that cloud closed; cancellation still propagates.
    private static async Task<SearchScopes> SafeResolveAsync(ISearchScopeResolver inner, Guid? userId, CancellationToken ct)
    {
        try
        {
            return await inner.ResolveAsync(userId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return SearchScopes.Failed;
        }
    }

    /// <summary>The pure combine rule, separated so it can be unit-tested without resolvers.</summary>
    public sealed class Combiner
    {
        private const string S3Scheme = "s3://";
        private const string AzureScheme = "azblob://";

        public SearchScopes Combine(SearchScopes awsScopes, SearchScopes azureScopes)
        {
            if (awsScopes.IsUnrestricted && azureScopes.IsUnrestricted)
                return SearchScopes.Unrestricted;

            var matches = new List<GrantMatch>();
            matches.AddRange(ContributionFor(awsScopes, S3Scheme));
            matches.AddRange(ContributionFor(azureScopes, AzureScheme));
            return SearchScopes.Of(matches);
        }

        // Unrestricted → the scheme wildcard (all of that cloud's docs). Granted → its own matches.
        // Any denial/failure/no-principal → nothing (that scheme's docs are hidden — fail closed).
        private static IReadOnlyList<GrantMatch> ContributionFor(SearchScopes scopes, string schemeWildcard)
        {
            if (scopes.IsUnrestricted)
                return [new GrantMatch(schemeWildcard, IsExact: false)];
            if (scopes.Outcome is ScopeOutcome.Granted)
                return scopes.Matches;
            return [];
        }
    }
}
