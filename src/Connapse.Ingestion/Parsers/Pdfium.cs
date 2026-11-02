using System.Runtime.Versioning;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Where PDFium, which PDFtoImage uses to render PDF pages, has native builds. PDFtoImage marks its
/// calls as supported only on those platforms, so the platform analyzer (CA1416) accepts a call only
/// after a check of <see cref="IsSupported"/>.
/// </summary>
internal static class Pdfium
{
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("macos")]
    public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public const string Unsupported = "PDF pages can be rendered only on Linux, Windows and macOS, where PDFium has native builds.";
}
