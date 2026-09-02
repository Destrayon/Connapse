namespace Connapse.Web.Services;

/// <summary>Which credential the AWS Access card is describing.</summary>
public enum AwsAccessMode
{
    /// <summary>Requirements have not loaded yet — show nothing actionable, to avoid a flash of setup.</summary>
    Loading,

    /// <summary>A Roles Anywhere credential is stored. The one credential Connapse owns and can reset.</summary>
    RolesAnywhere,

    /// <summary>No credential is stored. Offer setup — a probe may still resolve from the environment.</summary>
    NotStored
}

/// <summary>Decides how the AWS Access card presents itself, from only what can be known for certain.</summary>
public static class AwsAccessCard
{
    /// <summary>
    /// Resolves the card's mode without guessing where credentials come from.
    /// </summary>
    /// <remarks>
    /// A stored Roles Anywhere row is the one credential Connapse owns, so it is authoritative and
    /// the only thing there is to reset. Everything else is "not stored": a probe that resolves proves
    /// <i>some</i> credential exists in the environment, but not <i>what kind</i> — an instance or task
    /// role, environment access keys, a shared-credentials file, or web identity all resolve
    /// identically through the default chain. The card therefore must never claim it is an instance
    /// role, and must never hide the Roles Anywhere setup path behind that guess — an off-AWS
    /// installation running on bootstrap access keys would otherwise be steered away from setting up
    /// the very identity this feature exists to give it.
    /// </remarks>
    public static AwsAccessMode ResolveMode(bool requirementsLoaded, bool hasStoredRolesAnywhere) =>
        !requirementsLoaded ? AwsAccessMode.Loading
        : hasStoredRolesAnywhere ? AwsAccessMode.RolesAnywhere
        : AwsAccessMode.NotStored;

    /// <summary>
    /// Recomputes one manual-entry field when the stored credential is (re)loaded.
    /// </summary>
    /// <remarks>
    /// A field the operator has not touched still equals what was last loaded into it, so it takes the
    /// freshly loaded value — picking up a change made elsewhere since the form opened. A field they
    /// edited differs from the last-loaded value and is preserved, so a re-check never discards work in
    /// progress. On the first load both the current value and the last-loaded value are null, so every
    /// field fills.
    /// </remarks>
    public static string? RefreshManualField(string? current, string? lastLoaded, string? loaded) =>
        current == lastLoaded ? loaded : current;
}
