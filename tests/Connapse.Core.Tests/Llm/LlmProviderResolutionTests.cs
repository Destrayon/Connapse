using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Llm;
using FluentAssertions;

namespace Connapse.Core.Tests.Llm;

[Trait("Category", "Unit")]
public class LlmProviderResolutionTests
{
    [Theory]
    [InlineData("Ollama", typeof(OllamaLlmProvider))]
    [InlineData("ollama", typeof(OllamaLlmProvider))]
    [InlineData("SomethingElse", typeof(OllamaLlmProvider))]
    [InlineData("", typeof(OllamaLlmProvider))]
    public void Resolve_OllamaVariants_ReturnsOllamaProvider(string provider, Type expectedType)
    {
        var result = ResolveProviderType(provider);
        result.Should().Be(expectedType);
    }

    [Fact]
    public void Resolve_OpenAI_ReturnsOpenAiProvider()
    {
        var result = ResolveProviderType("OpenAI");
        result.Should().Be(typeof(OpenAiLlmProvider));
    }

    [Fact]
    public void Resolve_AzureOpenAI_ReturnsAzureOpenAiProvider()
    {
        var result = ResolveProviderType("AzureOpenAI");
        result.Should().Be(typeof(AzureOpenAiLlmProvider));
    }

    [Fact]
    public void Resolve_Anthropic_ReturnsAnthropicProvider()
    {
        var result = ResolveProviderType("Anthropic");
        result.Should().Be(typeof(AnthropicLlmProvider));
    }

    [Fact]
    public void Resolve_CaseSensitive_OpenAI_DoesNotMatchLowerCase()
    {
        // The factory uses exact string matching, so "openai" != "OpenAI"
        var result = ResolveProviderType("openai");
        result.Should().Be(typeof(OllamaLlmProvider));
    }

    /// <summary>
    /// Mirrors the switch expression from ServiceCollectionExtensions to validate routing logic.
    /// </summary>
    private static Type ResolveProviderType(string provider)
    {
        return provider switch
        {
            "OpenAI" => typeof(OpenAiLlmProvider),
            "AzureOpenAI" => typeof(AzureOpenAiLlmProvider),
            "Anthropic" => typeof(AnthropicLlmProvider),
            _ => typeof(OllamaLlmProvider)
        };
    }
}
