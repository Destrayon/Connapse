using System.Net;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Tests.Utilities;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Llm;

[Trait("Category", "Unit")]
public class OllamaLlmProviderTests
{
    private static OllamaLlmProvider CreateProvider(
        HttpMessageHandler handler,
        LlmSettings? settings = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };
        var opts = new TestOptionsSnapshot<LlmSettings>(settings ?? new LlmSettings
        {
            Provider = "Ollama",
            Model = "llama3.2",
            BaseUrl = "http://localhost:11434"
        });
        var logger = Substitute.For<ILogger<OllamaLlmProvider>>();
        return new OllamaLlmProvider(httpClient, opts, logger);
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
}
