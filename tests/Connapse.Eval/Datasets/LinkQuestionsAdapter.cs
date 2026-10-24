using System.Globalization;
using System.Text.Json;
using Connapse.Core;
using Connapse.Eval.Model;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Connectors.GitHub;

namespace Connapse.Eval.Datasets;

/// <summary>
/// Questions whose answer is a record linked to the one the question describes (CON-30): the pull
/// request that closed an issue, a ticket's parent, its duplicates. records.jsonl is a snapshot of
/// GitHub issues and pull requests or Jira issues as their APIs return them, rendered here by the
/// connectors' own renderers so the indexed text is what a synced source would hold.
/// questions.jsonl carries each question's kind, hop count, split and the API's answer records.
/// Built by eval/tools/build_link_suite.py.
/// </summary>
public sealed class LinkQuestionsAdapter : IDatasetAdapter
{
    public const string RecordsFile = "records.jsonl";
    public const string QuestionsFile = "questions.jsonl";

    public string Name => "link-questions";

    private sealed record QuestionLine(
        string Id, string Question, string Kind, int Hops, string Split, string Start, IReadOnlyList<string> Answers);

    public async Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        List<EvalDocument> corpus = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string line in await File.ReadAllLinesAsync(Path.Combine(directory, RecordsFile), ct))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using JsonDocument row = JsonDocument.Parse(line);
            EvalDocument doc = Render(row.RootElement, $"{datasetName}/{RecordsFile} line {lineNumber}");
            if (!ids.Add(doc.Id))
                throw new InvalidDataException($"{datasetName}/{RecordsFile} repeats record '{doc.Id}'.");
            corpus.Add(doc);
        }

        List<EvalQuery> queries = [];
        Qrels qrels = new();
        lineNumber = 0;
        foreach (string line in await File.ReadAllLinesAsync(Path.Combine(directory, QuestionsFile), ct))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string where = $"{datasetName}/{QuestionsFile} line {lineNumber}";
            QuestionLine q = JsonSerializer.Deserialize<QuestionLine>(line, EvalJson.Options)
                ?? throw new InvalidDataException($"{where} is empty.");
            if (string.IsNullOrWhiteSpace(q.Id) || string.IsNullOrWhiteSpace(q.Question) || string.IsNullOrWhiteSpace(q.Kind))
                throw new InvalidDataException($"{where} needs an id, a question and a kind.");
            if (q.Hops is < 1 or > 2)
                throw new InvalidDataException($"{where} ({q.Id}) has {q.Hops} hops; link questions are one or two links away.");
            Split split = q.Split switch
            {
                "dev" => Split.Dev,
                "test" => Split.Test,
                _ => throw new InvalidDataException($"{where} ({q.Id}) has split '{q.Split}'; expected dev or test."),
            };
            if (!ids.Contains(q.Start))
                throw new InvalidDataException($"{where} ({q.Id}) starts at '{q.Start}', which is not a record in the snapshot.");
            if (q.Answers is not { Count: > 0 })
                throw new InvalidDataException($"{where} ({q.Id}) has no answers.");
            if (qrels.QueryIds.Contains(q.Id))
                throw new InvalidDataException($"{where} repeats question '{q.Id}'.");
            foreach (string answer in q.Answers)
            {
                if (!ids.Contains(answer))
                    throw new InvalidDataException($"{where} ({q.Id}) answers '{answer}', which is not a record in the snapshot.");
                if (answer == q.Start)
                    throw new InvalidDataException($"{where} ({q.Id}) answers with its own start record.");
                qrels.Add(q.Id, answer, 1);
            }
            queries.Add(new EvalQuery(q.Id, q.Question, split,
                [$"kind:{q.Kind}", $"hops:{q.Hops.ToString(CultureInfo.InvariantCulture)}"]));
        }

        return new EvalDataset(datasetName, entry.Version, entry.Tags, corpus, queries, qrels);
    }

    internal static EvalDocument Render(JsonElement row, string where)
    {
        string id = row.GetProperty("id").GetString() ?? throw new InvalidDataException($"{where} has no id.");
        JsonElement issue = row.GetProperty("issue");
        return issue.TryGetProperty("fields", out _) ? RenderJira(id, issue) : RenderGitHub(id, issue, row);
    }

    private static EvalDocument RenderGitHub(string id, JsonElement issueJson, JsonElement row)
    {
        GitHubIssue issue = issueJson.Deserialize<GitHubIssue>(GitHubApiClient.Json)
            ?? throw new InvalidDataException($"Record '{id}' has no issue.");
        GitHubStoredRecord record = new()
        {
            Number = issue.Number,
            Issue = issue,
            Parent = row.TryGetProperty("parent", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null,
            Children = row.TryGetProperty("children", out JsonElement c) ? [.. c.EnumerateArray().Select(x => x.GetInt32())] : [],
        };
        foreach (JsonElement entry in row.GetProperty("comments").EnumerateArray())
        {
            bool review = entry.GetProperty("review").GetBoolean();
            GitHubComment comment = entry.GetProperty("comment").Deserialize<GitHubComment>(GitHubApiClient.Json)
                ?? throw new InvalidDataException($"Record '{id}' has an empty comment.");
            record.Comments[(review ? "r" : "c") + comment.Id.ToString(CultureInfo.InvariantCulture)] =
                GitHubRecordSource.ToStored(comment, review);
        }

        // https://github.com/{owner}/{repo}/issues/{n}: the record names its repository as the connector would.
        string[] path = new Uri(issue.HtmlUrl).AbsolutePath.Trim('/').Split('/');
        GitHubRenderedRecord rendered = GitHubRecordRenderer.Render(record, path[0], path[1]);
        return new EvalDocument(id, DocumentKind.Text, null, rendered.Markdown, null, rendered.Metadata)
        {
            FileName = Path.GetFileName(rendered.Path),
            Strategy = nameof(ChunkingStrategy.Record),
        };
    }

    private static EvalDocument RenderJira(string id, JsonElement issue)
    {
        JsonElement fields = issue.GetProperty("fields");
        List<JiraComment> comments = [];
        if (fields.TryGetProperty("comment", out JsonElement comment)
            && comment.TryGetProperty("comments", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in list.EnumerateArray())
            {
                string author = c.TryGetProperty("author", out JsonElement a) && a.TryGetProperty("displayName", out JsonElement n)
                    ? n.GetString() ?? "Unknown user" : "Unknown user";
                DateTimeOffset created = JiraRecordRenderer.ParseTime(c.TryGetProperty("created", out JsonElement t) ? t.GetString() : null)
                    ?? DateTimeOffset.MinValue;
                comments.Add(new JiraComment(author, created, c.TryGetProperty("body", out JsonElement b) ? b.Clone() : null));
            }
        }

        // The project's display name is not in a search answer; the key's prefix in title case stands in (FLINK → Flink).
        string prefix = id[..id.IndexOf('-')];
        string projectName = prefix[..1] + prefix[1..].ToLowerInvariant();
        string markdown = JiraRecordRenderer.RenderIssue(projectName, issue, comments, [], new Dictionary<string, string>());
        string issueId = issue.GetProperty("id").GetString() ?? id;
        return new EvalDocument(id, DocumentKind.Text, null, markdown, null, new Dictionary<string, string> { ["jira:issueKey"] = id })
        {
            FileName = issueId + ".md",
            Strategy = nameof(ChunkingStrategy.Record),
        };
    }
}
