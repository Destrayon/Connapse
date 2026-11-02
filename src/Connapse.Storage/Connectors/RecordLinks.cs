using Connapse.Core;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Connectors.GitHub;

namespace Connapse.Storage.Connectors;

/// <summary>
/// The links a synced record's connector metadata asserts, whichever connector it came from: GitHub (CON-33), Jira
/// and Confluence (CON-34). The pipeline and the eval harness both read links through here, so they cannot disagree.
/// </summary>
public static class RecordLinks
{
    /// <summary>Whether a document's metadata could carry links, so a document that never did skips the link tables.</summary>
    public static bool MayHaveLinks(IReadOnlyDictionary<string, string> metadata) =>
        GitHubLinks.MayHaveLinks(metadata) || JiraLinks.MayHaveLinks(metadata) || ConfluenceLinks.MayHaveLinks(metadata);

    /// <summary>The record's links, or null when its metadata is no connector's that has them.</summary>
    public static DocumentLinks? For(string documentId, IReadOnlyDictionary<string, string> metadata) =>
        GitHubLinks.For(documentId, metadata) ?? JiraLinks.For(documentId, metadata) ?? ConfluenceLinks.For(documentId, metadata);
}
