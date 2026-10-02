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
    /// <summary>
    /// Escapes a leading heading, table, quote or rule marker with a backslash. Markdown allows up
    /// to three spaces before one, so the marker is looked for after them.
    /// </summary>
    public static string EscapeLine(string line)
    {
        int indent = LeadingSpaces(line);
        if (indent == line.Length || indent > 3)
            return line;

        string rest = line[indent..];
        char first = rest[0];
        if (first is '#' or '|' or '>')
            return line[..indent] + "\\" + rest;

        // A line of only dashes, equals signs or asterisks is a rule, or underlines the line
        // above it into a heading.
        if (rest.Length >= 3 && rest.All(c => c is '-' or '=' or '*' or ' ') && rest.Trim().Length >= 3)
            return line[..indent] + "\\" + rest;

        return line;
    }

    private static int LeadingSpaces(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ')
            count++;
        return count;
    }

    /// <summary>
    /// Pushes every ATX heading down by <paramref name="levels"/>, to at most level 6, so a document
    /// embedded under a heading of its own -- an email attachment -- nests beneath it in the
    /// chunker's breadcrumb instead of standing beside it. Fenced code is left alone.
    /// </summary>
    public static string DemoteHeadings(string markdown, int levels)
    {
        var lines = markdown.Split('\n');
        bool inFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            int indent = LeadingSpaces(line);
            if (inFence || indent > 3 || indent == line.Length || line[indent] != '#')
                continue;

            int marker = indent;
            while (marker < line.Length && line[marker] == '#')
                marker++;
            int depth = marker - indent;
            if (depth > 6 || (marker < line.Length && line[marker] != ' '))
                continue;

            lines[i] = new string('#', Math.Min(6, depth + levels)) + line[marker..];
        }

        return string.Join('\n', lines);
    }
}
