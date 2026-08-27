using Connapse.Core;

namespace Connapse.Search;

/// <summary>
/// The resolver a deployment gets when it has configured no per-user permissions.
/// </summary>
/// <remarks>
/// Every search reaches every document, which is what Connapse has always done. Registered as the
/// default so that adding the enforcement path changes no behaviour: a deployment opts in to
/// filtering by registering a resolver that resolves something.
/// <para>
/// Denying by default here would be safer in the abstract and wrong in practice — it would leave
/// every existing installation unable to search anything the moment it upgraded, for a feature
/// nobody had switched on.
/// </para>
/// <para>
/// This is deliberately not a place to put logic. A resolver that sometimes restricts belongs in
/// its own implementation, so that reading this one tells you the whole truth about what a
/// default deployment enforces: nothing.
/// </para>
/// </remarks>
public sealed class UnrestrictedScopeResolver : ISearchScopeResolver
{
    public Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default) =>
        Task.FromResult(SearchScopes.Unrestricted);
}
