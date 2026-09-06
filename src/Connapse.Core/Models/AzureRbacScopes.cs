namespace Connapse.Core;

/// <summary>Whether the RBAC scope set was resolved confidently or must fail closed.</summary>
public enum RbacOutcome { Resolved, Failed }

/// <summary>An <c>azblob://</c> prefix the searcher may read via an RBAC grant. A whole-account
/// grant is <c>azblob://{account}/</c>; a grant broader than an account (RG/subscription/management
/// group) is <c>azblob://</c> (matches every account).</summary>
public record AzureScope(string Prefix);

/// <summary>An RBAC grant gated on a blob index-tag condition that cannot reduce to a prefix. The
/// <see cref="Scope"/> is the broad candidate prefix; the tag predicate is verified live per hit
/// (Phase 4e). Never excluded here. <see cref="KeyCaseSensitive"/> reflects the condition's
/// <c>&lt;$key_case_sensitive$&gt;</c> marker; <see cref="ValueCaseSensitive"/> is true for
/// <c>StringEquals</c> and false for <c>StringEqualsIgnoreCase</c> — Phase 4e compares tag values
/// accordingly.</summary>
public record AzureTagCondition(
    string Scope, string TagKey, string TagValue, bool KeyCaseSensitive, bool ValueCaseSensitive = false);

/// <summary>The searcher's effective RBAC-readable scope set. Fails closed: unless
/// <see cref="Outcome"/> is <see cref="RbacOutcome.Resolved"/>, both lists are empty.</summary>
public record AzureRbacScopes(
    IReadOnlyList<AzureScope> ReadablePrefixes,
    IReadOnlyList<AzureTagCondition> TagConditioned,
    RbacOutcome Outcome)
{
    public static AzureRbacScopes Resolved(
        IReadOnlyList<AzureScope> readablePrefixes, IReadOnlyList<AzureTagCondition> tagConditioned) =>
        new(readablePrefixes, tagConditioned, RbacOutcome.Resolved);

    public static AzureRbacScopes Failed() => new([], [], RbacOutcome.Failed);
}
