namespace Connapse.Core.Utilities;

/// <summary>
/// Sanitizes user-provided values before logging to prevent log forging (CWE-117).
/// Strips newline characters that could be used to inject forged log entries.
/// </summary>
public static class LogSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        return value.Replace("\r", "").Replace("\n", "");
    }
}
