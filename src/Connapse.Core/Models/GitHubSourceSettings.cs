namespace Connapse.Core;

/// <summary>
/// Deployment settings for GitHub sources.
/// <para>
/// Configuration-only, like <see cref="SourceSecuritySettings"/>: where the process keeps its
/// git mirrors is a property of the host, not something an API caller should be able to point
/// somewhere else.
/// </para>
/// </summary>
public record GitHubSourceSettings
{
    public const string SectionName = "Sources:GitHub";

    /// <summary>
    /// Directory holding one bare git mirror per docs source. Relative paths resolve against
    /// the working directory, so the default lands on the <c>appdata</c> volume in the
    /// container, next to the Data Protection key ring.
    /// <para>
    /// A mirror is a cache, not state: losing it costs one full re-fetch, and the sync engine
    /// is told to resync because the stored commit can no longer be diffed against.
    /// </para>
    /// </summary>
    public string MirrorDirectory { get; set; } = Path.Combine("appdata", "github-mirrors");
}
