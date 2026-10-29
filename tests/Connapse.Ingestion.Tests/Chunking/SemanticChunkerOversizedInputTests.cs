using System.Net;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace Connapse.Ingestion.Tests.Chunking;

/// <summary>
/// CON-144: PgVectorStore.cs, as CON-142's eval ingested it (commit 950c61fa), failed to upload
/// with the default Semantic chunker. Some of its chunks are cut by the recursive fallback and
/// carry no pooled vector, so the pipeline embeds every chunk's text, and the largest is 1,843
/// nomic-embed-text tokens. Ollama refused it and the upload said "Failed to connect to Ollama".
/// </summary>
[Trait("Category", "Unit")]
public class SemanticChunkerOversizedInputTests
{
    // Ollama's default num_batch. Ollama versions before the embedding-specific default of 2048
    // ran embedding models with it, and an input over it was refused even with truncation on.
    private const int TokenLimit = 512;

    private static readonly string TestData = Path.Combine(AppContext.BaseDirectory, "Chunking", "TestData");

    [Fact]
    public async Task EmbedBatchAsync_ChunksOfTheFileThatFailedInCon142_EmbedWithEveryRequestUnderTheLimit()
    {
        string content = await File.ReadAllTextAsync(Path.Combine(TestData, "PgVectorStore.cs.txt"));
        var ollama = new FakeOllama(
            BertTokenizer.Create(Path.Combine(TestData, "nomic-embed-text-vocab.txt")), TokenLimit);
        var settings = new EmbeddingSettings
        {
            Provider = "Ollama",
            Model = "nomic-embed-text",
            BaseUrl = "http://localhost:11434",
        };
        var provider = new OllamaEmbeddingProvider(
            new HttpClient(ollama), new SnapshotOf<EmbeddingSettings>(settings), NullLogger<OllamaEmbeddingProvider>.Instance);
        var chunker = new SemanticChunker(
            provider, new TiktokenTokenCounter(), new PragmaticSentenceSegmenter(), new RecursiveChunker(new TiktokenTokenCounter()));
        IReadOnlyList<ChunkInfo> chunks = await chunker.ChunkAsync(
            new ParsedDocument(content, [], []), new ChunkingSettings());
        chunks.Should().Contain(c => c.PrecomputedEmbedding == null, "this is what sends the chunk text itself to the model");

        // What IngestionPipeline.EmbedChunksAsync sends when a chunk has no pooled vector.
        IReadOnlyList<float[]> vectors = await provider.EmbedBatchAsync(
            chunks.Select(c => c.Content), EmbeddingInputType.Document);

        vectors.Should().HaveCount(chunks.Count);
        ollama.Rejections.Should().BeGreaterThan(0, "the file must still contain a chunk too long for the model");
        ollama.LongestAccepted.Should().BeLessThanOrEqualTo(TokenLimit);
    }

    /// <summary>
    /// Refuses, as Ollama does, any request holding an input over the limit, counting tokens with
    /// nomic-embed-text's own WordPiece vocabulary (bert-base-uncased's; vocab.txt from
    /// huggingface.co/nomic-ai/nomic-embed-text-v1.5).
    /// </summary>
    private sealed class FakeOllama(BertTokenizer tokenizer, int tokenLimit) : HttpMessageHandler
    {
        public int Rejections { get; private set; }
        public int LongestAccepted { get; private set; }

        /// <summary>
        /// Refuses the whole request with Ollama's 400 if any input is over the token limit, and
        /// otherwise returns one unit vector per input and records the longest input accepted.
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            List<int> lengths = body.RootElement.GetProperty("input").EnumerateArray()
                .Select(e => tokenizer.CountTokens(e.GetString()!)).ToList();

            if (lengths.Max() > tokenLimit)
            {
                Rejections++;
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"the input length exceeds the context length"}""", Encoding.UTF8, "application/json"),
                };
            }

            LongestAccepted = Math.Max(LongestAccepted, lengths.Max());
            // Any unit vector will do; this one varies with the text so the chunker still finds boundaries.
            string json = JsonSerializer.Serialize(new
            {
                embeddings = lengths.Select(n => new[] { (float)Math.Cos(n), (float)Math.Sin(n) }),
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SnapshotOf<T>(T value) : IOptionsSnapshot<T> where T : class
    {
        public T Value { get; } = value;
        public T Get(string? name) => Value;
    }
}
