namespace Connapse.Core;

/// <summary>Deployment settings for custom sources. Configuration-only.</summary>
public record CustomSourceSettings
{
    public const string SectionName = "CustomSources";

    /// <summary>
    /// Hosts a custom source may reach over plain HTTP — a middle man on the same machine or Docker
    /// network, such as <c>host.docker.internal</c>. Every other middle man must use HTTPS.
    /// </summary>
    public List<string> AllowedHttpHosts { get; set; } = [];
}
