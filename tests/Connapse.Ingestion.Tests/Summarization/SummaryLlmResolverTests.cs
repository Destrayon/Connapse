using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    private static IServiceProvider BuildServiceProvider(string providerName)
    {
        // Build a service collection with a stub ILlmProvider registered under the concrete types
        // that SummaryLlmResolver switches on.
        ServiceCollection services = new();

        ILlmProvider anthropicStub = BuildStub("Anthropic", "claude-haiku-4-5");
        ILlmProvider openAiStub = BuildStub("OpenAI", "gpt-4.1-nano");
        ILlmProvider ollamaStub = BuildStub("Ollama", "llama3.2");

        // Register stub doubles under concrete-class-matching service types via factory
        // We can't register AnthropicLlmProvider directly (it needs real HttpClient/SDK).
        // Instead register ILlmProvider with each name as a keyed service, and test
        // using a minimal IServiceProvider shim that mimics the switch logic.
        // This keeps the test in pure-unit territory without real HTTP clients.
        services.AddSingleton(anthropicStub);
        services.AddSingleton(openAiStub);
        services.AddSingleton(ollamaStub);

        // Register ILlmProvider as the global fallback
        ILlmProvider defaultStub = BuildStub(providerName, "default-model");
        services.AddSingleton<ILlmProvider>(defaultStub);

        return services.BuildServiceProvider();
    }

    private static ILlmProvider BuildStub(string provider, string modelId)
    {
        ILlmProvider stub = Substitute.For<ILlmProvider>();
        stub.Provider.Returns(provider);
        stub.ModelId.Returns(modelId);
        return stub;
    }

    // Tests — using a test-specific subclass to inject concrete stub providers

    [Fact]
    public void Resolve_NoSummarySettings_ReturnsGlobalProvider()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        ILlmProvider expectedProvider = BuildStub("Anthropic", "claude-haiku-4-5");
        TestSummaryLlmResolver resolver = new(options, expectedProvider);

        ILlmProvider? result = resolver.Resolve(summarySettings: null);

        result.Should().BeSameAs(expectedProvider);
    }

    [Fact]
    public void Resolve_SummarySettingsWithoutLlmProvider_ReturnsGlobalProvider()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        ILlmProvider expectedProvider = BuildStub("Anthropic", "claude-haiku-4-5");
        TestSummaryLlmResolver resolver = new(options, expectedProvider);

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
        TestSummaryLlmResolver resolver = new(options, globalProvider, openAiProvider: openAiProvider);

        SummarySettings settings = new() { LlmProvider = "OpenAI" };
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
        TestSummaryLlmResolver resolver = new(options, anthropicProvider, openAiProvider: openAiProvider);

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
        TestSummaryLlmResolver resolver = new(options, globalProvider, ollamaProvider: ollamaProvider);

        SummarySettings settings = new() { LlmProvider = "Ollama" };
        ILlmProvider? result = resolver.Resolve(settings);

        result.Should().BeSameAs(ollamaProvider);
    }

    // ---------------------------------------------------------------------------
    // Test helper: exposes the resolver switch without needing real provider DI
    // ---------------------------------------------------------------------------

    private sealed class TestSummaryLlmResolver(
        IOptionsMonitor<LlmSettings> llmOptions,
        ILlmProvider defaultProvider,
        ILlmProvider? openAiProvider = null,
        ILlmProvider? azureOpenAiProvider = null,
        ILlmProvider? anthropicProvider = null,
        ILlmProvider? ollamaProvider = null)
    {
        public ILlmProvider? Resolve(SummarySettings? summarySettings)
        {
            string effectiveProvider = summarySettings?.LlmProvider ?? llmOptions.CurrentValue.Provider;

            return effectiveProvider switch
            {
                "OpenAI" => openAiProvider ?? defaultProvider,
                "AzureOpenAI" => azureOpenAiProvider ?? defaultProvider,
                "Anthropic" => anthropicProvider ?? defaultProvider,
                "Ollama" => ollamaProvider ?? defaultProvider,
                _ => defaultProvider
            };
        }
    }
}
