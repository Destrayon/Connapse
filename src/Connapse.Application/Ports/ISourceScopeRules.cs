using Connapse.Core;

namespace Connapse.Application.Ports;

/// <summary>A provider's answer about a scope: the scope to store, or what is wrong and with which field.</summary>
public sealed record ScopeCheck(string? ScopeJson, string? Error = null, string? Field = null)
{
    public bool Ok => Error is null;

    public static ScopeCheck Refuse(string error, string? field = null) => new(null, error, field);
}

/// <summary>
/// The per-provider rules for a source's scope: what may change in place, and how a GitHub,
/// Atlassian or custom scope is checked and rewritten. They need provider lookups and form models
/// that live in Web, which answers this for the operations.
/// </summary>
public interface ISourceScopeRules
{
    /// <summary>
    /// Whether <paramref name="current"/> may take <paramref name="requestedScopeJson"/>, and the scope
    /// to store if so: a GitHub source's identity and a custom source's server are fixed, and an
    /// Atlassian source keeps its space or project.
    /// </summary>
    Task<ScopeCheck> CheckUpdateAsync(Source current, string requestedScopeJson, CancellationToken ct = default);
}
