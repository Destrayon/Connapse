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
