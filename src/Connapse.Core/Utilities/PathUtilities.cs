using System.Text.RegularExpressions;

namespace Connapse.Core.Utilities;

public static partial class PathUtilities
{
    private static readonly Regex ContainerNameRegex = GenerateContainerNameRegex();

    /// <summary>
    /// Validates a container name. Must be 2-128 characters, lowercase alphanumeric and hyphens,
    /// cannot start or end with a hyphen.
    /// </summary>
    public static bool IsValidContainerName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length >= 2
           && name.Length <= 128
           && ContainerNameRegex.IsMatch(name);

    /// <summary>
    /// Normalizes a file path: ensures leading slash, no trailing slash, forward slashes only.
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        path = path.Replace('\\', '/').Trim();

        if (!path.StartsWith('/'))
            path = "/" + path;

        path = path.TrimEnd('/');

        return string.IsNullOrEmpty(path) ? "/" : path;
    }

    /// <summary>
    /// Normalizes a folder path: ensures leading slash and trailing slash.
    /// </summary>
    public static string NormalizeFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        path = path.Replace('\\', '/').Trim();

        if (!path.StartsWith('/'))
            path = "/" + path;

        if (!path.EndsWith('/'))
            path += "/";

        return path;
    }

    /// <summary>
    /// Gets the parent folder path from a file or folder path.
    /// Returns "/" for root-level items.
    /// </summary>
    public static string GetParentPath(string path)
    {
        path = NormalizePath(path);
        var lastSlash = path.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : path[..lastSlash] + "/";
    }

    /// <summary>
    /// Generates a duplicate filename: "file.pdf" with index 1 becomes "file (1).pdf".
    /// </summary>
    public static string GenerateDuplicateName(string fileName, int index)
    {
        if (index <= 0)
            return fileName;

        var extension = System.IO.Path.GetExtension(fileName);
        var nameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(fileName);

        return $"{nameWithoutExt} ({index}){extension}";
    }

    /// <summary>
    /// Extracts the file name from a full path.
    /// </summary>
    public static string GetFileName(string path)
    {
        path = NormalizePath(path);
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*[a-z0-9]$", RegexOptions.Compiled)]
    private static partial Regex GenerateContainerNameRegex();
}
