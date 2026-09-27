using Connapse.Eval.Checks.OlmOcr;

namespace Connapse.Eval.Checks;

public enum ExpectedIngestion
{
    /// <summary>The file is readable: its text should be extracted and indexed.</summary>
    Extract,

    /// <summary>The file cannot be read: Connapse should reject it or mark it Failed with a reason.</summary>
    FailLoudly,
}

public sealed record DocumentExpectation(string Category, ExpectedIngestion Expected);

/// <summary>An olmOCR-style check on one document; <c>Test.Pdf</c> is the dataset document ID.</summary>
public sealed record ExtractionCheck(string Category, OlmOcrTest Test);

/// <summary>What an extract run expects of a dataset: an expectation per document, and checks on their text.</summary>
public sealed record ExtractionSpec(IReadOnlyDictionary<string, DocumentExpectation> Documents, IReadOnlyList<ExtractionCheck> Checks);
