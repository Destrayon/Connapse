namespace Connapse.Core;

/// <summary>
/// The one place that decides whether an uploaded file becomes a table (CON-302). A file it picks loads into its
/// container's tables, one table per file, beside the container's other files, and stays searchable text too, so text
/// search finds it as it did before tables existed (CON-319). A .csv loads as one table, an .xlsx as one table per
/// visible sheet (CON-308), and a structured file (.json, .jsonl, .ndjson, .xml, .yaml, .yml) as a table of its
/// records plus one child table per nested list (CON-335); a structured file that holds no list, such as a settings
/// file, stays text only. The loader reads tab-separated files too, but .tsv isn't an accepted upload type yet.
/// </summary>
public static class TableUploads
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".xlsx", ".json", ".jsonl", ".ndjson", ".xml", ".yaml", ".yml",
    };

    /// <summary>Whether a container's file named <paramref name="fileName"/> loads as a table.</summary>
    public static bool LoadsAsTable(string? fileName) => Extensions.Contains(Path.GetExtension(fileName) ?? "");
}
