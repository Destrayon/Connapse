using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Connapse.CustomSource.Example;

public sealed record ExampleDocument(
    string Id,
    string ScopeId,
    string Path,
    string FullPath,
    string ContentType,
    long Size,
    string Version,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<string> AccessKeys,
    string AccessVersion);

/// <summary>Document IDs are the base64url of the path inside the scope, so they are safe in a URL segment.</summary>
public static class DocumentId
{
    public static string Encode(string relativePath) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(relativePath));

    public static bool TryDecode(string id, out string relativePath)
    {
        relativePath = "";
        if (!Base64Url.IsValid(id)) return false;
        relativePath = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(id));
        return relativePath.Length > 0;
    }
}

/// <summary>Reads <c>scopes/{scopeId}/</c> — the stand-in for the target system's content.</summary>
public sealed class FolderCatalog(ExampleDataDirectory data)
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
    };

    public IReadOnlyList<ExampleDocument> List(string scopeId)
    {
        string root = ScopeRoot(scopeId);
        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateFiles(root, "*", Walk)
            .Select(file => Describe(scopeId, root, file))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Null when the ID is malformed, points outside the scope, or names no file.</summary>
    public ExampleDocument? Find(string scopeId, string id)
    {
        if (!DocumentId.TryDecode(id, out string relativePath) || System.IO.Path.IsPathRooted(relativePath))
            return null;

        string root = System.IO.Path.GetFullPath(ScopeRoot(scopeId));
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));
        bool inside = full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

        return inside && File.Exists(full) ? Describe(scopeId, root, full) : null;
    }

    /// <summary>One key per top-level folder; files at the scope root share <c>folder:{scope}/</c>.</summary>
    public static string AccessKey(string scopeId, string relativePath)
    {
        int slash = relativePath.IndexOf('/');
        return slash < 0 ? $"folder:{scopeId}/" : $"folder:{scopeId}/{relativePath[..slash]}";
    }

    private string ScopeRoot(string scopeId) => System.IO.Path.Combine(data.Path, "scopes", scopeId);

    private static ExampleDocument Describe(string scopeId, string root, string file)
    {
        string relativePath = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
        FileInfo info = new(file);
        IReadOnlyList<string> keys = [AccessKey(scopeId, relativePath)];

        return new ExampleDocument(
            DocumentId.Encode(relativePath),
            scopeId,
            relativePath,
            info.FullName,
            ContentTypeOf(relativePath),
            info.Length,
            VersionOf(File.ReadAllBytes(file)),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            keys,
            Hash(Encoding.UTF8.GetBytes(string.Join('\n', keys)), 8));
    }

    /// <summary>The content version: a hash of exactly these bytes.</summary>
    public static string VersionOf(byte[] bytes) => Hash(bytes, 16);

    private static string Hash(byte[] bytes, int length) =>
        Convert.ToHexString(SHA256.HashData(bytes))[..length].ToLowerInvariant();

    private static string ContentTypeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".json" => "application/json",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };
}
