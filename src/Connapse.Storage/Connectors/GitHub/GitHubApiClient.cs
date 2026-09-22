using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connapse.Storage.Connectors.GitHub;

/// <summary>
/// The anonymous REST budget ran out mid-cycle. Not a failure: whatever was merged before it is
/// kept, and the next cycle resumes from there.
/// </summary>
internal sealed class GitHubRateLimitedException(DateTimeOffset? resetAt)
    : Exception($"GitHub's anonymous API budget is spent until {resetAt?.ToString("O") ?? "later"}.")
{
    public DateTimeOffset? ResetAt { get; } = resetAt;
}

/// <summary>GitHub answered 401 or 404: the resource is not there for an anonymous caller.</summary>
internal sealed class GitHubNotFoundException(string url) : Exception($"GitHub returned not found for {url}.");

/// <summary>
/// The two list endpoints the issues sync reads, unauthenticated. Hand-rolled rather than Octokit:
/// what is needed is GET plus Link-header paging, and Octokit's models trail the fields this sync
/// depends on (<c>sub_issues_summary</c>).
/// </summary>
internal sealed class GitHubApiClient(HttpClient http, string apiBaseUrl)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Uri _base = new(apiBaseUrl.TrimEnd('/') + "/");

    /// <summary>
    /// Yields one page at a time so a caller can persist each before the next request — a budget
    /// that runs out on page 40 must not discard pages 1 to 39.
    /// </summary>
    public async IAsyncEnumerable<GitHubPage<T>> PagesAsync<T>(
        string relativeUrl, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Uri? next = new(_base, relativeUrl);

        while (next is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Connapse", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            ThrowIfUnusable(response, next);

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var items = await JsonSerializer.DeserializeAsync<List<T>>(body, Json, ct) ?? [];

            yield return new GitHubPage<T>(items, response.Headers.Date);

            next = NextLink(response);
        }
    }

    public async Task<List<T>> GetAllAsync<T>(string relativeUrl, CancellationToken ct = default)
    {
        var all = new List<T>();
        await foreach (var page in PagesAsync<T>(relativeUrl, ct))
            all.AddRange(page.Items);
        return all;
    }

    private void ThrowIfUnusable(HttpResponseMessage response, Uri url)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return;

            case HttpStatusCode.Unauthorized or HttpStatusCode.NotFound:
                throw new GitHubNotFoundException(url.GetLeftPart(UriPartial.Path));

            case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when IsRateLimited(response):
                throw new GitHubRateLimitedException(ResetAt(response));

            case HttpStatusCode.Gone:
                throw new InvalidOperationException(
                    "Issues are disabled on this repository, so there is nothing for an issues source to read.");

            default:
                response.EnsureSuccessStatusCode();
                return;
        }
    }

    /// <summary>
    /// The primary limit says so in <c>x-ratelimit-remaining</c>; a secondary (abuse) limit sends
    /// <c>retry-after</c> instead. A 403 with neither is a real refusal and is not retried.
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.RetryAfter is not null
        || (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
            && remaining.FirstOrDefault() == "0");

    private static DateTimeOffset? ResetAt(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return DateTimeOffset.UtcNow + delta;

        return response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out long epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null;
    }

    /// <summary>
    /// The <c>rel="next"</c> target, followed only on the API's own host: the header comes from
    /// the network, and this client must not be steered into requesting somewhere else.
    /// </summary>
    private Uri? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
            return null;

        foreach (string part in values.SelectMany(v => v.Split(',')))
        {
            string[] segments = part.Split(';');
            if (segments.Length < 2 || !segments.Skip(1).Any(s => s.Trim() == "rel=\"next\""))
                continue;

            string target = segments[0].Trim().TrimStart('<').TrimEnd('>');
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
                && string.Equals(uri.Authority, _base.Authority, StringComparison.OrdinalIgnoreCase)
                && uri.Scheme == _base.Scheme)
                return uri;
        }

        return null;
    }
}

internal sealed record GitHubPage<T>(IReadOnlyList<T> Items, DateTimeOffset? ServerDate);

// ── Payloads: only the fields the sync reads ─────────────────────────────

internal sealed record GitHubUser(string Login);

internal sealed record GitHubLabel(string Name);

internal sealed record GitHubMilestone(string Title);

internal sealed record GitHubPullRequestRef(DateTimeOffset? MergedAt);

internal sealed record GitHubSubIssuesSummary(int Total);

internal sealed record GitHubIssue(
    long Id,
    int Number,
    string Title,
    string? Body,
    string State,
    GitHubUser? User,
    IReadOnlyList<GitHubLabel>? Labels,
    GitHubMilestone? Milestone,
    string HtmlUrl,
    int Comments,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    GitHubUser? ClosedBy,
    GitHubPullRequestRef? PullRequest,
    GitHubSubIssuesSummary? SubIssuesSummary)
{
    [JsonIgnore]
    public bool IsPullRequest => PullRequest is not null;
}

/// <summary>
/// An issue comment or a pull-request review comment. <see cref="IssueUrl"/> is set on the first,
/// <see cref="PullRequestUrl"/> and <see cref="Path"/> on the second.
/// </summary>
internal sealed record GitHubComment(
    long Id,
    string? Body,
    GitHubUser? User,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? IssueUrl = null,
    string? PullRequestUrl = null,
    string? Path = null,
    JsonElement? Minimized = null)
{
    /// <summary>Hidden by a maintainer (spam, off-topic, outdated). Null on a visible comment.</summary>
    [JsonIgnore]
    public bool IsMinimized => Minimized is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };
}
