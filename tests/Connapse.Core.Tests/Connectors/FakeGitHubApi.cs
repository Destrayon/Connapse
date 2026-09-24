using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// An in-memory stand-in for the slice of GitHub's REST API the issues sync reads: the issue
/// list, the repository-wide comment lists, sub-issues, and one issue's comments. It honours
/// <c>since</c> (inclusive, like GitHub), pages through Link headers, and can run out of budget
/// the way the anonymous rate limit does.
/// <para>
/// Mutations move the clock forward a minute and bump <c>updated_at</c> where GitHub does:
/// adding or deleting a comment moves its issue, editing one does not.
/// </para>
/// </summary>
public sealed class FakeGitHubApi : HttpMessageHandler
{
    public const string BaseUrl = "https://api.github.test";

    private readonly Dictionary<int, Issue> _issues = [];
    private readonly Dictionary<long, Comment> _comments = [];
    private long _nextCommentId = 1000;

    public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Items per page, kept small so tests exercise paging.</summary>
    public int PageSize { get; set; } = 2;

    /// <summary>Requests allowed before every further one is refused as rate-limited. Null is unlimited.</summary>
    public int? Budget { get; set; }

    /// <summary>Answers every request 404, as GitHub does once a repository is not public.</summary>
    public bool Gone { get; set; }

    public int Requests { get; private set; }

    public List<string> RequestedPaths { get; } = [];

    /// <summary>What <c>GET /repos/{owner}/{repo}</c> reports: public, private, or internal.</summary>
    public string Visibility { get; set; } = "public";

    /// <summary>Answers for successive visibility checks, before falling back to <see cref="Visibility"/>.</summary>
    public Queue<string> VisibilityAnswers { get; } = new();

    /// <summary>The bearer token each request carried, or null when it carried none.</summary>
    public List<string?> Tokens { get; } = [];

    /// <summary>Tokens whose budget is spent: a request carrying one is refused as rate-limited.</summary>
    public HashSet<string> SpentTokens { get; } = [];

    /// <summary>Requests answered 304 because their If-None-Match matched — free on the real API.</summary>
    public int NotModified { get; private set; }

    /// <summary>
    /// Answers with an ETag derived from the body, or 304 when the request already holds it — the
    /// way GitHub makes an unchanged probe free.
    /// </summary>
    private Task<HttpResponseMessage> Respond(HttpRequestMessage request, string json)
    {
        string etag = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16] + "\"";

        if (request.Headers.TryGetValues("If-None-Match", out var held) && held.Contains(etag))
        {
            NotModified++;
            var unchanged = new HttpResponseMessage(HttpStatusCode.NotModified);
            unchanged.Headers.TryAddWithoutValidation("ETag", etag);
            return Task.FromResult(unchanged);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        response.Headers.Date = Now;
        return Task.FromResult(response);
    }

    public HttpClient CreateClient() => new(this, disposeHandler: false);

    // ── Mutations ──────────────────────────────────────────────────────────

    public void UpsertIssue(
        int number, string title, string? body = null, bool pullRequest = false, string state = "open",
        string[]? labels = null, string? milestone = null, bool merged = false)
    {
        Tick();
        if (!_issues.TryGetValue(number, out var issue))
        {
            issue = new Issue { Number = number, CreatedAt = Now };
            _issues[number] = issue;
        }

        issue.Title = title;
        issue.Body = body;
        issue.PullRequest = pullRequest;
        issue.State = merged ? "closed" : state;
        issue.MergedAt = merged ? Now : null;
        issue.ClosedAt = issue.State == "closed" ? Now : null;
        issue.Labels = labels ?? [];
        issue.Milestone = milestone;
        issue.UpdatedAt = Now;
    }

    /// <summary>
    /// Retitles an issue at the newest issue's own second, so it sorts behind that issue: the
    /// same-second edit a one-item probe cannot see.
    /// </summary>
    public void EditIssueInTheSameSecond(int number, string title)
    {
        var issue = _issues[number];
        issue.Title = title;
        issue.UpdatedAt = _issues.Values.Max(i => i.UpdatedAt);
    }

