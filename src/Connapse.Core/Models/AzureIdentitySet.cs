namespace Connapse.Core;

/// <summary>What the directory said about a searcher when their identity set was resolved.</summary>
public enum AzureIdentityOutcome
{
    /// <summary>A confident answer: the account is enabled and its group set is known.</summary>
    Resolved,

    /// <summary>The directory says the account is gone (404) or disabled — deny, and cacheable.</summary>
    Deprovisioned,

    /// <summary>The directory could not be asked (error/timeout/partial). Deny, and never cached.</summary>
    Failed,
}

/// <summary>
/// A searcher's Entra identity set: the object id plus the transitive security-group object ids
/// permissions may be held against. Fails closed — unless <see cref="Enabled"/> is true,
/// <see cref="PrincipalOids"/> is empty and nothing may be authorized from it.
/// </summary>
public record AzureIdentitySet(bool Enabled, IReadOnlyList<string> PrincipalOids, AzureIdentityOutcome Outcome)
{
    /// <summary>P = {oid} ∪ {group oids}, from a confident directory answer.</summary>
    public static AzureIdentitySet Resolved(IReadOnlyList<string> principalOids) =>
        new(true, principalOids, AzureIdentityOutcome.Resolved);

    /// <summary>The account is gone or disabled. Deny; cacheable.</summary>
    public static AzureIdentitySet Deprovisioned() => new(false, [], AzureIdentityOutcome.Deprovisioned);

    /// <summary>The directory could not be asked. Deny; never cache.</summary>
    public static AzureIdentitySet Failed() => new(false, [], AzureIdentityOutcome.Failed);
}
