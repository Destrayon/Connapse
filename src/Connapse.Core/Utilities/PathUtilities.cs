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
    /// Collapses any ".." segments to prevent path traversal.
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        path = path.Replace('\\', '/').Trim();

        if (!path.StartsWith('/'))
            path = "/" + path;

        path = CollapseDotSegments(path);

        path = path.TrimEnd('/');

        return string.IsNullOrEmpty(path) ? "/" : path;
    }

    /// <summary>
    /// Normalizes a folder path: ensures leading slash and trailing slash.
    /// Collapses any ".." segments to prevent path traversal.
    /// </summary>
    public static string NormalizeFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        path = path.Replace('\\', '/').Trim();

        if (!path.StartsWith('/'))
            path = "/" + path;

        path = CollapseDotSegments(path);

        if (!path.EndsWith('/'))
            path += "/";

        return path;
    }

    /// <summary>
    /// Returns true if the path contains ".." traversal segments that would escape the root.
    /// Use this for early rejection before normalization when you want to block traversal attempts.
    /// </summary>
    public static bool ContainsPathTraversal(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s == "..");
    }

    /// <summary>
    /// Returns true if the filename contains no directory separators or traversal sequences.
    /// Normalizes backslashes so the check works on Linux where '\' is not a path separator.
    /// </summary>
    public static bool IsValidFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var normalized = fileName.Replace('\\', '/');
        var sanitized = Path.GetFileName(normalized);
        return !string.IsNullOrEmpty(sanitized)
               && sanitized == normalized
               && sanitized != ".."
               && sanitized != ".";
    }

    /// <summary>
    /// Resolves "." and ".." segments in a path, clamping at root so traversal
    /// can never escape above "/".
    /// </summary>
    private static string CollapseDotSegments(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();

        foreach (var segment in segments)
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                // At root — clamp, don't escape
                continue;
            }

            stack.Add(segment);
        }

        return "/" + string.Join('/', stack);
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
