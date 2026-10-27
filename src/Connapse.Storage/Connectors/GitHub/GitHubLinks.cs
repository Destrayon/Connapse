using System.Globalization;
using Connapse.Core;

namespace Connapse.Storage.Connectors.GitHub;

/// <summary>
/// The links a GitHub record asserts in the link layer, read from the <c>github:*</c> metadata the connector gives
/// it (CON-33). Nothing here asks GitHub anything: both links are ones the sync already holds.
/// <list type="bullet">
/// <item><c>closes</c>: a pull request to the issues its description closes by GitHub's keywords.</item>
/// <item><c>parent</c>: a sub-issue to its parent.</item>
/// </list>
/// Plain <c>#N</c> mentions are left out: measured, they pulled in records the question was not about and pushed
/// the record it was about down. Node ids name the repository, lowercased as GitHub matches it.
/// </summary>
public static class GitHubLinks
{
    public const string Closes = "closes";
    public const string Parent = "parent";

    private const string Prefix = GitHubRecordRenderer.MetadataPrefix;

    /// <summary>
    /// Whether a document's metadata could carry links, so a document that never did skips the link tables.
    /// </summary>
    public static bool MayHaveLinks(IReadOnlyDictionary<string, string> metadata) =>
        metadata.ContainsKey(Prefix + "repo");

    /// <summary>The record's links, or null when its metadata is not a GitHub issue's or pull request's.</summary>
    public static DocumentLinks? For(string documentId, IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(Prefix + "repo", out string? repoValue) || string.IsNullOrWhiteSpace(repoValue)
            || Number(metadata.GetValueOrDefault(Prefix + "number")) is not int number)
            return null;
        string repo = repoValue.Trim().ToLowerInvariant();

        bool pullRequest = metadata.GetValueOrDefault(Prefix + "type") == "pull_request";
        string self = RecordNode(repo, number);
        List<LinkFact> facts =
        [
            new(self, LinkFactField.Kind, pullRequest ? "pull_request" : "issue"),
            new(self, LinkFactField.Name, $"{repo}#{number}"),
        ];
        List<LinkAssertion> links = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        void Add(string type, string to)
        {
            if (to != self && seen.Add(type + "|" + to))
                links.Add(new LinkAssertion(EdgeId(type, self, to), self, to, type));
        }

        if (pullRequest)
        {
            foreach (int closed in Numbers(metadata.GetValueOrDefault(Prefix + "closes")))
                Add(Closes, RecordNode(repo, closed));
        }
        if (Number(metadata.GetValueOrDefault(Prefix + "parent")) is int parent)
            Add(Parent, RecordNode(repo, parent));

        return new DocumentLinks(documentId, self, facts, links);
    }

    public static string RecordNode(string repo, int number) =>
        $"gh:{repo.ToLowerInvariant()}#{number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Derived from the link itself, so every document asserting it writes the same id.</summary>
    private static string EdgeId(string type, string from, string to) => $"{type}|{from}|{to}";

    private static IEnumerable<int> Numbers(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Number).OfType<int>();

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;
}
