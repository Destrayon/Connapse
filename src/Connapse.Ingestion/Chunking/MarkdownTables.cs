using System.Text;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Finds Markdown tables in a section and splits an oversized one on row boundaries.
/// <para>
/// The PDF and Office parsers write tables as Markdown (#597, #599). A plain recursive split
/// cuts a long table at whatever line or word fits, so later pieces are rows with no header and
/// a row can be cut in half -- the column a value belongs to is lost, which is the one thing a
/// table answers.
/// </para>
/// </summary>
internal static class MarkdownTables
{
    /// <summary>The body as consecutive pieces, each a whole table or the text between tables.</summary>
    public static IEnumerable<(int Start, string Text, bool IsTable)> SplitAroundTables(string body)
    {
        var lines = SplitLines(body);
        int i = 0;
        int pieceStart = 0;
        while (i < lines.Count)
        {
            if (IsTableStart(lines, i))
            {
                if (lines[i].Start > pieceStart)
                    yield return (pieceStart, body[pieceStart..lines[i].Start], false);

                int end = i;
                while (end < lines.Count && IsRow(lines[end].Text)) end++;
                int tableEnd = lines[end - 1].Start + lines[end - 1].Text.Length;
                yield return (lines[i].Start, body[lines[i].Start..tableEnd], true);
                pieceStart = tableEnd;
                i = end;
                continue;
            }
            i++;
        }

        if (pieceStart < body.Length)
            yield return (pieceStart, body[pieceStart..], false);
    }

    /// <summary>
    /// Groups of whole rows that fit the budget, each starting with the table's header and
    /// separator. Offsets are into the table text; repeated headers make them approximate.
    /// </summary>
    public static IEnumerable<(int Start, string Text)> SplitByRows(string table, int maxTokens, ITokenCounter counter)
    {
        var lines = SplitLines(table).Where(l => l.Text.Trim().Length > 0).ToList();
        if (lines.Count < 3)
        {
            yield return (0, table);
            yield break;
        }

        string header = lines[0].Text + "\n" + lines[1].Text + "\n";
        var group = new StringBuilder(header);
        int groupStart = lines[2].Start;
        int rowsInGroup = 0;

        for (int i = 2; i < lines.Count; i++)
        {
            string row = lines[i].Text + "\n";
            if (rowsInGroup > 0 && counter.CountTokens(group + row) > maxTokens)
            {
                yield return (groupStart, group.ToString());
                group.Clear().Append(header);
                groupStart = lines[i].Start;
                rowsInGroup = 0;
            }
            group.Append(row);
            rowsInGroup++;
        }

        if (rowsInGroup > 0)
            yield return (groupStart, group.ToString());
    }

    private static bool IsTableStart(List<(int Start, string Text)> lines, int i) =>
        i + 1 < lines.Count && IsRow(lines[i].Text) && IsSeparator(lines[i + 1].Text);

    private static bool IsRow(string line) => line.TrimStart().StartsWith('|');

    private static bool IsSeparator(string line)
    {
        string trimmed = line.Trim();
        return trimmed.StartsWith('|') && trimmed.Contains("---") && trimmed.All(c => c is '|' or '-' or ':' or ' ');
    }

    private static List<(int Start, string Text)> SplitLines(string text)
    {
        var lines = new List<(int Start, string Text)>();
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline;
            lines.Add((start, text[start..end].TrimEnd('\r')));
            if (newline < 0) break;
            start = newline + 1;
        }
        return lines;
    }
}