    public void DeleteIssue(int number)
    {
        Tick();
        _issues.Remove(number);
    }

    public long AddComment(int number, string login, string body, string? reviewPath = null)
    {
        Tick();
        long id = _nextCommentId++;
        _comments[id] = new Comment
        {
            Id = id, Number = number, Login = login, Body = body, ReviewPath = reviewPath,
            CreatedAt = Now, UpdatedAt = Now,
        };
        Bump(number);
        return id;
    }

    public void EditComment(long id, string body)
    {
        Tick();
        _comments[id].Body = body;
        _comments[id].UpdatedAt = Now;
    }

    public void MinimizeComment(long id)
    {
        Tick();
        _comments[id].Minimized = true;
        _comments[id].UpdatedAt = Now;
    }

    public void DeleteComment(long id)
    {
        Tick();
        int number = _comments[id].Number;
        _comments.Remove(id);
        Bump(number);
    }

    public void SetSubIssues(int parent, params int[] children)
    {
        Tick();
        _issues[parent].SubIssues = [.. children];
        Bump(parent);
    }

    private void Bump(int number)
    {
        if (_issues.TryGetValue(number, out var issue))
            issue.UpdatedAt = Now;
    }

    private void Tick() => Now = Now.AddMinutes(1);

    // ── Transport ──────────────────────────────────────────────────────────

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        var uri = request.RequestUri!;
        RequestedPaths.Add(uri.PathAndQuery);
        string? token = request.Headers.Authorization?.Parameter;
        Tokens.Add(token);

        if (token is not null && SpentTokens.Contains(token))
        {
            var spent = new HttpResponseMessage(HttpStatusCode.Forbidden);
            spent.Headers.Add("x-ratelimit-remaining", "0");
            spent.Headers.Add("x-ratelimit-reset", Now.AddHours(1).ToUnixTimeSeconds().ToString());
            return Task.FromResult(spent);
        }

        if (Budget is { } budget && Requests > budget)
        {
            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.Add("x-ratelimit-remaining", "0");
            limited.Headers.Add("x-ratelimit-reset", Now.AddHours(1).ToUnixTimeSeconds().ToString());
            return Task.FromResult(limited);
        }

        if (Gone)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        string[] path = uri.AbsolutePath.Trim('/').Split('/');
        if (path.Length == 3)
        {
            // repos/{owner}/{repo}: the visibility check every authenticated sync makes first.
            string visibility = VisibilityAnswers.TryDequeue(out string? next) ? next : Visibility;
            return Respond(request, JsonSerializer.Serialize(new
            {
                id = 1296269, @private = visibility != "public", visibility,
            }));
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        DateTimeOffset? since = query["since"] is { } s ? DateTimeOffset.Parse(s) : null;
        int page = int.TryParse(query["page"], out int p) ? p : 1;
        string[] segments = uri.AbsolutePath.Trim('/').Split('/');

        // repos/{owner}/{repo}/...
        List<object> items = segments[3..] switch
        {
            ["issues"] when query["sort"] == "updated" =>
                [.. _issues.Values.Where(i => since is null || i.UpdatedAt >= since)
                    .OrderBy(i => i.UpdatedAt).ThenBy(i => i.Number).Select(IssueJson)],
            ["issues"] =>
                [.. _issues.Values.OrderByDescending(i => i.Number).Select(IssueJson)],
            ["issues", "comments"] =>
                [.. Comments(review: false, since).Select(CommentJson)],
            ["pulls", "comments"] =>
                [.. Comments(review: true, since).Select(CommentJson)],
            ["issues", var n, "sub_issues"] when _issues.TryGetValue(int.Parse(n), out var parent) =>
                [.. parent.SubIssues.Where(_issues.ContainsKey).Select(c => IssueJson(_issues[c]))],
            ["issues", var n, "comments"] when _issues.ContainsKey(int.Parse(n)) =>
                [.. _comments.Values.Where(c => c.Number == int.Parse(n) && c.ReviewPath is null)
                    .OrderBy(c => c.Id).Select(CommentJson)],
            _ => throw new InvalidOperationException("unexpected request " + uri),
        };

        // A probe asks for the newest item first, one per page; honoured so that it sees the item
        // that moved rather than whatever happens to sort first.
        if (query["direction"] == "desc")
            items.Reverse();
        int pageSize = int.TryParse(query["per_page"], out int requested) && requested < PageSize ? requested : PageSize;

        var slice = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var response = Respond(request, JsonSerializer.Serialize(slice)).Result;

        if (response.StatusCode == HttpStatusCode.OK && page * pageSize < items.Count)
        {
            query["page"] = (page + 1).ToString();
            response.Headers.Add("Link", $"<{BaseUrl}{uri.AbsolutePath}?{query}>; rel=\"next\"");
        }

        return Task.FromResult(response);
    }

