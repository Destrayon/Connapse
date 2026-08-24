using System.Text.Json.Nodes;
using Connapse.Core;

namespace Connapse.Web.Components.Settings;

/// <summary>
/// The editable shape of a new source, flattened out of the provider-specific scope JSON it is
/// stored as.
/// <para>
/// A plain record rather than logic inside the Razor component, for the same reason as
/// <see cref="ConnectionForm"/>: this is where a mistyped key silently becomes a source that
/// points at nothing, or at the wrong bucket, and the component itself has no test harness in
/// this repository. The keys written here must match the ones
/// <c>ConnectorFactory.Create(Source, Connection)</c> reads, so they are asserted in tests
/// rather than trusted.
/// </para>
/// </summary>
public sealed record SourceForm
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid ConnectionId { get; set; }

    /// <summary>S3 bucket, or Azure blob container. Which one is decided by the connection.</summary>
    public string? Container { get; set; }

    /// <summary>Cloud only. Narrows the source to a subtree of the bucket or container.</summary>
    public string? Prefix { get; set; }

    /// <summary>Filesystem only. Resolved beneath the connection's allowed root.</summary>
    public string? SubPath { get; set; }

    /// <summary>Newline-separated in the UI; an array in the stored JSON.</summary>
    public string? IncludePatterns { get; set; }

    /// <summary>Newline-separated in the UI; an array in the stored JSON.</summary>
    public string? ExcludePatterns { get; set; }

    /// <summary>Null means "use the configured default" rather than "never sync".</summary>
    public int? SyncIntervalSeconds { get; set; }

    /// <summary>
    /// Builds the scope JSON for a connection's provider. Only the keys that provider reads are
    /// emitted — writing a <c>bucketName</c> into a filesystem scope would be silently ignored
    /// at sync time, which is worse than being rejected.
    /// </summary>
    public string ToScopeJson(ConnectionProvider provider)
    {
        var node = new JsonObject();

        switch (provider)
        {
            case ConnectionProvider.S3:
                node["bucketName"] = Container?.Trim() ?? "";
                if (!Blank(Prefix)) node["prefix"] = Prefix!.Trim();
                break;

            case ConnectionProvider.AzureBlob:
                node["containerName"] = Container?.Trim() ?? "";
                if (!Blank(Prefix)) node["prefix"] = Prefix!.Trim();
                break;

            // One case for both. The SFTP scope was deliberately given the same shape as the
            // filesystem one so that this form, the preflight and the scope summary each gained
            // a provider here rather than a parallel implementation to keep in step.
            case ConnectionProvider.Filesystem:
            case ConnectionProvider.Sftp:
                // Always present, and empty means the root itself. ConnectorFactory treats a
                // blank subPath as "the allowed root", so this is a meaningful value rather
                // than a missing one.
                node["subPath"] = SubPath?.Trim() ?? "";
                AddPatterns(node, "includePatterns", IncludePatterns);
                AddPatterns(node, "excludePatterns", ExcludePatterns);
                break;

            default:
                throw new NotSupportedException($"Unknown connection provider: {provider}");
        }

        return node.ToJsonString();
    }

    /// <summary>
    /// Field-level validation. Returns the first problem, or null when the form is well formed.
    /// <para>
    /// Deliberately does not check the connection's allowlist — that is
    /// <see cref="Connapse.Web.Services.SourceScopePreflight"/>, which needs deployment
    /// configuration this record does not have.
    /// </para>
    /// </summary>
    public string? Validate(ConnectionProvider provider)
    {
        if (Blank(Name))
            return "A source name is required.";

        if (ConnectionId == Guid.Empty)
            return "Choose a connection for this source.";

        if (provider is ConnectionProvider.S3 && Blank(Container))
            return "A bucket name is required.";

        if (provider is ConnectionProvider.AzureBlob && Blank(Container))
            return "A blob container name is required.";

        if (SyncIntervalSeconds is { } interval && interval < 60)
            return "Sync interval must be at least 60 seconds.";

        return null;
    }

    /// <summary>
    /// Splits a newline- or comma-separated textarea into entries, dropping blanks. Shared shape
    /// with the connection form's allowed-locations field so the two behave the same way.
    /// </summary>
    internal static List<string> ParsePatterns(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .ToList();

    private static void AddPatterns(JsonObject node, string name, string? raw)
    {
        var values = ParsePatterns(raw);
        if (values.Count == 0) return;

        var array = new JsonArray();
        foreach (string value in values) array.Add(value);
        node[name] = array;
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
