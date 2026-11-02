using System.Net;
using System.Text;
using System.Text.Json;
using Connapse.Core.Tests.Utilities;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Vectors;

[Trait("Category", "Unit")]
public class OllamaEmbeddingProviderTests
{
    private const string TooLong = """{"error":"the input length exceeds the context length"}""";

    private readonly ILogger<OllamaEmbeddingProvider> _logger = Substitute.For<ILogger<OllamaEmbeddingProvider>>();

    private OllamaEmbeddingProvider CreateProvider(HttpMessageHandler handler, string model = "all-minilm-cased", int batchSize = 32)
    {
        var settings = new EmbeddingSettings
        {
            Provider = "Ollama",
            Model = model,
            BaseUrl = "http://localhost:11434",
            BatchSize = batchSize,
        };
        return new OllamaEmbeddingProvider(new HttpClient(handler), new TestOptionsSnapshot<EmbeddingSettings>(settings), _logger);
    }

    [Fact]
    public async Task EmbedBatchAsync_AllTextsFit_SendsOneRequestAndKeepsOrder()
    {
        var ollama = new FakeOllama(maxInputLength: 100);
        var provider = CreateProvider(ollama);

        IReadOnlyList<float[]> vectors = await provider.EmbedBatchAsync(["one", "three"], EmbeddingInputType.Unspecified);

        ollama.Requests.Should().Be(1);
        vectors.Select(v => v[0]).Should().Equal(3f, 5f);
    }

    [Fact]
    public async Task EmbedBatchAsync_TextLongerThanTheModelAccepts_IsEmbeddedWholeNotTruncatedByOllama()
    {
        var ollama = new FakeOllama(maxInputLength: 40);
        string text = string.Join(' ', Enumerable.Repeat("word", 20));

        await CreateProvider(ollama).EmbedBatchAsync([text], EmbeddingInputType.Unspecified);

        string.Concat(ollama.Accepted).Should().Be(text);
    }

    [Fact]
    public async Task EmbedBatchAsync_TextsFillTheLastSubBatchExactly_SendsNoEmptyRequest()
    {
        var ollama = new FakeOllama(maxInputLength: 100);

        IReadOnlyList<float[]> vectors = await CreateProvider(ollama, batchSize: 2)
            .EmbedBatchAsync(["a", "b", "c", "d"], EmbeddingInputType.Unspecified);

        vectors.Should().HaveCount(4);
        ollama.Requests.Should().Be(2);
    }

