using System.Net;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Tests.Utilities;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Llm;

[Trait("Category", "Unit")]
public class OllamaLlmProviderTests
{
    private static OllamaLlmProvider CreateProvider(
        HttpMessageHandler handler,
        LlmSettings? settings = null,
        LlmConcurrencyGate? gate = null)
    {
        var resolvedSettings = settings ?? new LlmSettings
        {
            Provider = "Ollama",
            Model = "llama3.2",
            BaseUrl = "http://localhost:11434"
        };
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };
        var opts = new TestOptionsSnapshot<LlmSettings>(resolvedSettings);
        var logger = Substitute.For<ILogger<OllamaLlmProvider>>();
        return new OllamaLlmProvider(
            httpClient,
            opts,
            logger,
            gate ?? new LlmConcurrencyGate(Options.Create(resolvedSettings)));
    }

    [Fact]
    public void Provider_ReturnsOllama()
    {
        var handler = new StubHandler("""{"message":{"role":"assistant","content":"hi"},"done":true}""");
        var provider = CreateProvider(handler);

        provider.Provider.Should().Be("Ollama");
        provider.ModelId.Should().Be("llama3.2");
    }

    [Fact]
    public async Task CompleteAsync_ValidResponse_ReturnsContent()
    {
        var json = """{"message":{"role":"assistant","content":"Hello world"},"done":true}""";
        var handler = new StubHandler(json);
        var provider = CreateProvider(handler);

        var result = await provider.CompleteAsync("system", "say hello");

        result.Should().Be("Hello world");
    }

    [Fact]
    public async Task CompleteAsync_EmptyMessageContent_ThrowsInvalidOperation()
    {
        var json = """{"message":{"role":"assistant","content":null},"done":true}""";
        var handler = new StubHandler(json);
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync("system", "say hello");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*empty response*");
    }

    [Fact]
    public async Task CompleteAsync_HttpError_ThrowsInvalidOperation()
    {
        var handler = new StubHandler(statusCode: HttpStatusCode.InternalServerError);
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync("system", "say hello");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to connect to Ollama*");
    }

    [Fact]
    public async Task StreamAsync_MultipleChunks_YieldsAllTokens()
    {
        // Ollama streams NDJSON — one JSON object per line
        var lines = new StringBuilder();
        lines.AppendLine("""{"message":{"role":"assistant","content":"Hello"},"done":false}""");
        lines.AppendLine("""{"message":{"role":"assistant","content":" world"},"done":false}""");
        lines.AppendLine("""{"message":{"role":"assistant","content":"!"},"done":true}""");

        var handler = new StubHandler(lines.ToString());
        var provider = CreateProvider(handler);

        var tokens = new List<string>();
        await foreach (var token in provider.StreamAsync("system", "say hello"))
            tokens.Add(token);

        tokens.Should().BeEquivalentTo(["Hello", " world", "!"]);
    }

    [Fact]
    public void ResolveModel_NoOverride_UsesConfiguredModel()
    {
        var settings = new LlmSettings { Provider = "Ollama", Model = "llama3.2", BaseUrl = "http://localhost:11434" };
        var handler = new StubHandler("""{"message":{"role":"assistant","content":"hi"},"done":true}""");
        var provider = CreateProvider(handler, settings);

        provider.ResolveModel(null).Should().Be("llama3.2");
    }

    [Fact]
    public void ResolveModel_WithModelOverride_UsesOverride()
    {
        var settings = new LlmSettings { Provider = "Ollama", Model = "llama3.2", BaseUrl = "http://localhost:11434" };
        var handler = new StubHandler("""{"message":{"role":"assistant","content":"hi"},"done":true}""");
        var provider = CreateProvider(handler, settings);

        var options = new LlmCompletionOptions(Model: "qwen3:14b");
        provider.ResolveModel(options).Should().Be("qwen3:14b");
    }

    [Fact]
    public async Task CompleteAsync_ConcurrentCalls_AreSerializedByGate()
    {
        // Regression guard for the Ollama queue-saturation timeout: with MaxConcurrentRequests=1
        // the provider must let only ONE /api/chat call reach Ollama at a time, even when many
        // callers invoke CompleteAsync at once. Without the gate, all N hit Ollama together,
        // its internal queue backs up, and the tail requests blow past HttpClient.Timeout.
        var json = """{"message":{"role":"assistant","content":"ok"},"done":true}""";
        var handler = new ConcurrencyTrackingHandler(json, TimeSpan.FromMilliseconds(75));
        var settings = new LlmSettings
        {
            Provider = "Ollama",
            Model = "llama3.2",
            BaseUrl = "http://localhost:11434",
            MaxConcurrentRequests = 1,
        };
        var provider = CreateProvider(handler, settings);

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => provider.CompleteAsync("system", "hi"))
            .ToArray();
        await Task.WhenAll(tasks);

        handler.MaxObservedConcurrency.Should().Be(1,
            "the concurrency gate must serialize Ollama calls to MaxConcurrentRequests");
    }

    /// <summary>
    /// Minimal HttpMessageHandler stub that returns a canned response.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string? _content;
        private readonly HttpStatusCode _statusCode;

        public StubHandler(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _content = content;
            _statusCode = statusCode;
        }

        public StubHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = _content is not null
                    ? new StringContent(_content, Encoding.UTF8, "application/json")
                    : null
            });
    }

    /// <summary>
    /// Handler that records the peak number of requests in flight at once, so a test can
    /// assert the provider never lets more than the gate limit reach Ollama concurrently.
    /// </summary>
    private sealed class ConcurrencyTrackingHandler(string content, TimeSpan delay) : HttpMessageHandler
    {
        private int _current;
        private int _maxObserved;

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int inFlight = Interlocked.Increment(ref _current);
            UpdateMax(inFlight);
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }

        private void UpdateMax(int observed)
        {
            int current;
            while (observed > (current = Volatile.Read(ref _maxObserved)))
                Interlocked.CompareExchange(ref _maxObserved, observed, current);
        }
    }
}