    private IEnumerable<Comment> Comments(bool review, DateTimeOffset? since) =>
        _comments.Values
            .Where(c => (c.ReviewPath is not null) == review && (since is null || c.UpdatedAt >= since))
            .OrderBy(c => c.UpdatedAt).ThenBy(c => c.Id);

    private object IssueJson(Issue i) => new Dictionary<string, object?>
    {
        ["id"] = 90000L + i.Number,
        ["number"] = i.Number,
        ["title"] = i.Title,
        ["body"] = i.Body,
        ["state"] = i.State,
        ["user"] = new { login = "author" + i.Number },
        ["labels"] = i.Labels.Select(l => new { name = l }).ToArray(),
        ["milestone"] = i.Milestone is null ? null : new { title = i.Milestone },
        ["html_url"] = $"https://github.com/octocat/hello/{(i.PullRequest ? "pull" : "issues")}/{i.Number}",
        ["comments"] = _comments.Values.Count(c => c.Number == i.Number && c.ReviewPath is null),
        ["created_at"] = i.CreatedAt,
        ["updated_at"] = i.UpdatedAt,
        ["closed_at"] = i.ClosedAt,
        ["closed_by"] = i.ClosedAt is null ? null : new { login = "closer" },
        ["pull_request"] = i.PullRequest ? new { merged_at = i.MergedAt } : null,
        ["sub_issues_summary"] = i.PullRequest ? null : new { total = i.SubIssues.Count },
    };

    private static object CommentJson(Comment c) => new Dictionary<string, object?>
    {
        ["id"] = c.Id,
        ["body"] = c.Body,
        ["user"] = new { login = c.Login, type = c.Login.EndsWith("[bot]") ? "Bot" : "User" },
        ["created_at"] = c.CreatedAt,
        ["updated_at"] = c.UpdatedAt,
        ["issue_url"] = c.ReviewPath is null ? $"{BaseUrl}/repos/octocat/hello/issues/{c.Number}" : null,
        ["pull_request_url"] = c.ReviewPath is null ? null : $"{BaseUrl}/repos/octocat/hello/pulls/{c.Number}",
        ["path"] = c.ReviewPath,
        ["minimized"] = c.Minimized ? new { reason = "spam" } : null,
    };

    private sealed class Issue
    {
        public int Number { get; init; }
        public string Title { get; set; } = "";
        public string? Body { get; set; }
        public bool PullRequest { get; set; }
        public string State { get; set; } = "open";
        public string[] Labels { get; set; } = [];
        public string? Milestone { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }
        public DateTimeOffset? MergedAt { get; set; }
        public List<int> SubIssues { get; set; } = [];
    }

    private sealed class Comment
    {
        public long Id { get; init; }
        public int Number { get; init; }
        public string Login { get; init; } = "";
        public string Body { get; set; } = "";
        public string? ReviewPath { get; init; }
        public bool Minimized { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
