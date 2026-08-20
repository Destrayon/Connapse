using Connapse.Core.Utilities;

namespace Connapse.Core;

/// <summary>
/// Bounds what external data a source may be pointed at, independent of who points it.
/// <para>
/// Deliberately configuration-only — appsettings, environment, or the deployment's own
/// config file — and never writable through the API or the settings table. The authority a
/// filesystem root confers is the same class of thing as a cloud credential, and this project
/// already refuses to accept those over an API. Elasticsearch takes the same line with
/// <c>path.repo</c>, which is a static node setting requiring a restart to change.
/// </para>
/// </summary>
public record SourceSecuritySettings
{
    public const string SectionName = "Sources:Security";

    /// <summary>
    /// Directories a filesystem connection's <c>allowedRoot</c> may name. A root must equal
    /// one of these or sit beneath it.
    /// <para>
    /// Empty means unrestricted, which is the pre-existing behaviour and is retained for one
    /// release so upgrades do not break every filesystem source that #350 backfilled. The
    /// unrestricted case logs a warning naming each root in use, so an operator can see what
    /// to configure before it becomes deny-by-default.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowedFilesystemRoots { get; set; } = [];

    /// <summary>
    /// Decides whether a filesystem root is permitted, resolving links on both sides so a
    /// symlinked root cannot masquerade as an allowed one.
    /// </summary>
    public FilesystemRootDecision EvaluateRoot(string allowedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);

        if (AllowedFilesystemRoots.Count == 0)
            return FilesystemRootDecision.UnrestrictedByConfiguration;

        foreach (string permitted in AllowedFilesystemRoots)
        {
            if (string.IsNullOrWhiteSpace(permitted)) continue;

            // ResolveWithin, not a string compare: a root of "/data/link-to-etc" would
            // otherwise pass by looking like it sits under an allowed "/data".
            if (PathConfinement.ResolveWithin(permitted, allowedRoot) is not null)
                return FilesystemRootDecision.Allowed;
        }

        return FilesystemRootDecision.Denied;
    }
}

public enum FilesystemRootDecision
{
    /// <summary>The root sits inside a configured entry.</summary>
    Allowed,

    /// <summary>No allowlist is configured. Permitted, but the caller should warn.</summary>
    UnrestrictedByConfiguration,

    /// <summary>An allowlist is configured and this root is not covered by it.</summary>
    Denied
}
