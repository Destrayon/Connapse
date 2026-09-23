using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Storage.Connectors;

namespace Connapse.Web.Components.Settings;

/// <summary>
/// Adding a public GitHub repository on a GitHub App connection: one address in, up to two sources
/// out — the repository's markdown docs and its issues and pull requests.
/// <para>
/// A record rather than logic in the New source dialog for the same reason as
/// <see cref="SourceForm"/>: the scope keys written here must match the ones
/// <c>ConnectorFactory</c> reads, and the page has no test harness.
/// </para>
/// </summary>
public sealed record GitHubRepositoryForm
{
    private const int MaxSourceName = 128;

    /// <summary>A github.com URL or <c>owner/repo</c>.</summary>
    public string Repository { get; set; } = "";

    public bool IncludeDocs { get; set; } = true;

    public bool IncludeIssues { get; set; } = true;

    public bool IncludeComments { get; set; } = true;

    /// <summary>Newline-separated file-name globs for docs. Blank means the markdown defaults.</summary>
    public string? DocPatterns { get; set; }

    /// <summary>
    /// Reads <c>owner/repo</c>, or any github.com URL that starts with them — a browser address
    /// such as <c>https://github.com/o/r/tree/main/docs</c> is what people paste. Anything on
    /// another host is refused: a source without a connection may only read github.com.
    /// </summary>
    public static bool TryParse(string? input, out string owner, out string repo)
    {
        owner = repo = "";
        string text = (input ?? "").Trim();
        if (text.Length == 0)
            return false;

        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http")
                || !string.Equals(uri.Host, GitHubConnectorConfig.PublicHost, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Host, "www." + GitHubConnectorConfig.PublicHost, StringComparison.OrdinalIgnoreCase))
                return false;

            text = uri.AbsolutePath;
        }
        else if (text.StartsWith(GitHubConnectorConfig.PublicHost + "/", StringComparison.OrdinalIgnoreCase))
        {
            text = text[(GitHubConnectorConfig.PublicHost.Length + 1)..];
        }

        string[] segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        string candidateRepo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        if (!GitHubConnectorConfig.IsValidOwner(segments[0]) || !GitHubConnectorConfig.IsValidRepo(candidateRepo))
            return false;

        owner = segments[0];
        repo = candidateRepo;
        return true;
    }

    /// <summary>The first problem with the form, or null when it can be submitted.</summary>
    public string? Validate()
    {
        if (!TryParse(Repository, out _, out _))
            return "Enter a public GitHub repository as owner/repo or its github.com address.";

        if (!IncludeDocs && !IncludeIssues)
            return "Choose docs, issues and pull requests, or both.";

        return null;
    }

    /// <summary>
    /// The sources to create on <paramref name="connectionId"/>, docs first. Call
    /// <see cref="Validate"/> first. No sync interval is set: reading as the App, an idle repository
    /// costs nothing to poll, so the default applies.
    /// </summary>
    public IReadOnlyList<CreateSourceRequest> ToRequests(Guid connectionId)
    {
        if (!TryParse(Repository, out string owner, out string repo))
            throw new InvalidOperationException("The repository address is not valid.");

        var requests = new List<CreateSourceRequest>();

        if (IncludeDocs)
        {
            var scope = Scope(owner, repo, GitHubContentKind.Docs);
            var patterns = SourceForm.ParsePatterns(DocPatterns);
            if (patterns.Count > 0)
                scope["includePatterns"] = new JsonArray([.. patterns.Select(p => (JsonNode)p)]);

            requests.Add(new CreateSourceRequest(
                Name(owner, repo, "docs"),
                ConnectionId: connectionId,
                ScopeJson: scope.ToJsonString(),
                Description: $"Markdown docs from github.com/{owner}/{repo}"));
        }

        if (IncludeIssues)
        {
            var scope = Scope(owner, repo, GitHubContentKind.IssuesAndPullRequests);
            scope["includeComments"] = IncludeComments;

            requests.Add(new CreateSourceRequest(
                Name(owner, repo, "issues"),
                ConnectionId: connectionId,
                ScopeJson: scope.ToJsonString(),
                Description: $"Issues and pull requests from github.com/{owner}/{repo}"));
        }

        return requests;
    }

    private static JsonObject Scope(string owner, string repo, GitHubContentKind kind) => new()
    {
        ["owner"] = owner,
        ["repo"] = repo,
        ["kind"] = kind.ToString(),
    };

    /// <summary>
    /// <c>owner/repo docs</c>. A 39-character owner and a 100-character repository name can
    /// exceed the store's 128-character limit, so the repository part gives way.
    /// </summary>
    private static string Name(string owner, string repo, string suffix)
    {
        string name = $"{owner}/{repo} {suffix}";
        if (name.Length <= MaxSourceName)
            return name;

        int keep = MaxSourceName - owner.Length - suffix.Length - 3;
        return $"{owner}/{repo[..keep]}… {suffix}";
    }
}
