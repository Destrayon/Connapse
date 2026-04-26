using Connapse.Core.Interfaces;
using Microsoft.ML.Tokenizers;

namespace Connapse.Ingestion.Utilities;

/// <summary>
/// Real tiktoken-based token counter. Defaults to cl100k_base (matches
/// every OpenAI-compatible embedding model deployed in Connapse today).
/// </summary>
public class TiktokenTokenCounter : ITokenCounter
{
    private readonly Tokenizer _tokenizer;

    public TiktokenTokenCounter(string encodingName = "cl100k_base")
    {
        _tokenizer = TiktokenTokenizer.CreateForEncoding(encodingName);
    }

    public int CountTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return _tokenizer.CountTokens(text);
    }

    public int GetIndexAtTokenCount(string text, int tokenCount)
    {
        if (string.IsNullOrWhiteSpace(text) || tokenCount <= 0) return 0;
        return _tokenizer.GetIndexByTokenCount(text, tokenCount, out _, out _);
    }
}
