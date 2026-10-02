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
        ".docx", ".pptx", ".epub",
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
            // Before the directory is read: ZipArchive allocates per entry, so a package listing
            // millions of empty entries would exhaust memory here, inside the "cheap" check.
            long? entries = ReadDeclaredEntryCount(content);
            content.Position = start;
            if (entries > limits.MaxZipEntries)
                return $"it lists {entries:N0} entries, over the {limits.MaxZipEntries:N0} entry limit [too_many_entries]";

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
                return $"it expands {inflated / compressed}:1, over the {limits.MaxCompressionRatio}:1 limit for ZIP-based documents [decompressed_too_large]";

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

    /// <summary>
    /// The entry count from the ZIP's end-of-central-directory record (or its ZIP64 counterpart),
    /// read from the last 64 KiB without touching the directory itself. Null when there is none.
    /// </summary>
    internal static long? ReadDeclaredEntryCount(Stream content)
    {
        const int EocdSize = 22;
        int tail = (int)Math.Min(content.Length, EocdSize + ushort.MaxValue);
        if (tail < EocdSize) return null;

        byte[] buffer = new byte[tail];
        content.Position = content.Length - tail;
        content.ReadExactly(buffer);

        for (int i = tail - EocdSize; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(buffer, i) != 0x06054B50) continue;

            ushort total = BitConverter.ToUInt16(buffer, i + 10);
            if (total != ushort.MaxValue) return total;

            // ZIP64: the locator sits just before the record and points at the real count.
            int locator = i - 20;
            if (locator < 0 || BitConverter.ToUInt32(buffer, locator) != 0x07064B50) return total;
            long recordOffset = BitConverter.ToInt64(buffer, locator + 8);
            // Compared against what is left, so an offset near long.MaxValue cannot wrap past it.
            if (recordOffset < 0 || recordOffset > content.Length - 40) return total;

            byte[] record = new byte[40];
            content.Position = recordOffset;
            content.ReadExactly(record);
            return BitConverter.ToUInt32(record, 0) == 0x06064B50 ? BitConverter.ToInt64(record, 32) : total;
        }

        return null;
    }
}
