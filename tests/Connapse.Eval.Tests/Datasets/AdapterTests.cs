using Connapse.Eval.Datasets;
using Connapse.Eval.Model;
using FluentAssertions;
using Parquet.Serialization;

namespace Connapse.Eval.Tests.Datasets;

[Trait("Category", "Unit")]
public class AdapterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eval-adapters-" + Guid.NewGuid().ToString("N"));

    public AdapterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DatasetEntry Entry(string adapter) => new(adapter, "rev1", ["domain:test"], []);

    [Fact]
    public async Task BeirParquet_NoScoreColumn_GradesOneAndKeepsOnlyJudgedQueries()
    {
        await ParquetSerializer.SerializeAsync(
            new[] { new BeirCorpusRow { Id = "d1", Text = "alpha" }, new BeirCorpusRow { Id = "d2", Text = "beta" } },
            Path.Combine(_dir, "corpus.parquet"));
        await ParquetSerializer.SerializeAsync(
            new[] { new BeirQueryRow { Id = "q1", Text = "find alpha" }, new BeirQueryRow { Id = "q9", Text = "unjudged" } },
            Path.Combine(_dir, "queries.parquet"));
        await ParquetSerializer.SerializeAsync(
            new[] { new BeirQrelRowNoScore { QueryId = "q1", CorpusId = "d1" } },
            Path.Combine(_dir, "qrels.parquet"));

        EvalDataset dataset = await DatasetAdapters.Get("beir-parquet")
            .LoadAsync("nano-test", Entry("beir-parquet"), _dir, CancellationToken.None);

        dataset.Name.Should().Be("nano-test");
        dataset.Tags.Should().Equal("domain:test");
        dataset.Corpus.Select(d => d.Id).Should().Equal("d1", "d2");
        dataset.Queries.Should().ContainSingle().Which.Should().Match<EvalQuery>(q => q.Id == "q1" && q.Split == Split.Test);
        dataset.Qrels.For("q1")["d1"].Should().Be(1);
    }

    [Fact]
    public async Task BeirJsonl_TsvWithHeader_ReadsTitlesAndGrades()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "corpus.jsonl"),
            "{\"_id\":\"d1\",\"title\":\"Title one\",\"text\":\"body one\"}\n{\"_id\":\"d2\",\"title\":\"\",\"text\":\"body two\"}\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, "queries.jsonl"),
            "{\"_id\":\"q1\",\"text\":\"question one\"}\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, "qrels.tsv"), "query-id\tcorpus-id\tscore\nq1\td2\t1\n");

        EvalDataset dataset = await DatasetAdapters.Get("beir-jsonl")
            .LoadAsync("cqa-test", Entry("beir-jsonl"), _dir, CancellationToken.None);

        dataset.Corpus[0].Title.Should().Be("Title one");
        dataset.Corpus[1].Title.Should().BeNull();
        dataset.Queries.Should().ContainSingle(q => q.Id == "q1" && q.Text == "question one");
        dataset.Qrels.For("q1")["d2"].Should().Be(1);
    }

    [Fact]
    public async Task RagBench_Splits_MapValidationToDevTestToTestAndKeepTrainDocsAsDistractors()
    {
        await ParquetSerializer.SerializeAsync(
            new[] { new RagBenchRow { Id = "t1", Question = "train q", Documents = ["train doc"], AllRelevantSentenceKeys = ["0a"] } },
            Path.Combine(_dir, "train.parquet"));
        await ParquetSerializer.SerializeAsync(
            new[] { new RagBenchRow { Id = "v1", Question = "dev q", Documents = ["doc A", "doc B"], AllRelevantSentenceKeys = ["1c"] } },
            Path.Combine(_dir, "validation.parquet"));
        await ParquetSerializer.SerializeAsync(
            new[]
            {
                new RagBenchRow { Id = "s1", Question = "test q", Documents = ["doc A", "doc C"], AllRelevantSentenceKeys = ["0b", "1a", "1b"] },
                new RagBenchRow { Id = "s2", Question = "no answer", Documents = ["doc C"], AllRelevantSentenceKeys = [] },
            },
            Path.Combine(_dir, "test.parquet"));

        EvalDataset dataset = await DatasetAdapters.Get("ragbench")
            .LoadAsync("rb-test", Entry("ragbench"), _dir, CancellationToken.None);

        dataset.Corpus.Should().HaveCount(4, "train doc, doc A, doc B and doc C, each once");
        dataset.Queries.Select(q => (q.Id, q.Split)).Should().Equal(("v1", Split.Dev), ("s1", Split.Test), ("s2", Split.Test));
        string docA = RagBenchAdapter.DocId("doc A");
        string docB = RagBenchAdapter.DocId("doc B");
        string docC = RagBenchAdapter.DocId("doc C");
        dataset.Qrels.For("v1").Keys.Should().Equal(docB);
        dataset.Qrels.For("s1").Keys.Should().BeEquivalentTo([docA, docC]);
        dataset.Qrels.For("s2").Should().BeEmpty();
    }

    [Fact]
    public async Task RagBench_DuplicateIdSameSplitSameQuestion_MergesIntoOneQueryWithUnionQrels()
    {
        await ParquetSerializer.SerializeAsync(Array.Empty<RagBenchRow>(), Path.Combine(_dir, "train.parquet"));
        await ParquetSerializer.SerializeAsync(Array.Empty<RagBenchRow>(), Path.Combine(_dir, "validation.parquet"));
        await ParquetSerializer.SerializeAsync(
            new[]
            {
                new RagBenchRow { Id = "s1", Question = "test q", Documents = ["doc A", "doc B"], AllRelevantSentenceKeys = ["0a"] },
                new RagBenchRow { Id = "s1", Question = "test q", Documents = ["doc A", "doc B"], AllRelevantSentenceKeys = ["1b"] },
            },
            Path.Combine(_dir, "test.parquet"));

        EvalDataset dataset = await DatasetAdapters.Get("ragbench")
            .LoadAsync("rb-dup-test", Entry("ragbench"), _dir, CancellationToken.None);

        dataset.Queries.Should().ContainSingle(q => q.Id == "s1" && q.Split == Split.Test);
        string docA = RagBenchAdapter.DocId("doc A");
        string docB = RagBenchAdapter.DocId("doc B");
        dataset.Qrels.For("s1").Keys.Should().BeEquivalentTo([docA, docB]);
    }

    [Fact]
    public async Task RagBench_DuplicateIdDifferentQuestionText_Throws()
    {
        await ParquetSerializer.SerializeAsync(Array.Empty<RagBenchRow>(), Path.Combine(_dir, "train.parquet"));
        await ParquetSerializer.SerializeAsync(Array.Empty<RagBenchRow>(), Path.Combine(_dir, "validation.parquet"));
        await ParquetSerializer.SerializeAsync(
            new[]
            {
                new RagBenchRow { Id = "s1", Question = "test q", Documents = ["doc A"], AllRelevantSentenceKeys = ["0a"] },
                new RagBenchRow { Id = "s1", Question = "different q", Documents = ["doc A"], AllRelevantSentenceKeys = ["0a"] },
            },
            Path.Combine(_dir, "test.parquet"));

        Func<Task> act = () => DatasetAdapters.Get("ragbench")
            .LoadAsync("rb-dup-question", Entry("ragbench"), _dir, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*s1*");
    }

    [Fact]
    public async Task RagBench_SameIdInValidationAndTest_Throws()
    {
        await ParquetSerializer.SerializeAsync(Array.Empty<RagBenchRow>(), Path.Combine(_dir, "train.parquet"));
        await ParquetSerializer.SerializeAsync(
            new[] { new RagBenchRow { Id = "s1", Question = "test q", Documents = ["doc A"], AllRelevantSentenceKeys = ["0a"] } },
            Path.Combine(_dir, "validation.parquet"));
        await ParquetSerializer.SerializeAsync(
            new[] { new RagBenchRow { Id = "s1", Question = "test q", Documents = ["doc A"], AllRelevantSentenceKeys = ["0a"] } },
            Path.Combine(_dir, "test.parquet"));

        Func<Task> act = () => DatasetAdapters.Get("ragbench")
            .LoadAsync("rb-dup-split", Entry("ragbench"), _dir, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*s1*");
    }

    [Fact]
    public async Task BeirJsonl_DuplicateCorpusId_Throws()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "corpus.jsonl"),
            "{\"_id\":\"d1\",\"title\":\"Title one\",\"text\":\"body one\"}\n{\"_id\":\"d1\",\"title\":\"\",\"text\":\"body two\"}\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, "queries.jsonl"),
            "{\"_id\":\"q1\",\"text\":\"question one\"}\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, "qrels.tsv"), "query-id\tcorpus-id\tscore\nq1\td1\t1\n");

        Func<Task> act = () => DatasetAdapters.Get("beir-jsonl")
            .LoadAsync("cqa-dup-corpus", Entry("beir-jsonl"), _dir, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*d1*");
    }

    [Theory]
    [InlineData("query-id\tcorpus-id\tscore\nq1\td1\t1\nq1\td2\n", "*qrels.tsv*line 3*")]
    [InlineData("query-id\tcorpus-id\tscore\nq1\td1\tone\n", "*qrels.tsv*line 2*")]
    [InlineData("query-id\tcorpus-id\tscore\nq1\td1\t1\nq1\td1\t2\n", "*qrels.tsv*line 3*q1*d1*")]
    public async Task BeirJsonl_MalformedOrDuplicateQrelsLine_ThrowsNamingFileAndLine(string qrels, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "corpus.jsonl"),
            "{\"_id\":\"d1\",\"text\":\"body one\"}\n{\"_id\":\"d2\",\"text\":\"body two\"}\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, "queries.jsonl"), "{\"_id\":\"q1\",\"text\":\"question one\"}\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, "qrels.tsv"), qrels);

        Func<Task> act = () => DatasetAdapters.Get("beir-jsonl")
            .LoadAsync("cqa-bad-qrels", Entry("beir-jsonl"), _dir, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage(message);
    }

    private static DatasetEntry PdfQaEntry() => new("pdf-qa", "rev1", [],
        [new DatasetFile("a.pdf", "https://x.test/a.pdf", null), new DatasetFile("questions.jsonl", "repo:pdfqa/questions.jsonl", null)]);

    [Fact]
    public async Task PdfQa_Questions_LoadPdfsAsFilesAndQuestionsWithPassageGold()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "questions.jsonl"),
            """{"id":"t1","question":"How many in Ohio?","doc":"a.pdf","evidence":["Ohio","4,213"],"answer":"4,213","page":3,"kind":"table-ruled"}""" + "\n");

        EvalDataset dataset = await DatasetAdapters.Get("pdf-qa").LoadAsync("pdfqa", PdfQaEntry(), _dir, CancellationToken.None);

        dataset.Corpus.Should().ContainSingle().Which.Should().Match<EvalDocument>(d =>
            d.Id == "a.pdf" && d.Kind == DocumentKind.File && d.FilePath == Path.Combine(_dir, "a.pdf"));
        dataset.Queries.Should().ContainSingle().Which.Text.Should().Be("How many in Ohio?");
        dataset.Passages!["t1"].Should().BeEquivalentTo(new PassageGold("a.pdf", ["Ohio", "4,213"], "4,213", PdfQaAdapter.Kinds["table-ruled"]));
        dataset.Qrels.For("t1").Should().ContainKey("a.pdf");
        dataset.Queries[0].Tags.Should().Equal("kind:table-ruled");
        dataset.Passages["t1"].MaxGapWords.Should().Be(PdfQaAdapter.Kinds["table-ruled"]);
    }

    [Theory]
    [InlineData("""{"id":"t1","question":"q","doc":"b.pdf","evidence":["x"],"answer":"x","kind":"columns"}""", "*b.pdf*")]
    [InlineData("""{"id":"t1","question":"q","doc":"a.pdf","evidence":["|"],"answer":"x","kind":"columns"}""", "*evidence*")]
    [InlineData("""{"id":"t1","question":"q","doc":"a.pdf","evidence":["x"],"answer":"x","kind":"columns"}""" + "\n"
        + """{"id":"t1","question":"q","doc":"a.pdf","evidence":["x"],"answer":"x","kind":"columns"}""", "*line 2*repeats*")]
    [InlineData("""{"id":"t1","question":"q","doc":"a.pdf","evidence":["x"],"answer":"x"}""", "*needs a kind*")]
    [InlineData("""{"id":"t1","question":"q","doc":"a.pdf","evidence":["x"],"answer":"x","kind":"tables"}""", "*needs a kind*")]
    public async Task PdfQa_BadQuestion_ThrowsNamingTheLine(string questions, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "questions.jsonl"), questions);

        Func<Task> act = () => DatasetAdapters.Get("pdf-qa").LoadAsync("pdfqa", PdfQaEntry(), _dir, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage(message);
    }

    private async Task WriteEnterpriseAsync(EnterpriseDocumentRow[] documents, EnterpriseQuestionRow[] questions)
    {
        // Two documents per row group, so loading has to read more than one.
        await ParquetSerializer.SerializeAsync(documents, Path.Combine(_dir, "documents.parquet"),
            new Parquet.ParquetOptions { RowGroupSize = 2 });
        await ParquetSerializer.SerializeAsync(questions, Path.Combine(_dir, "questions.parquet"));
    }

    private static EnterpriseDocumentRow Doc(string id, string source) =>
        new() { DocId = id, SourceType = source, Title = $"title {id}", Content = $"content {id}" };

    [Fact]
    public async Task EnterpriseRagBench_LoadsEveryRowGroupTagsKindAndSourceAndSkipsQuestionsWithoutGold()
    {
        await WriteEnterpriseAsync(
            [Doc("d1", "slack"), Doc("d2", "jira"), Doc("d3", "gmail"), Doc("d4", "slack"), Doc("d5", "github")],
            [
                new() { QuestionId = "q1", QuestionType = "basic", SourceTypes = ["slack"], Question = "where?", ExpectedDocIds = ["d4"] },
                new() { QuestionId = "q2", QuestionType = "completeness", SourceTypes = ["jira", "gmail", "jira"], Question = "all?", ExpectedDocIds = ["d2", "d3"] },
                new() { QuestionId = "q3", QuestionType = "info_not_found", SourceTypes = [], Question = "missing?", ExpectedDocIds = [] },
            ]);

        EvalDataset dataset = await DatasetAdapters.Get("enterprise-rag-bench").LoadAsync("erb", Entry("enterprise-rag-bench"), _dir, CancellationToken.None);

        dataset.Corpus.Select(d => d.Id).Should().Equal("d1", "d2", "d3", "d4", "d5");
        dataset.Corpus[3].Should().Match<EvalDocument>(d => d.Title == "title d4" && d.Text == "content d4" && d.Metadata["source"] == "slack");
        dataset.Queries.Select(q => q.Id).Should().Equal("q1", "q2");
        dataset.Queries[1].Tags.Should().Equal("kind:completeness", "source:jira", "source:gmail");
        dataset.Qrels.For("q2").Keys.Should().BeEquivalentTo(["d2", "d3"]);
    }

    [Fact]
    public async Task EnterpriseRagBench_GoldDocumentMissingFromTheCorpus_Throws()
    {
        await WriteEnterpriseAsync(
            [Doc("d1", "slack")],
            [new() { QuestionId = "q1", QuestionType = "basic", SourceTypes = ["slack"], Question = "where?", ExpectedDocIds = ["d9"] }]);

        Func<Task> act = () => DatasetAdapters.Get("enterprise-rag-bench").LoadAsync("erb", Entry("enterprise-rag-bench"), _dir, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*1 gold documents*d9*");
    }

    [Fact]
    public void Get_UnknownAdapter_ThrowsListingKnownAdapters()
    {
        Action act = () => DatasetAdapters.Get("nope");

        act.Should().Throw<InvalidOperationException>().WithMessage("*beir-parquet*");
    }

    public sealed class BeirQrelRowNoScore
    {
        [System.Text.Json.Serialization.JsonPropertyName("query-id")] public string QueryId { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("corpus-id")] public string CorpusId { get; set; } = "";
    }
}
