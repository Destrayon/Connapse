namespace Connapse.Core;

/// <summary>
/// Deployment settings for Atlassian sources. Configuration-only, like
/// <see cref="GitHubSourceSettings"/>: it names a directory on the host.
/// </summary>
public record AtlassianSourceSettings
{
    public const string SectionName = "Sources:Atlassian";

    /// <summary>
    /// Directory holding one page-state store per Confluence source, beside the GitHub mirrors on
    /// the <c>appdata</c> volume. A cache of the last listing: the next sync rebuilds it.
    /// </summary>
    public string StateDirectory { get; set; } = Path.Combine("appdata", "atlassian-state");
}
