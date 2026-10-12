using Connapse.Core;

namespace Connapse.Web.Services;

/// <summary>
/// Deleting a source removes everything Connapse indexed from it, so the Sources page and the
/// REST API both ask for the source's name, as deleting a connection does.
/// </summary>
public static class SourceDeletion
{
    /// <summary>Whether <paramref name="confirmName"/> is the source's name, the value typed to confirm.</summary>
    public static bool Confirmed(Source source, string? confirmName) =>
        string.Equals(confirmName?.Trim(), source.Name, StringComparison.Ordinal);

    /// <summary>What deleting the source removes, in a sentence.</summary>
    public static string Consequence(Source source) =>
        $"Deleting '{source.Name}' removes its {Documents(source.DocumentCount)} from search. "
        + "The data in the external system is untouched; re-creating the source syncs it again.";

    /// <summary>The 400 answer when the API's confirmName is missing or wrong.</summary>
    public static string ConfirmMismatch(Source source) =>
        $"confirmName must be the source's name, '{source.Name}'. {Consequence(source)}";

    public static string Documents(int count) => count == 1 ? "1 indexed document" : $"{count} indexed documents";
}
