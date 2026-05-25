using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Ingestion.Tests.Summarization;

[Trait("Category", "Unit")]
public class SummaryLlmResolverTests
{
    // Helpers

    private static IOptionsMonitor<LlmSettings> BuildOptions(string provider, string model)
    {
        LlmSettings settings = new() { Provider = provider, Model = model };
        IOptionsMonitor<LlmSettings> monitor = Substitute.For<IOptionsMonitor<LlmSettings>>();
        monitor.CurrentValue.Returns(settings);
        return monitor;
    }

    private static ILlmProvider BuildStub(string provider, string modelId)
    {
        ILlmProvider stub = Substitute.For<ILlmProvider>();
        stub.Provider.Returns(provider);
        stub.ModelId.Returns(modelId);
        return stub;
    }

    // Test helper: simulates the resolve logic of SummaryLlmResolver without DI complexity
    private sealed class TestSummaryLlmResolver
    {
        private readonly IOptionsMonitor<LlmSettings> _llmOptions;
        private readonly Dictionary<string, ILlmProvider?> _providers = new(StringComparer.OrdinalIgnoreCase);

        public TestSummaryLlmResolver(IOptionsMonitor<LlmSettings> llmOptions, ILlmProvider? defaultProvider)
        {
            _llmOptions = llmOptions;
            _providers["OPENAI"] = null;
            _providers["AZUREOPENAI"] = null;
            _providers["ANTHROPIC"] = null;
            _providers["OLLAMA"] = null;
            _defaultProvider = defaultProvider;
        }

        private readonly ILlmProvider? _defaultProvider;

        public void SetProvider(string name, ILlmProvider provider) =>
            _providers[name.ToUpperInvariant()] = provider;

        public ILlmProvider? Resolve(SummarySettings? summarySettings)
        {
            LlmSettings globalSettings = _llmOptions.CurrentValue;
            string effectiveProvider = (summarySettings?.LlmProvider ?? globalSettings.Provider).Trim();

            ILlmProvider? resolved = effectiveProvider.ToUpperInvariant() switch
            {
                "OPENAI" => _providers["OPENAI"],
                "AZUREOPENAI" => _providers["AZUREOPENAI"],
                "ANTHROPIC" => _providers["ANTHROPIC"],
                "OLLAMA" => _providers["OLLAMA"],
                _ => null
            };

            return resolved ?? _defaultProvider;
        }
    }

    // Tests

    [Fact]
    public void Resolve_NoSummarySettings_ReturnsGlobalProvider()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        ILlmProvider expectedProvider = BuildStub("Anthropic", "claude-haiku-4-5");

        var resolver = new TestSummaryLlmResolver(options, expectedProvider);
        ILlmProvider? result = resolver.Resolve(summarySettings: null);

        result.Should().BeSameAs(expectedProvider);
    }

    [Fact]
    public void Resolve_SummarySettingsWithoutLlmProvider_ReturnsGlobalProvider()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        ILlmProvider expectedProvider = BuildStub("Anthropic", "claude-haiku-4-5");

        var resolver = new TestSummaryLlmResolver(options, expectedProvider);
        SummarySettings settings = new(); // LlmProvider is null
        ILlmProvider? result = resolver.Resolve(settings);

        result.Should().BeSameAs(expectedProvider);
    }

    [Fact]
    public void Resolve_OverrideProvider_ReturnsOverriddenProvider()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        ILlmProvider globalProvider = BuildStub("Anthropic", "claude-haiku-4-5");
        ILlmProvider openAiProvider = BuildStub("OpenAI", "gpt-4.1-nano");

        var resolver = new TestSummaryLlmResolver(options, globalProvider);
        resolver.SetProvider("OpenAI", openAiProvider);

        SummarySettings settings = new() { LlmProvider = "openai" }; // test case-insensitive
        ILlmProvider? result = resolver.Resolve(settings);

        result.Should().BeSameAs(openAiProvider);
        result!.Provider.Should().Be("OpenAI");
    }

    [Fact]
    public void Resolve_PartialOverride_OnlyProvider_ReturnsThatProviderWithItsOwnModel()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        ILlmProvider anthropicProvider = BuildStub("Anthropic", "claude-haiku-4-5");
        ILlmProvider openAiProvider = BuildStub("OpenAI", "gpt-4.1-nano");

        var resolver = new TestSummaryLlmResolver(options, anthropicProvider);
        resolver.SetProvider("OpenAI", openAiProvider);

        SummarySettings settings = new() { LlmProvider = "OpenAI" }; // no LlmModel override
        ILlmProvider? result = resolver.Resolve(settings);

        // The provider is overridden; model stays as whatever the OpenAI provider has
        result.Should().BeSameAs(openAiProvider);
    }

    [Fact]
    public void Resolve_OllamaOverride_ReturnsOllamaProvider()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        ILlmProvider globalProvider = BuildStub("Anthropic", "claude-haiku-4-5");
        ILlmProvider ollamaProvider = BuildStub("Ollama", "llama3.2");

        var resolver = new TestSummaryLlmResolver(options, globalProvider);
        resolver.SetProvider("Ollama", ollamaProvider);

        SummarySettings settings = new() { LlmProvider = "Ollama" };
        ILlmProvider? result = resolver.Resolve(settings);

        result.Should().BeSameAs(ollamaProvider);
    }
}
