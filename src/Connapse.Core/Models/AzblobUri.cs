namespace Connapse.Core;

/// <summary>Parses <c>azblob://account/container/blobpath</c> resource URIs into a
/// <see cref="Gen2Path"/> (container = filesystem). The blob path is required and kept verbatim.</summary>
public static class AzblobUri
{
    private const string Scheme = "azblob://";

    public static bool IsAzblob(string? resourceUri) =>
        resourceUri is not null && resourceUri.StartsWith(Scheme, StringComparison.Ordinal);

    public static bool TryParse(string? resourceUri, out Gen2Path path)
    {
        path = new Gen2Path("", "", "");
        if (!IsAzblob(resourceUri))
            return false;

        string rest = resourceUri![Scheme.Length..];
        int firstSlash = rest.IndexOf('/');
        if (firstSlash <= 0)
            return false; // no container
        int secondSlash = rest.IndexOf('/', firstSlash + 1);
        if (secondSlash < 0 || secondSlash == rest.Length - 1)
            return false; // no container/path boundary, or empty blob path

        string account = rest[..firstSlash];
        string container = rest[(firstSlash + 1)..secondSlash];
        string blobPath = rest[(secondSlash + 1)..];
        if (account.Length == 0 || container.Length == 0 || blobPath.Length == 0)
            return false;

        path = new Gen2Path(account, container, blobPath);
        return true;
    }
}
