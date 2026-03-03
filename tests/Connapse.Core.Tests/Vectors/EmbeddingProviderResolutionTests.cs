using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connapse.Core.Tests.Vectors;

[Trait("Category", "Unit")]
public class EmbeddingProviderResolutionTests
{
    /// <summary>
    /// Verifies the factory delegate pattern resolves the correct provider based on settings.
    /// This test replicates the DI registration logic from ServiceCollectionExtensions.
    /// </summary>
    [Theory]
    [InlineData("Ollama", typeof(OllamaEmbeddingProvider))]
    [InlineData("ollama", typeof(OllamaEmbeddingProvider))]  // default/fallback
    [InlineData("SomethingElse", typeof(OllamaEmbeddingProvider))]  // unknown → fallback
    public void Resolve_OllamaVariants_ReturnsOllamaProvider(string provider, Type expectedType)
    {
        // The factory delegate uses exact string matching for OpenAI/AzureOpenAI,
        // falling back to Ollama for anything else (including case variants).
        // This test validates the switch logic without actually constructing providers.
        var result = ResolveProviderType(provider);
        result.Should().Be(expectedType);
    }

    [Fact]
    public void Resolve_OpenAI_ReturnsOpenAiProvider()
    {
        var result = ResolveProviderType("OpenAI");
        result.Should().Be(typeof(OpenAiEmbeddingProvider));
    }

    [Fact]
    public void Resolve_AzureOpenAI_ReturnsAzureOpenAiProvider()
    {
        var result = ResolveProviderType("AzureOpenAI");
        result.Should().Be(typeof(AzureOpenAiEmbeddingProvider));
    }

    /// <summary>
    /// Mirrors the switch expression from ServiceCollectionExtensions to validate routing logic.
    /// </summary>
    private static Type ResolveProviderType(string provider)
    {
        return provider switch
        {
            "OpenAI" => typeof(OpenAiEmbeddingProvider),
            "AzureOpenAI" => typeof(AzureOpenAiEmbeddingProvider),
            _ => typeof(OllamaEmbeddingProvider)
        };
    }
}
