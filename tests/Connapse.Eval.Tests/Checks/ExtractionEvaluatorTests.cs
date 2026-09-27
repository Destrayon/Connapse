using Connapse.Core;
using Connapse.Eval.Checks;
using Connapse.Eval.Checks.OlmOcr;
using Connapse.Eval.Runs;
using FluentAssertions;

namespace Connapse.Eval.Tests.Checks;

[Trait("Category", "Unit")]
public class ExtractionEvaluatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eval-extraction-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static DocumentRecord Doc(
        string? status = "Ready", string? state = "SummaryIndexed", string? error = null, string? uploadError = null,
        bool stalled = false, int? parsedChars = 100, int chunks = 2, IReadOnlyList<int>? emptyPages = null,
        ExpectedIngestion expected = ExpectedIngestion.Extract) =>
        new("ds", "doc.pdf", "cat", expected, uploadError, status, error, state, stalled, 10, parsedChars, 1, emptyPages ?? [], chunks, [], null);

    private static ExtractionCheck Check(OlmOcrTest test) => new("cat", test);

    [Fact]
    public void Evaluate_PresentTextSplitAcrossChunks_PassesParsedFailsChunks()
    {
        ExtractionCheck check = Check(new TextPresenceTest("doc.pdf", 1, "p1", "present", 0, "the quick brown fox jumps"));
        DocumentTexts texts = new("the quick brown fox jumps over", ["the quick brown", "fox jumps over"]);

        List<CheckRecord> records = ExtractionEvaluator.Evaluate(Doc(), texts, [check]).Where(r => r.CheckId == "p1").ToList();

        records.Single(r => r.Level == CheckLevel.Parsed).Outcome.Should().Be(CheckOutcome.Pass);
        records.Single(r => r.Level == CheckLevel.Chunks).Should().Match<CheckRecord>(r => r.Outcome == CheckOutcome.Fail && r.Detail.Contains("no single chunk"));
    }

    [Fact]
    public void Evaluate_AbsentTextInOneChunk_FailsNamingTheChunk()
    {
        ExtractionCheck check = Check(new TextPresenceTest("doc.pdf", 1, "a1", "absent", 0, "Journal of Examples"));
        DocumentTexts texts = new("body", ["body text", "Journal of Examples, page 3"]);

        CheckRecord chunks = ExtractionEvaluator.Evaluate(Doc(), texts, [check]).Single(r => r.CheckId == "a1" && r.Level == CheckLevel.Chunks);

        chunks.Outcome.Should().Be(CheckOutcome.Fail);
        chunks.Detail.Should().StartWith("chunk 1:");
    }

    [Fact]
    public void Evaluate_OrderAndMath_RunOnParsedTextOnly()
    {
        ExtractionCheck order = Check(new TextOrderTest("doc.pdf", 1, "o1", "order", 0, "first", "second"));
        ExtractionCheck math = Check(new MathTest("doc.pdf", 1, "m1", "math", 0, "x^2"));

        List<CheckRecord> records = ExtractionEvaluator.Evaluate(Doc(), new DocumentTexts("first then second", ["first then second"]), [order, math])
            .Where(r => r.Level != CheckLevel.Document).ToList();

        records.Should().HaveCount(2).And.OnlyContain(r => r.Level == CheckLevel.Parsed);
        records.Single(r => r.CheckId == "o1").Outcome.Should().Be(CheckOutcome.Pass);
        records.Single(r => r.CheckId == "m1").Outcome.Should().Be(CheckOutcome.Skipped);
    }

    [Fact]
    public void Evaluate_StalledDocument_FailsTextChecksWithTheReason()
    {
        ExtractionCheck check = Check(new TextPresenceTest("doc.pdf", 1, "p1", "present", 0, "anything"));

        List<CheckRecord> records = ExtractionEvaluator.Evaluate(Doc(stalled: true), new DocumentTexts("anything", ["anything"]), [check])
            .Where(r => r.CheckId == "p1").ToList();

        records.Should().OnlyContain(r => r.Outcome == CheckOutcome.Fail && r.Detail.Contains("stalled"));
    }

    [Theory]
    [InlineData(null, null, null, "Zero-byte uploads are not allowed.", false, CheckOutcome.Pass)]
    [InlineData("Failed", "Failed", "No extractable content", null, false, CheckOutcome.Pass)]
    [InlineData("Failed", "SummaryIndexed", "No extractable content", null, false, CheckOutcome.Fail)]
    [InlineData("Failed", "Failed", "", null, false, CheckOutcome.Fail)]
    [InlineData("Ready", "SummaryIndexed", null, null, false, CheckOutcome.Fail)]
    [InlineData("Processing", "Pending", null, null, true, CheckOutcome.Fail)]
    public void FailsLoudly_PassesOnlyForRejectionOrAVisibleFailureWithAReason(
        string? status, string? state, string? error, string? uploadError, bool stalled, string expected)
    {
        DocumentRecord doc = Doc(status, state, error, uploadError, stalled, expected: ExpectedIngestion.FailLoudly);

        ExtractionEvaluator.FailsLoudlyCheck(doc).Outcome.Should().Be(expected);
    }

    [Fact]
    public void Evaluate_ExtractExpectation_HasNoFailsLoudlyCheck() =>
        ExtractionEvaluator.Evaluate(Doc(), new DocumentTexts("x", ["x"]), []).Select(r => r.Type)
            .Should().Equal(ExtractionEvaluator.NoSilentFailure);

    [Theory]
    [InlineData("Ready", "SummaryIndexed", 0, 0, false, CheckOutcome.Fail)]
    [InlineData("Failed", "SummaryIndexed", 0, 0, false, CheckOutcome.Fail)]
    [InlineData("Failed", "Failed", 0, 0, false, CheckOutcome.Pass)]
    [InlineData("Ready", "SummaryIndexed", 100, 0, false, CheckOutcome.Fail)]
    [InlineData("Ready", "SummaryIndexed", 100, 3, true, CheckOutcome.Fail)]
    [InlineData("Ready", "SummaryIndexed", 100, 3, false, CheckOutcome.Pass)]
    public void NoSilentFailure_FlagsDocumentsShownAsFineWithTextMissing(
        string status, string state, int parsedChars, int chunks, bool emptyPage, string expected)
    {
        DocumentRecord doc = Doc(status, state, parsedChars: parsedChars, chunks: chunks, emptyPages: emptyPage ? [2] : []);

        ExtractionEvaluator.NoSilentFailureCheck(doc).Outcome.Should().Be(expected);
    }

    [Fact]
    public void Score_ComputesHeadlineRatesFromTheRunFolder()
    {
        RunFolder run = RunFolder.Create(_root, "extract-v1", "connapse", "extract", "abc1234", DateTimeOffset.UtcNow);
        run.WriteManifest(new RunManifest("abc1234", false, "extract-v1", "connapse", "extract", "h", SearchMode.Keyword,
            new Dictionary<string, string>(), new Dictionary<string, string>(), "m", "os", 1, DateTimeOffset.UtcNow, null,
            [new RunDatasetInfo("ds", "1", new Dictionary<string, string>(), [], 2, 0, false, 0)], null, [], RunManifest.ExtractKind));
        DocumentRecord silent = Doc(parsedChars: 0, chunks: 0) with { DocId = "a" };
        DocumentRecord loud = Doc("Failed", "Failed", "bad", expected: ExpectedIngestion.FailLoudly) with { DocId = "b" };
        run.WriteExtraction("ds", [silent, loud],
        [
            new CheckRecord("ds", "a", "cat", "a1", "present", CheckLevel.Parsed, CheckOutcome.Pass, ""),
            new CheckRecord("ds", "a", "cat", "a1", "present", CheckLevel.Chunks, CheckOutcome.Fail, ""),
            ExtractionEvaluator.NoSilentFailureCheck(silent),
            ExtractionEvaluator.NoSilentFailureCheck(loud),
            ExtractionEvaluator.FailsLoudlyCheck(loud),
        ]);
        run.MarkComplete("ds");

        ExtractionScores scores = ExtractionScoring.Score(run);

        scores.SilentFailureRate.Should().Be(0.5);
        scores.FailsLoudlyRate.Should().Be(1.0);
        scores.Datasets.Single().Categories.Select(c => (c.Level, c.Rate))
            .Should().BeEquivalentTo([(CheckLevel.Chunks, 0.0), (CheckLevel.Parsed, 1.0)]);
        run.IsExtraction.Should().BeTrue();
    }
}
