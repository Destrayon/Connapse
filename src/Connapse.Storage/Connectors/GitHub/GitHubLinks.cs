using System.Globalization;
using Connapse.Core;

namespace Connapse.Storage.Connectors.GitHub;

/// <summary>
/// The links a GitHub document asserts in the link layer, read from the <c>github:*</c> metadata the connector
/// gives it (CON-33). Nothing here asks GitHub anything: every link is one the sync already holds.
/// <list type="bullet">
/// <item><c>closes</c>: a pull request to the issues its description closes by GitHub's keywords.</item>
/// <item><c>references</c>: a record to the records its text mentions as <c>#N</c>. GitHub's
/// <c>cross-referenced</c> timeline event is the other end of the same mention, and costs a request per record.</item>
/// <item><c>parent</c>: a sub-issue to its parent.</item>
/// <item><c>touches</c>: a pull request to the files it changed. A docs-source document stands for its file, so
/// a pull request reaches the documentation it edited.</item>
/// </list>
/// Node ids name the repository, lowercased as GitHub matches it, so an issues source and a docs source of one
/// repository meet on the same nodes.
/// </summary>
public static class GitHubLinks
{
    public const string Closes = "closes";
    public const string References = "references";
    public const string Parent = "parent";
    public const string Touches = "touches";

    private const string Prefix = GitHubRecordRenderer.MetadataPrefix;

    /// <summary>The metadata key listing a pull request's files, one path per line.</summary>
    public const string FilesKey = Prefix + "files";

    /// <summary>The metadata key a docs-source document carries its own path under.</summary>
    public const string PathKey = Prefix + "path";

    /// <summary>
    /// Whether a document's metadata could carry links, so a document that never did skips the link tables.
    /// </summary>
    public static bool MayHaveLinks(IReadOnlyDictionary<string, string> metadata) =>
        metadata.ContainsKey(Prefix + "repo");

    /// <summary>The document's links, or null when its metadata is not a GitHub record's or document's.</summary>
    public static DocumentLinks? For(string documentId, IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(Prefix + "repo", out string? repoValue) || string.IsNullOrWhiteSpace(repoValue))
            return null;
        string repo = repoValue.Trim().ToLowerInvariant();

        if (metadata.TryGetValue(PathKey, out string? docPath) && docPath.StartsWith('/'))
        {
            string node = FileNode(repo, docPath);
            return new DocumentLinks(documentId, node,
                [new LinkFact(node, LinkFactField.Kind, "file"), new LinkFact(node, LinkFactField.Name, docPath)], []);
        }

        if (Number(metadata.GetValueOrDefault(Prefix + "number")) is not int number)
            return null;

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
        foreach (int mentioned in Numbers(metadata.GetValueOrDefault(Prefix + "references")))
            Add(References, RecordNode(repo, mentioned));
        if (Number(metadata.GetValueOrDefault(Prefix + "parent")) is int parent)
            Add(Parent, RecordNode(repo, parent));
        if (pullRequest && metadata.TryGetValue(FilesKey, out string? files))
        {
            foreach (string path in files.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!path.StartsWith('/'))
                    continue;
                string file = FileNode(repo, path);
                // Named here too, so a file no document stands for can still be walked through.
                if (seen.Add(Touches + "|" + file))
                {
                    facts.Add(new LinkFact(file, LinkFactField.Name, path));
                    links.Add(new LinkAssertion(EdgeId(Touches, self, file), self, file, Touches));
                }
            }
        }

        return new DocumentLinks(documentId, self, facts, links);
    }

    public static string RecordNode(string repo, int number) =>
        $"gh:{repo.ToLowerInvariant()}#{number.ToString(CultureInfo.InvariantCulture)}";

    public static string FileNode(string repo, string path) => $"gh:{repo.ToLowerInvariant()}:{path}";

    /// <summary>Derived from the link itself, so every document asserting it writes the same id.</summary>
    private static string EdgeId(string type, string from, string to) => $"{type}|{from}|{to}";

    private static IEnumerable<int> Numbers(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Number).OfType<int>();

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;
}
