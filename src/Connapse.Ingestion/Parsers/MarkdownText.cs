namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Keeps text taken from a document from being read as Markdown structure.
/// <para>
/// The Office converters write headings and tables on purpose; a line of the author's own text
/// that happens to start with "# " or "|" must not become another one. The DocumentAware chunker
/// splits sections at headings and keeps tables whole, so such a line would move chunk
/// boundaries and give chunks a heading the document never had.
/// </para>
/// </summary>
internal static class MarkdownText
{
    /// <summary>Escapes a leading heading, table, quote or rule marker with a backslash.</summary>
    public static string EscapeLine(string line)
    {
        if (line.Length == 0)
            return line;

        char first = line[0];
        if (first is '#' or '|' or '>')
            return "\\" + line;

        // A line of only dashes, equals signs or asterisks is a rule, or underlines the line
        // above it into a heading.
        if (line.Length >= 3 && line.All(c => c is '-' or '=' or '*' or ' ') && line.Trim().Length >= 3)
            return "\\" + line;

        return line;
    }
}
