using System.IO.Compression;
using Connapse.Core;

namespace Connapse.Ingestion.Validation;

/// <summary>
/// Size checks that run before a parser touches a file. Each breach is reported with a reason
/// code in brackets, so an administrator reading the document's error can tell a limit that
/// fired from a file that could not be read.
/// </summary>
public static class ParseLimits
{
    /// <summary>Below this, a high compression ratio is ordinary XML and not worth refusing.</summary>
    private const long RatioCheckFloorBytes = 10L * 1024 * 1024;

    private static readonly HashSet<string> ZipOfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".pptx",
    };

    /// <summary>
    /// Explains why the file is too large to parse, or returns null when it is within limits.
    /// Leaves the stream at the position it started from.
    /// </summary>
    public static string? CheckInput(Stream content, string extension, UploadSettings limits)
    {
        if (content.Length > limits.MaxFileBytes)
            return $"the file is {Megabytes(content.Length)} MB, over the {Megabytes(limits.MaxFileBytes)} MB limit [file_too_large]";

        if (!ZipOfficeExtensions.Contains(extension))
            return null;

        long start = content.Position;
        try
        {
            // The sizes come from the ZIP central directory, so this reads a few kilobytes no
            // matter how far the package would inflate.
            using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            long inflated = 0;
            long compressed = 0;
            foreach (var entry in zip.Entries)
            {
                inflated += entry.Length;
                compressed += entry.CompressedLength;
            }

            if (inflated > limits.MaxDecompressedBytes)
                return $"it expands to {Megabytes(inflated)} MB, over the {Megabytes(limits.MaxDecompressedBytes)} MB limit [decompressed_too_large]";

            if (inflated > RatioCheckFloorBytes && compressed > 0 && inflated / compressed > limits.MaxCompressionRatio)
                return $"it expands {inflated / compressed}:1, over the {limits.MaxCompressionRatio}:1 limit for Office files [decompressed_too_large]";

            return null;
        }
        catch (InvalidDataException)
        {
            // Not a readable ZIP. The content sniffer or the parser reports that more usefully.
            return null;
        }
        finally
        {
            content.Position = start;
        }
    }

    private static long Megabytes(long bytes) => bytes / (1024 * 1024);
}
