namespace Connapse.Core.Utilities;

/// <summary>
/// Where a document of a connected custom source came from: <c>custom://{sourceId}/{escaped document id}</c>.
/// The source names the connection and provider to ask; the document ID is the middle man's own, escaped as
/// one path segment exactly as the connector's paths escape it.
/// </summary>
public static class CustomResourceUri
{
    public const string Scheme = "custom://";

    public static string For(Guid sourceId, string documentId) => $"{SourcePrefix(sourceId)}{Uri.EscapeDataString(documentId)}";

    public static string SourcePrefix(Guid sourceId) => $"{Scheme}{sourceId}/";

    public static bool TryParse(string? uri, out Guid sourceId, out string documentId)
    {
        sourceId = Guid.Empty;
        documentId = "";
        if (uri is null || !uri.StartsWith(Scheme, StringComparison.Ordinal)) return false;

        string rest = uri[Scheme.Length..];
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1 || !Guid.TryParse(rest[..slash], out sourceId)) return false;

        documentId = Uri.UnescapeDataString(rest[(slash + 1)..]);
        return documentId.Length > 0;
    }
}
