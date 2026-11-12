using System.Globalization;
using System.Text;
using Connapse.Core;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// The text a large CSV is searched by once it has loaded as a table (CON-329). Embedding every row of a 60,000-row
/// file took over ten minutes on two CPUs, and a row-per-passage index answers questions about the data worse than
/// the table does. A large table's text is what it is, its columns, and its first rows; a small one keeps every row.
/// </summary>
internal static class CsvTextDigest
{
    /// <summary>Tables with more rows than this are searched by their summary instead of their full text.</summary>
    public const long MaxFullTextRows = 1000;

    /// <summary>How many of the file's first lines the summary carries, the header included.</summary>
    public const int SampleLines = 51;

    /// <summary>The most text the sample lines take, so a few huge cells can't bring the cost back.</summary>
    public const int MaxSampleChars = 32 * 1024;

    /// <summary>Whether <paramref name="table"/> is large enough to be searched by its summary.</summary>
    public static bool IsLarge(TableInfo table) => table.RowCount > MaxFullTextRows;

    /// <summary>
    /// The summary of <paramref name="table"/>, with the first lines of <paramref name="content"/>, the file's text.
    /// A cell holding a line break can cut the last sample row short; it is text to search, not data to read.
    /// </summary>
    public static string Build(TableInfo table, string content)
    {
        var text = new StringBuilder();
        text.Append(table.Name).Append(": a table of ")
            .Append(table.RowCount.ToString("N0", CultureInfo.InvariantCulture)).Append(" rows and ")
            .Append(table.Columns.Count.ToString(CultureInfo.InvariantCulture)).Append(" columns. ")
            .Append("Text search holds only its first rows; query the table for the rest.\n\n");

        text.Append("Columns: ")
            .AppendJoin(", ", table.Columns.Select(c => $"{c.Name} ({Describe(c.Type)})"))
            .Append("\n\n");

        int lines = 0;
        int start = 0;
        int sampleStart = text.Length;
        while (lines < SampleLines && start < content.Length)
        {
            int end = content.IndexOf('\n', start);
            int next = end < 0 ? content.Length : end + 1;
            ReadOnlySpan<char> line = content.AsSpan(start, (end < 0 ? content.Length : end) - start).TrimEnd('\r');
            if (text.Length - sampleStart + line.Length > MaxSampleChars)
                break;
            text.Append(line).Append('\n');
            lines++;
            start = next;
        }
        return text.ToString();
    }

    private static string Describe(TableColumnType type) => type switch
    {
        TableColumnType.WholeNumber => "whole number",
        TableColumnType.Number => "number",
        TableColumnType.Boolean => "true/false",
        TableColumnType.Date => "date",
        TableColumnType.DateTime => "date and time",
        TableColumnType.Json => "JSON",
        _ => "text",
    };
}
