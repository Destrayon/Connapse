using System.Globalization;

namespace Connapse.Core.Utilities;

/// <summary>What the one upload limit, <see cref="UploadSettings.MaxFileBytes"/>, means for a request and how a refusal reads.</summary>
public static class UploadLimits
{
    /// <summary>Room for a form's boundaries, part headers and other fields around a file at the limit.</summary>
    public const long FormOverheadBytes = 1024 * 1024;

    /// <summary>
    /// Kestrel's own default. An upload request may always be this large, so that a low file limit still leaves room
    /// for a request of several small files; the file limit then applies to each file.
    /// </summary>
    public const long SmallestRequestBytes = 30_000_000;

    /// <summary>The largest request body an upload may send.</summary>
    public static long RequestBytes(long maxFileBytes) =>
        Math.Max(maxFileBytes > long.MaxValue - FormOverheadBytes ? long.MaxValue : maxFileBytes + FormOverheadBytes, SmallestRequestBytes);

    public static string FileTooLarge(long bytes, long maxFileBytes) =>
        $"The file is {Megabytes(bytes)} MB, over the {Megabytes(maxFileBytes)} MB limit.";

    public static string RequestTooLarge(long bytes, long maxFileBytes) =>
        $"This upload is {Megabytes(bytes)} MB, over the {Megabytes(maxFileBytes)} MB limit. " +
        "Send several files in separate requests.";

    /// <summary>For a body of no stated length, refused before its end, so its size isn't known.</summary>
    public static string RequestOverLimit(long maxFileBytes) =>
        $"This upload is over the {Megabytes(maxFileBytes)} MB limit. Send several files in separate requests.";

    /// <summary>Rounded up, so a file one byte over the limit doesn't read as being at it.</summary>
    private static string Megabytes(long bytes) =>
        (Math.Ceiling(bytes / (1024d * 1024d) * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture);
}
