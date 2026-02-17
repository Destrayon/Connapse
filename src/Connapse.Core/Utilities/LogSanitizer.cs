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
            return string.Empty;

        // Remove CR/LF and normalize other control characters to prevent log forging
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            // Strip newline characters entirely
            if (ch == '\r' || ch == '\n')
                continue;

            // Replace any other control character with a safe placeholder (space)
            if (char.IsControl(ch))
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
