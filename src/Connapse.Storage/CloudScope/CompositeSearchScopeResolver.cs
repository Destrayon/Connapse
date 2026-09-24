using Connapse.Core;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Unions the AWS and Azure scope resolvers into one, per cloud and per URI scheme. Each cloud
/// governs its own scheme (<c>s3://</c>, <c>azblob://</c>); non-cloud documents (resource_uri NULL)
/// are always admitted by the store's existing fallback. A cloud that is not enforcing contributes
/// its scheme wildcard (its docs visible); a cloud that denies or fails contributes nothing (its
/// docs hidden), and one cloud's failure never denies the other's or non-cloud docs. Only when both
/// clouds are unrestricted is the result globally unrestricted.
/// <para>
/// GitHub is the exception to that last rule: it only ever contributes grants, never
/// "unrestricted", because private repository documents must be filtered whether or not any cloud
/// is. So when GitHub is part of the composite the result is always a set of matches — the clouds'
/// wildcards when they do not filter, plus the private repositories this user may read.
/// </para>
/// </summary>
// The inner resolvers are typed as ISearchScopeResolver (not the concrete AWS/Azure types) purely
// so this can be unit-tested with throwing fakes; DI wires the two concrete resolvers in via an
// explicit factory, which also avoids a self-referential ISearchScopeResolver resolution.
public sealed class CompositeSearchScopeResolver(
    ISearchScopeResolver aws,
    ISearchScopeResolver azure,
    ISearchScopeResolver? github = null) : ISearchScopeResolver
{
    private static readonly Combiner Rule = new();

    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        SearchScopes awsScopes = await SafeResolveAsync(aws, userId, ct);
        SearchScopes azureScopes = await SafeResolveAsync(azure, userId, ct);
        if (github is null)
            return Rule.Combine(awsScopes, azureScopes);

        SearchScopes githubScopes = await SafeResolveAsync(github, userId, ct);
        return Rule.Combine(awsScopes, azureScopes, githubScopes);
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

        /// <summary>
        /// With GitHub: never globally unrestricted. Each cloud contributes as above; GitHub adds
        /// only the private repositories it granted — an "unrestricted" GitHub answer is not a
        /// permit and adds nothing, so its documents fail closed whatever the clouds say.
        /// </summary>
        public SearchScopes Combine(SearchScopes awsScopes, SearchScopes azureScopes, SearchScopes githubScopes)
        {
            var matches = new List<GrantMatch>();
            matches.AddRange(ContributionFor(awsScopes, S3Scheme));
            matches.AddRange(ContributionFor(azureScopes, AzureScheme));
            var github = githubScopes.Outcome is ScopeOutcome.Granted
                ? githubScopes.Matches.Where(m => m.Value.StartsWith(GitHubSearchScopeResolver.Scheme, StringComparison.Ordinal)).ToList()
                : [];
            matches.AddRange(github);

            // Only the clouds' "not filtering" wildcards, no one's own grant: the deployment's answer,
            // which a caller who is not a person gets as it got "unrestricted" before GitHub joined.
            bool personal = awsScopes.Outcome is ScopeOutcome.Granted || azureScopes.Outcome is ScopeOutcome.Granted || github.Count > 0;
            return personal ? SearchScopes.Of(matches) : SearchScopes.DeploymentWide(matches);
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