    [Fact]
    public async Task EmbedBatchAsync_SeveralSubBatches_LogsProgressOfEach()
    {
        await CreateProvider(new FakeOllama(maxInputLength: 100), batchSize: 2)
            .EmbedBatchAsync(["a", "b", "c"], EmbeddingInputType.Unspecified);

        _logger.Received(1).Log(
            LogLevel.Debug, Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString() == "Embedded batch 1/2 (2 texts)"),
            null, Arg.Any<Func<object, Exception?, string>>());
        _logger.Received(1).Log(
            LogLevel.Debug, Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString() == "Embedded batch 2/2 (1 texts)"),
            null, Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task EmbedAsync_OneText_ReturnsItsVector()
    {
        float[] vector = await CreateProvider(new FakeOllama(maxInputLength: 100)).EmbedAsync("four", EmbeddingInputType.Unspecified);

        vector.Should().Equal(FakeOllama.VectorFor("four"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmbedAsync_BlankText_ThrowsWithoutCallingOllama(string? text)
    {
        var ollama = new FakeOllama(maxInputLength: 100);

        Func<Task> act = () => CreateProvider(ollama).EmbedAsync(text!, EmbeddingInputType.Unspecified);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().StartWith("Text cannot be empty");
        ollama.Requests.Should().Be(0);
    }

    [Fact]
    public async Task EmbedBatchAsync_OneTextTooLong_SplitsOnlyThatTextIntoPiecesThatFit()
    {
        var ollama = new FakeOllama(maxInputLength: 100);
        var provider = CreateProvider(ollama);
        string longText = string.Join(" ", Enumerable.Repeat("word", 80)); // 399 chars

        IReadOnlyList<float[]> vectors = await provider.EmbedBatchAsync(["short", longText, "tail"], EmbeddingInputType.Unspecified);

        vectors.Should().HaveCount(3);
        vectors[0].Should().Equal(FakeOllama.VectorFor("short"));
        vectors[2].Should().Equal(FakeOllama.VectorFor("tail"));
        ollama.Accepted.Should().OnlyContain(text => text.Length <= 100);
        ollama.Accepted.Should().Contain(["short", "tail"]);
        string.Concat(ollama.Accepted.Where(t => t is not ("short" or "tail"))).Should().Be(longText);
        Length(vectors[1]).Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public async Task EmbedBatchAsync_TextSplit_LogsAWarningWithoutTheText()
    {
        var provider = CreateProvider(new FakeOllama(maxInputLength: 100));

        await provider.EmbedBatchAsync([new string('x', 150)], EmbeddingInputType.Unspecified);

        _logger.Received(1).Log(
            LogLevel.Warning, Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("150-character") && state.ToString()!.Contains("2 pieces")),
            null, Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task EmbedBatchAsync_TextFitsAfterBatchRejected_DoesNotWarn()
    {
        // The batch as a whole is rejected, but each text alone fits.
        var provider = CreateProvider(new FakeOllama(maxInputLength: 100, maxRequestLength: 150));

        await provider.EmbedBatchAsync([new string('x', 90), new string('y', 90)], EmbeddingInputType.Unspecified);

        _logger.DidNotReceive().Log(
            LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task EmbedBatchAsync_PromptedModel_EveryPieceCarriesThePrompt()
    {
        var ollama = new FakeOllama(maxInputLength: 120);
        var provider = CreateProvider(ollama, model: "nomic-embed-text");
        string longText = string.Join(" ", Enumerable.Repeat("Word", 60));

        await provider.EmbedBatchAsync([longText], EmbeddingInputType.Document);

        ollama.Accepted.Should().HaveCountGreaterThan(1)
            .And.OnlyContain(text => text.StartsWith("search_document: ") && !text.Any(char.IsUpper));
    }

    [Fact]
    public async Task EmbedBatchAsync_RejectionInLaterSubBatch_RetriesOnlyThatSubBatch()
    {
        var ollama = new FakeOllama(maxInputLength: 100);
        var provider = CreateProvider(ollama, batchSize: 2);

        IReadOnlyList<float[]> vectors = await provider.EmbedBatchAsync(
            ["a", "b", new string('z', 150)], EmbeddingInputType.Unspecified);

        vectors.Should().HaveCount(3);
        vectors[1].Should().Equal(FakeOllama.VectorFor("b"));
        // [a, b] once; [z…] rejected as a batch and again alone; then its two halves.
        ollama.Requests.Should().Be(5);
    }

    [Fact]
    public async Task EmbedBatchAsync_RejectedWith413_IsSplitToo()
    {
        var ollama = new FakeOllama(maxInputLength: 100, tooLongStatus: HttpStatusCode.RequestEntityTooLarge);
        var provider = CreateProvider(ollama);

        IReadOnlyList<float[]> vectors = await provider.EmbedBatchAsync([new string('q', 150)], EmbeddingInputType.Unspecified);

        vectors.Should().HaveCount(1);
        ollama.Accepted.Should().HaveCount(2);
    }

    [Fact]
    public async Task EmbedBatchAsync_TooLongEvenWhenSplit_ReportsTheContextLengthNotAConnectionFailure()
    {
        var provider = CreateProvider(new FakeOllama(maxInputLength: 10));

        Func<Task> act = () => provider.EmbedBatchAsync([new string('x', 40)], EmbeddingInputType.Unspecified);

        var ex = (await act.Should().ThrowAsync<EmbeddingInputTooLongException>()).Which;
        ex.Message.Should().Contain("all-minilm-cased")
            .And.Contain("20-character")
            .And.Contain("the input length exceeds the context length")
            .And.NotContain("Failed to connect");
        ex.Reason.Should().Be("the input length exceeds the context length");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":"model \"x\" not found, try pulling it first"}""", "model \"x\" not found, try pulling it first")]
    [InlineData(HttpStatusCode.InternalServerError, "runner crashed", "runner crashed")]
    [InlineData(HttpStatusCode.InternalServerError, """{"message":"other shape"}""", """{"message":"other shape"}""")]
    [InlineData(HttpStatusCode.InternalServerError, """["array"]""", """["array"]""")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":42}""", """{"error":42}""")]
    [InlineData(HttpStatusCode.BadGateway, "", "Bad Gateway")]
    public async Task EmbedBatchAsync_OtherRejection_ReportsStatusAndOllamasWords(
        HttpStatusCode status, string body, string expectedReason)
    {
        var provider = CreateProvider(new StubHandler(status, body));

        Func<Task> act = () => provider.EmbedBatchAsync(["text"], EmbeddingInputType.Unspecified);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Should().NotBeOfType<EmbeddingInputTooLongException>();
        ex.Message.Should().Be(
            $"Ollama rejected the embedding request for model 'all-minilm-cased' ({(int)status} {status}): {expectedReason}");
    }

    [Fact]
    public async Task EmbedBatchAsync_NoReasonPhraseOrBody_SaysSo()
    {
        // .NET has no reason phrase for a status code it does not know.
        var provider = CreateProvider(new StubHandler((HttpStatusCode)599, ""));

        Func<Task> act = () => provider.EmbedBatchAsync(["text"], EmbeddingInputType.Unspecified);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().EndWith(": no error message");
    }

    [Fact]
    public async Task EmbedBatchAsync_OllamaUnreachable_ReportsAConnectionFailure()
    {
        var provider = CreateProvider(new ThrowingHandler());

        Func<Task> act = () => provider.EmbedBatchAsync(["text"], EmbeddingInputType.Unspecified);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().StartWith("Failed to connect to Ollama embedding service at http://localhost:11434/");
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task EmbedBatchAsync_EmptyEmbeddings_Throws()
    {
        var provider = CreateProvider(new StubHandler(HttpStatusCode.OK, """{"embeddings":[]}"""));

        Func<Task> act = () => provider.EmbedBatchAsync(["text"], EmbeddingInputType.Unspecified);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be("Ollama /api/embed returned empty embeddings");
    }

    [Fact]
    public async Task EmbedBatchAsync_NoTexts_SendsNothing()
    {
        var ollama = new FakeOllama(maxInputLength: 100);

        IReadOnlyList<float[]> vectors = await CreateProvider(ollama).EmbedBatchAsync([], EmbeddingInputType.Unspecified);

        vectors.Should().BeEmpty();
        ollama.Requests.Should().Be(0);
    }

    private static float Length(float[] v) => (float)Math.Sqrt(v.Sum(x => x * x));

    /// <summary>
    /// Stands in for Ollama's /api/embed: a request holding any input longer than
    /// <c>maxInputLength</c> characters, or more than <c>maxRequestLength</c> in total, is refused
    /// the way Ollama refuses it when sent <c>"truncate": false</c>. Without that, Ollama's default
    /// is to cut each input to fit and embed only its start, and the fake does the same.
    /// </summary>
    private sealed class FakeOllama(
        int maxInputLength,
        int maxRequestLength = int.MaxValue,
        HttpStatusCode tooLongStatus = HttpStatusCode.BadRequest) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public List<string> Accepted { get; } = [];

        public static float[] VectorFor(string text) => [text.Length, 1f];

        /// <summary>
        /// Truncates each input to the limit unless the request says <c>"truncate": false</c>, then
        /// refuses the request if anything is still over a limit, and otherwise records the inputs
        /// as accepted and returns <see cref="VectorFor"/> of each.
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            List<string> inputs = body.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();

            bool truncate = !body.RootElement.TryGetProperty("truncate", out JsonElement t) || t.ValueKind != JsonValueKind.False;
            if (truncate)
                inputs = inputs.Select(i => i.Length > maxInputLength ? i[..maxInputLength] : i).ToList();

            if (inputs.Any(i => i.Length > maxInputLength) || inputs.Sum(i => i.Length) > maxRequestLength)
                return new HttpResponseMessage(tooLongStatus) { Content = new StringContent(TooLong, Encoding.UTF8, "application/json") };

            Accepted.AddRange(inputs);
            string json = JsonSerializer.Serialize(new { embeddings = inputs.Select(VectorFor) });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No connection could be made");
    }
}
