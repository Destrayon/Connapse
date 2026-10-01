using Connapse.Core;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Unions any number of scope resolvers into one, per URI scheme. Each resolver governs its own
/// scheme (<c>s3://</c>, <c>azblob://</c>, <c>github://</c>, <c>atlassian://</c>); non-provider
/// documents (resource_uri NULL) are always admitted by the store's existing fallback. A cloud
/// (<see cref="ScopeKind.Cloud"/>) that is not enforcing contributes its scheme wildcard (its docs
/// visible); one that denies or fails contributes nothing (its docs hidden), and one resolver's
/// failure never denies another's or non-provider docs. Only when every resolver is a cloud and every
/// cloud is unrestricted is the result globally unrestricted.
/// <para>
/// GitHub and Atlassian (<see cref="ScopeKind.GrantOnly"/>) are the exception to that last rule:
/// they only ever contribute grants, never "unrestricted", because their documents must be filtered
/// whether or not any cloud is. So when one is part of the composite the result is always a set of
/// matches — the clouds' wildcards when they do not filter, plus what this user was granted.
/// </para>
/// </summary>
// The inner resolvers are typed as ISearchScopeResolver (not the concrete AWS/Azure types) purely
// so this can be unit-tested with throwing fakes; DI wires the concrete resolvers in via an
// explicit factory, which also avoids a self-referential ISearchScopeResolver resolution.
public sealed class CompositeSearchScopeResolver(IReadOnlyList<SchemeResolver> resolvers) : ISearchScopeResolver
{
    private static readonly Combiner Rule = new();

    /// <summary>The original shape: two clouds and an optional GitHub. Maps onto the list form.</summary>
    public CompositeSearchScopeResolver(
        ISearchScopeResolver aws,
        ISearchScopeResolver azure,
        ISearchScopeResolver? github = null)
        : this(LegacyList(aws, azure, github))
    {
    }

    private static List<SchemeResolver> LegacyList(ISearchScopeResolver aws, ISearchScopeResolver azure, ISearchScopeResolver? github)
    {
        var list = new List<SchemeResolver>
        {
            new(aws, Combiner.S3Scheme, ScopeKind.Cloud),
            new(azure, Combiner.AzureScheme, ScopeKind.Cloud),
        };
        if (github is not null)
            list.Add(new SchemeResolver(github, GitHubSearchScopeResolver.Scheme, ScopeKind.GrantOnly));
        return list;
    }

    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        var resolved = new List<(SchemeResolver Entry, SearchScopes Scopes)>(resolvers.Count);
        foreach (SchemeResolver entry in resolvers)
            resolved.Add((entry, await SafeResolveAsync(entry.Resolver, userId, ct)));

        return Rule.Combine(resolved);
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
        internal const string S3Scheme = "s3://";
        internal const string AzureScheme = "azblob://";

        public SearchScopes Combine(SearchScopes awsScopes, SearchScopes azureScopes) =>
            Combine(
            [
                (new SchemeResolver(NoResolver.Instance, S3Scheme, ScopeKind.Cloud), awsScopes),
                (new SchemeResolver(NoResolver.Instance, AzureScheme, ScopeKind.Cloud), azureScopes),
            ]);

        /// <summary>
        /// With GitHub: never globally unrestricted. Each cloud contributes as above; GitHub adds
        /// only the private repositories it granted — an "unrestricted" GitHub answer is not a
        /// permit and adds nothing, so its documents fail closed whatever the clouds say.
        /// </summary>
        public SearchScopes Combine(SearchScopes awsScopes, SearchScopes azureScopes, SearchScopes githubScopes) =>
            Combine(
            [
                (new SchemeResolver(NoResolver.Instance, S3Scheme, ScopeKind.Cloud), awsScopes),
                (new SchemeResolver(NoResolver.Instance, AzureScheme, ScopeKind.Cloud), azureScopes),
                (new SchemeResolver(NoResolver.Instance, GitHubSearchScopeResolver.Scheme, ScopeKind.GrantOnly), githubScopes),
            ]);

        /// <summary>
        /// Any number of schemes. A <see cref="ScopeKind.Cloud"/> entry contributes its wildcard when
        /// unrestricted and its own matches when granted. A <see cref="ScopeKind.GrantOnly"/> entry
        /// contributes only granted matches under its own scheme; with one present the result is never
        /// globally unrestricted, so the documents it governs fail closed whatever the clouds say.
        /// </summary>
        public SearchScopes Combine(IReadOnlyList<(SchemeResolver Entry, SearchScopes Scopes)> resolved)
        {
            bool anyGrantOnly = resolved.Any(r => r.Entry.Kind is ScopeKind.GrantOnly);
            if (!anyGrantOnly && resolved.All(r => r.Scopes.IsUnrestricted))
                return SearchScopes.Unrestricted;

            var matches = new List<GrantMatch>();
            bool personal = false;
            foreach ((SchemeResolver entry, SearchScopes scopes) in resolved)
            {
                if (entry.Kind is ScopeKind.Cloud)
                {
                    matches.AddRange(ContributionFor(scopes, entry.Scheme));
                    personal |= scopes.Outcome is ScopeOutcome.Granted;
                }
                else
                {
                    List<GrantMatch> granted = scopes.Outcome is ScopeOutcome.Granted
                        ? scopes.Matches.Where(m => m.Value.StartsWith(entry.Scheme, StringComparison.Ordinal)).ToList()
                        : [];
                    matches.AddRange(granted);
                    personal |= granted.Count > 0;
                }
            }

            // Only the clouds' "not filtering" wildcards, no one's own grant: the deployment's answer,
            // which a caller who is not a person gets as it got "unrestricted" before GitHub joined.
            // With no grant-only entry there was no such answer to stand in for, so it stays Of.
            return personal || !anyGrantOnly ? SearchScopes.Of(matches) : SearchScopes.DeploymentWide(matches);
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

        // The pure rule never resolves anything; the wrappers above need an entry to name a scheme.
        private sealed class NoResolver : ISearchScopeResolver
        {
            public static readonly NoResolver Instance = new();

            public Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default) =>
                Task.FromResult(SearchScopes.Failed);
        }
    }
}

/// <summary>How a scheme's resolver takes part in the combine rule.</summary>
public enum ScopeKind
{
    /// <summary>Unrestricted means its scheme wildcard, granted means its matches, anything else nothing.</summary>
    Cloud,

    /// <summary>Only granted matches under its own scheme count; never "unrestricted".</summary>
    GrantOnly,
}

/// <summary>A resolver together with the URI scheme it governs and how it combines.</summary>
public sealed record SchemeResolver(ISearchScopeResolver Resolver, string Scheme, ScopeKind Kind);
