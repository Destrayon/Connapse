using Connapse.Eval.Checks.Matching;
using Connapse.Eval.Checks.OlmOcr;

namespace Connapse.Eval.Checks;

public static class CheckLevel
{
    /// <summary>The parser's output for the whole document: did extraction keep the text?</summary>
    public const string Parsed = "parsed";

    /// <summary>The stored chunks: does one chunk hold what the check needs?</summary>
    public const string Chunks = "chunks";

    /// <summary>A check on how the document ended up, not on its text.</summary>
    public const string Document = "document";
}

public static class CheckOutcome
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Skipped = "skipped";
}

/// <summary>What one document came to, as written to documents.jsonl.</summary>
public sealed record DocumentRecord(
    string Dataset,
    string DocId,
    string Category,
    ExpectedIngestion Expected,
    string? UploadError,
    string? Status,
    string? ErrorMessage,
    string? IngestionState,
    bool Stalled,
    double ElapsedMs,
    int? ParsedChars,
    int? PageCount,
    IReadOnlyList<int> EmptyPages,
    int ChunkCount,
    IReadOnlyList<string> ParserWarnings,
    string? ParseError);

/// <summary>One check at one level, as written to checks.jsonl.</summary>
public sealed record CheckRecord(
    string Dataset,
    string DocId,
    string Category,
    string CheckId,
    string Type,
    string Level,
    string Outcome,
    string Detail);

/// <summary>A document's two text views, held in memory only while its checks run.</summary>
public sealed record DocumentTexts(string? ParsedText, IReadOnlyList<string> Chunks);

/// <summary>
/// Runs a dataset's checks against one document. Text checks run on the parsed text (level
/// "parsed") and, for present, absent and table checks, on the chunks (level "chunks"): present and
/// table pass when one single chunk passes, absent passes when no chunk contains the text. Order and
/// baseline checks describe the whole text, so they run on the parsed text only.
/// </summary>
public static class ExtractionEvaluator
{
    public const string FailsLoudly = "fails-loudly";
    public const string NoSilentFailure = "no-silent-failure";

    public static IEnumerable<CheckRecord> Evaluate(DocumentRecord doc, DocumentTexts texts, IEnumerable<ExtractionCheck> checks)
    {
        foreach (ExtractionCheck check in checks)
            foreach (CheckRecord record in EvaluateText(doc, texts, check))
                yield return record;

        if (doc.Expected == ExpectedIngestion.FailLoudly)
            yield return FailsLoudlyCheck(doc);
        yield return NoSilentFailureCheck(doc);
    }

    private static IEnumerable<CheckRecord> EvaluateText(DocumentRecord doc, DocumentTexts texts, ExtractionCheck check)
    {
        OlmOcrTest test = check.Test;
        CheckRecord Record(string level, string outcome, string detail) =>
            new(doc.Dataset, doc.DocId, check.Category, test.Id, test.Type, level, outcome, detail);

        if (test is MathTest)
        {
            yield return Record(CheckLevel.Parsed, CheckOutcome.Skipped, "math checks compare rendered LaTeX and are not run");
            yield break;
        }

        bool chunkLevel = test is TextPresenceTest or TableTest;
        string? unavailable = doc.Stalled ? "document stalled during ingestion"
            : doc.UploadError is not null ? $"upload rejected: {doc.UploadError}"
            : null;

        if (unavailable is not null || texts.ParsedText is null)
        {
            yield return Record(CheckLevel.Parsed, CheckOutcome.Fail, unavailable ?? $"no parsed text: {doc.ParseError}");
        }
        else
        {
            OlmOcrOutcome parsed = test.Run(texts.ParsedText);
            yield return Record(CheckLevel.Parsed, Outcome(parsed), parsed.Explanation);
        }

        if (!chunkLevel)
            yield break;
        if (unavailable is not null)
        {
            yield return Record(CheckLevel.Chunks, CheckOutcome.Fail, unavailable);
            yield break;
        }

        bool absent = test.Type == "absent";
        if (texts.Chunks.Count == 0)
        {
            yield return Record(CheckLevel.Chunks, absent ? CheckOutcome.Pass : CheckOutcome.Fail, "document has no chunks");
            yield break;
        }

        // partial_ratio aligns the shorter string inside the longer, so a chunk that is only a fragment
        // of the reference text would score 100. A chunk shorter than the reference cannot contain it:
        // it fails a present check and passes an absent one without being scored.
        int referenceLength = test is TextPresenceTest presence ? PyText.Length(presence.Text) : 0;
        List<OlmOcrOutcome> perChunk = texts.Chunks
            .Select(chunk => PyText.Length(OlmOcrText.Normalize(chunk)!) >= referenceLength
                ? test.Run(chunk)
                : new OlmOcrOutcome(absent, "chunk is shorter than the text"))
            .ToList();
        if (absent)
        {
            int index = perChunk.FindIndex(o => !o.Passed);
            yield return index < 0
                ? Record(CheckLevel.Chunks, CheckOutcome.Pass, "")
                : Record(CheckLevel.Chunks, CheckOutcome.Fail, $"chunk {index}: {perChunk[index].Explanation}");
        }
        else
        {
            yield return perChunk.Any(o => o.Passed)
                ? Record(CheckLevel.Chunks, CheckOutcome.Pass, "")
                : Record(CheckLevel.Chunks, CheckOutcome.Fail, $"no single chunk passes ({texts.Chunks.Count} chunks); first: {perChunk[0].Explanation}");
        }
    }

