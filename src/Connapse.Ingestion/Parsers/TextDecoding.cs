using System.Text;
using UtfUnknown;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Turns a text file's bytes into a string without assuming they are UTF-8.
/// <para>
/// TextParser used to hand every file to a StreamReader, which reads UTF-8 unless a BOM says
/// otherwise. Latin-1 files came out with replacement characters where their accents had been,
/// and BOM-less UTF-16 decoded to text interleaved with NUL characters that PostgreSQL refuses
/// to store, so those documents never reached a final state.
/// </para>
/// </summary>
internal static class TextDecoding
{
    /// <summary>Below this the charset detector is guessing, and Windows-1252 is the safer guess.</summary>
    private const float MinDetectorConfidence = 0.5f;

    /// <summary>How much of the file the UTF-16 heuristic samples.</summary>
    private const int Utf16SampleBytes = 4096;

    /// <summary>How much of the file the charset detector reads.</summary>
    private const int DetectorSampleBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextDecoding()
    {
        // Windows-1252 and the legacy code pages the detector can name live outside the
        // encodings .NET ships enabled.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Decodes in order of certainty: a BOM, BOM-less UTF-16, strict UTF-8, the charset
    /// detector when it is confident, and finally Windows-1252, which maps every byte.
    /// </summary>
    public static (string Text, Encoding Encoding) Decode(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding = Detect(bytes, out int preambleLength);
        string text = encoding.GetString(bytes[preambleLength..]);

        // A stray NUL survives every decoding above, and PostgreSQL text columns reject it.
        if (text.Contains('\0'))
            text = text.Replace("\0", string.Empty);

        return (text, encoding);
    }

    internal static Encoding Detect(ReadOnlySpan<byte> bytes, out int preambleLength)
    {
        preambleLength = 0;

        // UTF-32 LE before UTF-16 LE: its BOM starts with the same two bytes.
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00])) { preambleLength = 4; return Encoding.UTF32; }
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF])) { preambleLength = 4; return new UTF32Encoding(bigEndian: true, byteOrderMark: true); }
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { preambleLength = 3; return Encoding.UTF8; }
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) { preambleLength = 2; return Encoding.Unicode; }
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) { preambleLength = 2; return Encoding.BigEndianUnicode; }

        // Before UTF-8, deliberately: NUL is a valid UTF-8 byte, so UTF-16 text in a Latin
        // script passes the strict UTF-8 check while being nothing of the sort.
        if (LooksLikeUtf16(bytes, out bool bigEndian))
            return bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;

        if (IsValidUtf8(bytes))
            return Encoding.UTF8;

        // A sample is enough to name a code page, and the detector's cost grows with its input.
        byte[] sample = bytes[..Math.Min(bytes.Length, DetectorSampleBytes)].ToArray();
        DetectionDetail? detected = CharsetDetector.DetectFromBytes(sample).Detected;
        if (detected?.Encoding is { } guess && detected.Confidence >= MinDetectorConfidence)
            return guess;

        return Encoding.GetEncoding(1252);
    }

    /// <summary>
    /// UTF-16 text in a Latin script has a zero high byte in nearly every code unit, so one
    /// byte parity is mostly NUL and the other almost never is. Text in other scripts has few
    /// zero bytes and is left to the charset detector.
    /// </summary>
    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, out bool bigEndian)
    {
        bigEndian = false;
        ReadOnlySpan<byte> sample = bytes[..Math.Min(bytes.Length, Utf16SampleBytes)];
        if (sample.Length < 4) return false;

        int evenNuls = 0, oddNuls = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            if (sample[i] != 0) continue;
            if (i % 2 == 0) evenNuls++; else oddNuls++;
        }

        int pairs = sample.Length / 2;
        bool mostlyOdd = oddNuls >= pairs * 0.3 && evenNuls <= pairs * 0.05;
        bool mostlyEven = evenNuls >= pairs * 0.3 && oddNuls <= pairs * 0.05;

        bigEndian = mostlyEven;
        return mostlyOdd || mostlyEven;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
