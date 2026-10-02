using System.Text;

namespace Connapse.Core.Utilities;

/// <summary>
/// Makes text safe to write to a PostgreSQL text column.
/// <para>
/// PostgreSQL rejects NUL outright (SQLSTATE 22021), and Npgsql cannot encode a lone UTF-16
/// surrogate to UTF-8 at all. Parsers produce both: PDF fonts map glyphs to U+0000 and to
/// half-pairs, and math alphabets live above the BMP where a broken pair is easy to emit. On
/// 2026-10-01 these failed 74 otherwise readable olmOCR-bench PDFs, and an error message quoting
/// such text could not be saved either, which left documents stuck in Processing (#334).
/// </para>
/// </summary>
public static class StorableText
{
    /// <summary>
    /// Removes NUL characters and replaces unpaired surrogates with U+FFFD. Returns the input
    /// unchanged, without allocating, when there is nothing to fix.
    /// </summary>
    public static string Clean(string value)
    {
        int first = FirstProblem(value);
        if (first < 0) return value;

        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, first);

        for (int i = first; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\0') continue;

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(c).Append(value[i + 1]);
                    i++;
                }
                else
                {
                    builder.Append('�');
                }
                continue;
            }

            builder.Append(char.IsLowSurrogate(c) ? '�' : c);
        }

        return builder.ToString();
    }

    private static int FirstProblem(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\0') return i;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { i++; continue; }
                return i;
            }
            if (char.IsLowSurrogate(c)) return i;
        }

        return -1;
    }
}
