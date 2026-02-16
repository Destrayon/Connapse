using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Search.Reranking;

/// <summary>
/// Cross-encoder reranker that uses an LLM to score (query, chunk) pairs.
/// More accurate than RRF but slower and more expensive.
/// </summary>
public class CrossEncoderReranker : ISearchReranker
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CrossEncoderReranker> _logger;
    private readonly SearchSettings _searchSettings;
    private readonly LlmSettings _llmSettings;

    public string Name => "CrossEncoder";

    public CrossEncoderReranker(
        HttpClient httpClient,
        IOptionsMonitor<SearchSettings> searchSettings,
        IOptionsMonitor<LlmSettings> llmSettings,
        ILogger<CrossEncoderReranker> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _searchSettings = searchSettings.CurrentValue;
        _llmSettings = llmSettings.CurrentValue;
    }

    /// <summary>
    /// Reranks hits by scoring each (query, chunk) pair using an LLM.
    /// Sends a prompt asking the LLM to rate relevance on a 0-10 scale.
    /// </summary>
    public async Task<List<SearchHit>> RerankAsync(
        string query,
        List<SearchHit> hits,
        CancellationToken cancellationToken = default)
    {
        if (hits.Count == 0)
        {
            return hits;
        }

        // Check if cross-encoder model is configured
        if (string.IsNullOrEmpty(_searchSettings.CrossEncoderModel))
        {
            _logger.LogWarning(
                "CrossEncoderModel not configured, falling back to original scores");
            return hits;
        }

        _logger.LogInformation(
            "Cross-encoder reranking {Count} hits using model '{Model}'",
            hits.Count,
            _searchSettings.CrossEncoderModel);

        var scoredHits = new List<(SearchHit hit, float score)>();

        // Score each hit
        foreach (var hit in hits)
        {
            try
            {
                var score = await ScoreRelevanceAsync(query, hit.Content, cancellationToken);
                scoredHits.Add((hit, score));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to score chunk {ChunkId}, using original score",
                    hit.ChunkId);
                scoredHits.Add((hit, hit.Score));
            }
        }

        // Normalize scores to 0-1 range
        var maxScore = scoredHits.Max(s => s.score);
        var minScore = scoredHits.Min(s => s.score);
        var scoreRange = maxScore - minScore;

        var rerankedHits = scoredHits
            .Select(s =>
            {
                var normalizedScore = scoreRange > 0
                    ? (s.score - minScore) / scoreRange
                    : 1.0f;

                return s.hit with
                {
                    Score = normalizedScore,
                    Metadata = new Dictionary<string, string>(s.hit.Metadata)
                    {
                        ["crossEncoderScore"] = s.score.ToString("F4"),
                        ["reranker"] = "CrossEncoder"
                    }
                };
            })
            .OrderByDescending(h => h.Score)
            .ToList();

        _logger.LogInformation(
            "Cross-encoder reranking complete, score range: [{Min:F2}, {Max:F2}]",
            minScore,
            maxScore);

        return rerankedHits;
    }

    /// <summary>
    /// Scores the relevance of a chunk to a query using an LLM.
    /// Returns a score between 0 and 10.
    /// </summary>
    private async Task<float> ScoreRelevanceAsync(
        string query,
        string chunk,
        CancellationToken ct)
    {
        var prompt = $@"Rate how relevant the following text is to answering the query, on a scale from 0 to 10.
Only respond with a single number.

Query: {query}

Text: {chunk}

Relevance score (0-10):";

        var requestBody = new
        {
            model = _searchSettings.CrossEncoderModel ?? _llmSettings.Model,
            prompt,
            stream = false,
            options = new
            {
                temperature = 0.1, // Low temperature for consistent scoring
                num_predict = 10   // Short response
            }
        };

        var baseUrl = _llmSettings.BaseUrl ?? "http://localhost:11434";
        var url = $"{baseUrl.TrimEnd('/')}/api/generate";

        var response = await _httpClient.PostAsJsonAsync(url, requestBody, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);

        if (result?.Response == null)
        {
            _logger.LogWarning("Empty response from LLM");
            return 5.0f; // Neutral score
        }

        // Extract numeric score from response
        var scoreText = result.Response.Trim();
        if (float.TryParse(scoreText, out var score))
        {
            return Math.Clamp(score, 0f, 10f);
        }

        // Try to extract first number from response if parsing failed
        var numbers = new string(scoreText.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (float.TryParse(numbers, out score))
        {
            return Math.Clamp(score, 0f, 10f);
        }

        _logger.LogWarning(
            "Failed to parse score from LLM response: '{Response}'",
            scoreText);
        return 5.0f; // Neutral score
    }

    private record OllamaGenerateResponse(
        string? Model,
        string? Response,
        bool Done);
}
