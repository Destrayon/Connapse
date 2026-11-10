namespace Connapse.Core;

/// <summary>
/// The one place that decides whether an uploaded file becomes a table (CON-302). A file it picks loads into its
/// container's tables, one table per file, beside the container's other files, and stays searchable text too, so text
/// search finds it as it did before tables existed (CON-319). Only .csv for now: the loader reads tab-separated files
/// too, but .tsv isn't an accepted upload type yet.
/// </summary>
public static class TableUploads
{
    /// <summary>Whether a container's file named <paramref name="fileName"/> loads as a table.</summary>
    public static bool LoadsAsTable(string? fileName) =>
        string.Equals(Path.GetExtension(fileName), ".csv", StringComparison.OrdinalIgnoreCase);
}
