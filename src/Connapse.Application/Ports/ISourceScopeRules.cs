using Connapse.Core;

namespace Connapse.Application.Ports;

/// <summary>Why a provider refused a scope: it is wrong, it is already a source, or the provider couldn't be asked.</summary>
public enum ScopeRefusal
{
    Invalid,
    Duplicate,
    Unavailable,
}

/// <summary>
/// A provider's answer about a scope: the scope to store (and, for a create, the name to use when
/// none was given and a warning to pass on), or what is wrong and with which field.
/// </summary>
public sealed record ScopeCheck(string? ScopeJson, string? Error = null, string? Field = null)
{
    public bool Ok => Error is null;

    public ScopeRefusal Refusal { get; init; }

    public string? DefaultName { get; init; }

    public string? Warning { get; init; }

    public static ScopeCheck Refuse(string error, string? field = null, ScopeRefusal refusal = ScopeRefusal.Invalid) =>
        new(null, error, field) { Refusal = refusal };
}

/// <summary>
/// The per-provider rules for a source's scope: what may change in place, and how a GitHub,
/// Atlassian, custom or storage scope is checked and rewritten. They need provider lookups and
/// form models that live in Web, which answers this for the operations.
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

/// <summary>
/// How a provider checks a new source's scope, asking it where the scope names something it owns
/// (a GitHub repository, an Atlassian space or project, a custom server's scope) and checking a
/// storage scope against the connection's allowed locations. Web answers it for the create operation.
/// </summary>
public interface ISourceCreateRules
{
    /// <summary>Whether a new source on <paramref name="connection"/> may read <paramref name="scopeJson"/>, and the scope to store.</summary>
    Task<ScopeCheck> CheckCreateAsync(Connection connection, string? scopeJson, CancellationToken ct = default);
}