    /// <summary>
    /// Passes when the upload was rejected, or the document ended with status Failed, a reason, and
    /// ingestion state Failed (what the UI badge shows), without stalling.
    /// </summary>
    public static CheckRecord FailsLoudlyCheck(DocumentRecord doc)
    {
        (string outcome, string detail) =
            doc.Stalled ? (CheckOutcome.Fail, "stalled: never reached a final state")
            : doc.UploadError is not null ? (CheckOutcome.Pass, $"rejected at upload: {doc.UploadError}")
            : doc.Status == "Failed" && !string.IsNullOrWhiteSpace(doc.ErrorMessage) && doc.IngestionState == "Failed"
                ? (CheckOutcome.Pass, $"failed: {doc.ErrorMessage}")
            : (CheckOutcome.Fail, $"status {doc.Status ?? "none"}, ingestion state {doc.IngestionState ?? "none"}, "
                + $"error message {(string.IsNullOrWhiteSpace(doc.ErrorMessage) ? "empty" : $"'{doc.ErrorMessage}'")}");
        return new CheckRecord(doc.Dataset, doc.DocId, doc.Category, $"{doc.DocId}_{FailsLoudly}", FailsLoudly,
            CheckLevel.Document, outcome, detail);
    }

    /// <summary>
    /// Fails when a document is shown as fine (not rejected, ingestion state not Failed) although its
    /// parsed text is empty, a PDF page produced no text, or nothing was stored for search.
    /// </summary>
    public static CheckRecord NoSilentFailureCheck(DocumentRecord doc)
    {
        string? problem = doc.Stalled ? "stalled: never reached a final state"
            : doc.UploadError is not null || doc.IngestionState == "Failed" ? null
            : doc.ParsedChars is null or 0 ? "shown as indexed but the parser extracted no text"
            : doc.EmptyPages.Count > 0 ? $"shown as indexed but page(s) {string.Join(", ", doc.EmptyPages)} of {doc.PageCount} produced no text"
            : doc.ChunkCount == 0 ? "shown as indexed but no chunks were stored"
            : null;
        return new CheckRecord(doc.Dataset, doc.DocId, doc.Category, $"{doc.DocId}_{NoSilentFailure}", NoSilentFailure,
            CheckLevel.Document, problem is null ? CheckOutcome.Pass : CheckOutcome.Fail, problem ?? "");
    }

    private static string Outcome(OlmOcrOutcome outcome) =>
        outcome.Skipped ? CheckOutcome.Skipped : outcome.Passed ? CheckOutcome.Pass : CheckOutcome.Fail;
}
