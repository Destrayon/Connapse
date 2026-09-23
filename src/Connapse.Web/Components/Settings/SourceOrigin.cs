using Connapse.Core;

namespace Connapse.Web.Components.Settings;

/// <summary>A kind of source that needs no connection, because its content needs no credential.</summary>
/// <param name="Key">Stable identifier, used in the New source dialog's choice.</param>
/// <param name="Label">What the dialog offers.</param>
/// <param name="Provider">Stored on the source in place of a connection.</param>
public sealed record ConnectionlessSourceKind(string Key, string Label, ConnectionProvider Provider);

/// <summary>
/// Every kind of connection-less source the New source dialog offers. A new one — a web crawl, say
/// — is an entry here plus its fields in the dialog, not a new button or page.
/// </summary>
public static class ConnectionlessSourceKinds
{
    public static readonly ConnectionlessSourceKind GitHub =
        new("github", "Public GitHub repository", ConnectionProvider.GitHub);

    public static IReadOnlyList<ConnectionlessSourceKind> All { get; } = [GitHub];

    public static ConnectionlessSourceKind? Find(string? key) =>
        All.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.Ordinal));
}

/// <summary>
/// Where a new source's content comes from: one of the administrator's connections, or a kind that
/// needs none. The New source dialog's first choice, round-tripped through a single select value.
/// </summary>
public readonly record struct SourceOrigin
{
    private const string ConnectionPrefix = "connection:";
    private const string KindPrefix = "public:";

    private SourceOrigin(Guid? connectionId, ConnectionlessSourceKind? kind)
    {
        ConnectionId = connectionId;
        Kind = kind;
    }

    /// <summary>The chosen connection, or null when a connection-less kind was chosen.</summary>
    public Guid? ConnectionId { get; }

    /// <summary>The chosen connection-less kind, or null when a connection was chosen.</summary>
    public ConnectionlessSourceKind? Kind { get; }

    public bool IsEmpty => ConnectionId is null && Kind is null;

    public static SourceOrigin ForConnection(Guid connectionId) => new(connectionId, null);

    public static SourceOrigin ForKind(ConnectionlessSourceKind kind) => new(null, kind);

    /// <summary>The value the dialog's select carries for this choice.</summary>
    public string Value =>
        ConnectionId is { } id ? ConnectionPrefix + id.ToString("D")
        : Kind is { } kind ? KindPrefix + kind.Key
        : "";

    /// <summary>Reads a select value back. Anything unrecognised is the empty origin, never a guess.</summary>
    public static SourceOrigin Parse(string? value)
    {
        if (value is null)
            return default;

        if (value.StartsWith(ConnectionPrefix, StringComparison.Ordinal)
            && Guid.TryParse(value[ConnectionPrefix.Length..], out var id))
            return ForConnection(id);

        if (value.StartsWith(KindPrefix, StringComparison.Ordinal)
            && ConnectionlessSourceKinds.Find(value[KindPrefix.Length..]) is { } kind)
            return ForKind(kind);

        return default;
    }
}
