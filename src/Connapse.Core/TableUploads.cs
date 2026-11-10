namespace Connapse.Core;

/// <summary>
/// The one place that decides whether an uploaded file becomes a table, and whether it stays searchable text too
/// (CON-302). A file it picks loads into its container's tables, one table per file, beside the container's other
/// files. Only .csv for now: the loader reads tab-separated files too, but .tsv isn't an accepted upload type yet.
/// </summary>
public static class TableUploads
{
    /// <summary>Whether a container's file named <paramref name="fileName"/> loads as a table.</summary>
    public static bool LoadsAsTable(string? fileName) =>
        string.Equals(Path.GetExtension(fileName), ".csv", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a table's file is also indexed as text. In a documents container it is, so text search finds a CSV as
    /// it did before tables existed; a tables container holds tables only.
    /// </summary>
    public static bool KeepsText(ContainerKind kind) => kind != ContainerKind.Tables;
}
