using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using FluentAssertions;

namespace Connapse.Eval.Tests.Datasets;

[Trait("Category", "Unit")]
public class LinkQuestionsAdapterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eval-links-" + Guid.NewGuid().ToString("N"));

    public LinkQuestionsAdapterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DatasetEntry Entry = new("link-questions", "2026-10-01", ["source:github"], []);

    private const string Issue =
        """{"id":"issues/7","issue":{"id":107,"number":7,"title":"Search misses old documents","body":"Old docs never match.","state":"closed","user":{"login":"alice","type":"User"},"labels":[{"name":"bug"}],"milestone":null,"html_url":"https://github.com/Destrayon/Connapse/issues/7","comments":1,"created_at":"2026-09-01T10:00:00Z","updated_at":"2026-09-02T10:00:00Z","closed_at":"2026-09-02T10:00:00Z","closed_by":null,"pull_request":null,"sub_issues_summary":{"total":0}},"comments":[{"review":false,"comment":{"id":901,"body":"Seen it too.","user":{"login":"bob","type":"User"},"created_at":"2026-09-01T11:00:00Z","updated_at":"2026-09-01T11:00:00Z","issue_url":"https://api.github.com/repos/Destrayon/Connapse/issues/7","pull_request_url":null,"path":null}}],"parent":3,"children":[]}""";

    private const string Pull =
        """{"id":"pulls/8","issue":{"id":108,"number":8,"title":"Score keyword candidates on both sides","body":"Fixes #7","state":"closed","user":{"login":"carol","type":"User"},"labels":[],"milestone":null,"html_url":"https://github.com/Destrayon/Connapse/pull/8","comments":0,"created_at":"2026-09-02T09:00:00Z","updated_at":"2026-09-02T10:00:00Z","closed_at":"2026-09-02T10:00:00Z","closed_by":null,"pull_request":{"merged_at":"2026-09-02T10:00:00Z"},"sub_issues_summary":{"total":0}},"comments":[],"parent":null,"children":[]}""";

    private const string Epic =
        """{"id":"issues/3","issue":{"id":103,"number":3,"title":"Search quality epic","body":null,"state":"open","user":{"login":"alice","type":"User"},"labels":[],"milestone":null,"html_url":"https://github.com/Destrayon/Connapse/issues/3","comments":0,"created_at":"2026-08-01T10:00:00Z","updated_at":"2026-09-02T10:00:00Z","closed_at":null,"closed_by":null,"pull_request":null,"sub_issues_summary":{"total":1}},"comments":[],"parent":null,"children":[7]}""";

    private const string Jira =
        """{"id":"FLINK-20","issue":{"id":"13400020","key":"FLINK-20","fields":{"summary":"Checkpoint stalls under backpressure","description":"Steps to reproduce: run the job.","issuetype":{"name":"Bug"},"status":{"name":"Resolved"},"priority":{"name":"Major"},"labels":[],"components":[{"name":"Runtime / Checkpointing"}],"fixVersions":[],"issuelinks":[{"type":{"name":"Blocker","inward":"is blocked by","outward":"blocks"},"inwardIssue":{"key":"FLINK-19","fields":{"summary":"x"}}}],"created":"2022-03-01T10:00:00.000+0000","updated":"2022-03-02T10:00:00.000+0000","comment":{"comments":[{"author":{"displayName":"Dana"},"created":"2022-03-01T12:00:00.000+0000","body":"Reproduced on 1.15."}]}}}}""";

    private const string JiraBlocker =
        """{"id":"FLINK-19","issue":{"id":"13400019","key":"FLINK-19","fields":{"summary":"Network buffers leak","description":null,"issuetype":{"name":"Bug"},"status":{"name":"Open"},"created":"2022-02-01T10:00:00.000+0000","updated":"2022-02-02T10:00:00.000+0000"}}}""";

    private async Task<EvalDataset> LoadAsync(string[] records, string[] questions)
    {
        await File.WriteAllLinesAsync(Path.Combine(_dir, LinkQuestionsAdapter.RecordsFile), records);
        await File.WriteAllLinesAsync(Path.Combine(_dir, LinkQuestionsAdapter.QuestionsFile), questions);
        return await DatasetAdapters.Get("link-questions").LoadAsync("links-test", Entry, _dir, CancellationToken.None);
    }

    private static string Question(string id = "q1", string start = "issues/7", string answers = "\"pulls/8\"", string split = "test", int hops = 1) =>
        $$"""{"id":"{{id}}","question":"Which change fixed old documents never matching?","kind":"gh-closed-by","hops":{{hops}},"split":"{{split}}","start":"{{start}}","answers":[{{answers}}]}""";

    [Fact]
    public async Task LoadAsync_GitHubRecords_RenderLikeTheConnector()
    {
        EvalDataset dataset = await LoadAsync([Issue, Pull, Epic], [Question()]);

        EvalDocument issue = dataset.Corpus.Single(d => d.Id == "issues/7");
        issue.FileName.Should().Be("7.md");
        issue.Strategy.Should().Be("Record");
        issue.Title.Should().BeNull();
        issue.Text.Should().StartWith("# Destrayon/Connapse #7: Search misses old documents")
            .And.Contain("Parent: #3")
            .And.Contain("--- Comment by bob (2026-09-01) ---");
        dataset.Corpus.Single(d => d.Id == "pulls/8").Text.Should().Contain("Closes: #7");
        dataset.Corpus.Single(d => d.Id == "issues/3").Text.Should().Contain("Sub-issues: #7");
    }

    [Fact]
    public async Task LoadAsync_JiraRecords_RenderLinksAndComments()
    {
        EvalDataset dataset = await LoadAsync([Jira, JiraBlocker],
            [Question(start: "FLINK-20", answers: "\"FLINK-19\"")]);

        EvalDocument doc = dataset.Corpus.Single(d => d.Id == "FLINK-20");
        doc.FileName.Should().Be("13400020.md");
        doc.Strategy.Should().Be("Record");
        doc.Text.Should().StartWith("# Flink > FLINK-20 Checkpoint stalls under backpressure")
            .And.Contain("is blocked by FLINK-19")
            .And.Contain("Reproduced on 1.15.");
        // The link metadata the connector hands the pipeline at read time (CON-34).
        doc.Metadata["jira:site"].Should().Be("apache");
        doc.Metadata["jira:links"].Should().Be("in|FLINK-19|blocker");
    }

    private const string Page =
        """{"id":"pages/200","space":"FLINK","page":{"id":"200","type":"page","status":"current","title":"FLIP-9 Async Sinks","ancestors":[{"id":"100","title":"Flink Improvement Proposals"}],"metadata":{"labels":{"results":[{"name":"accepted"}]}},"body":{"storage":{"value":"<p>Design for async sinks.</p><ac:structured-macro ac:name=\"jira\"><ac:parameter ac:name=\"key\">FLINK-20</ac:parameter></ac:structured-macro>"}}}}""";

    private const string ParentPage =
        """{"id":"pages/100","space":"FLINK","page":{"id":"100","type":"page","status":"current","title":"Flink Improvement Proposals","ancestors":[],"body":{"storage":{"value":"<p>All FLIPs.</p>"}}}}""";

    [Fact]
    public async Task LoadAsync_ConfluencePages_RenderLikeTheConnectorWithTheirLinks()
    {
        EvalDataset dataset = await LoadAsync([Jira, JiraBlocker, Page, ParentPage],
            [Question(start: "pages/200", answers: "\"pages/100\"")]);

        EvalDocument doc = dataset.Corpus.Single(d => d.Id == "pages/200");
        doc.FileName.Should().Be("200.md");
        doc.Strategy.Should().BeNull();
        doc.Text.Should().Contain("FLINK > Flink Improvement Proposals > FLIP-9 Async Sinks")
            .And.Contain("Design for async sinks.");
        doc.Metadata["confluence:site"].Should().Be("apache");
        doc.Metadata["confluence:parent"].Should().Be("100");
        doc.Metadata["confluence:labels"].Should().Be("accepted");
        doc.Metadata["confluence:issues"].Should().Be("FLINK-20");
        dataset.Corpus.Single(d => d.Id == "pages/100").Metadata["confluence:parent"].Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_Questions_CarryKindHopsSplitAndApiAnswers()
    {
        EvalDataset dataset = await LoadAsync([Issue, Pull, Epic],
            [Question(), Question(id: "q2", start: "issues/7", answers: "\"pulls/8\",\"issues/3\"", split: "dev", hops: 2)]);

        dataset.Queries.Single(q => q.Id == "q1").Should().Match<EvalQuery>(q =>
            q.Split == Split.Test && q.Tags.Contains("kind:gh-closed-by") && q.Tags.Contains("hops:1"));
        dataset.Queries.Single(q => q.Id == "q2").Should().Match<EvalQuery>(q => q.Split == Split.Dev && q.Tags.Contains("hops:2"));
        dataset.Qrels.For("q2").Keys.Should().BeEquivalentTo("pulls/8", "issues/3");
    }

    [Theory]
    [InlineData("issues/99", "\"pulls/8\"", "test", 1, "not a record")]
    [InlineData("issues/7", "\"pulls/99\"", "test", 1, "not a record")]
    [InlineData("issues/7", "\"issues/7\"", "test", 1, "its own start")]
    [InlineData("issues/7", "", "test", 1, "no answers")]
    [InlineData("issues/7", "\"pulls/8\"", "train", 1, "split 'train'")]
    [InlineData("issues/7", "\"pulls/8\"", "test", 3, "3 hops")]
    public async Task LoadAsync_InvalidQuestion_FailsLoudly(string start, string answers, string split, int hops, string message)
    {
        Func<Task> load = () => LoadAsync([Issue, Pull, Epic], [Question(start: start, answers: answers, split: split, hops: hops)]);

        (await load.Should().ThrowAsync<InvalidDataException>()).WithMessage($"*{message}*");
    }

    [Fact]
    public async Task LoadAsync_RepeatedRecord_FailsLoudly()
    {
        Func<Task> load = () => LoadAsync([Issue, Issue], [Question(answers: "\"issues/7\"")]);

        (await load.Should().ThrowAsync<InvalidDataException>()).WithMessage("*repeats record 'issues/7'*");
    }
}
