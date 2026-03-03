using Connapse.Core;
using Connapse.Core.Tests.Utilities;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Vectors;

[Trait("Category", "Unit")]
public class OpenAiEmbeddingProviderTests
{
    [Fact]
    public void Constructor_MissingApiKey_Throws()
    {
        var settings = new TestOptionsSnapshot<EmbeddingSettings>(new EmbeddingSettings
        {
            Provider = "OpenAI",
            Model = "text-embedding-3-small",
            OpenAiApiKey = null
        });
        var logger = Substitute.For<ILogger<OpenAiEmbeddingProvider>>();

        var act = () => new OpenAiEmbeddingProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*API key*required*");
    }

    [Fact]
    public void Constructor_EmptyApiKey_Throws()
    {
        var settings = new TestOptionsSnapshot<EmbeddingSettings>(new EmbeddingSettings
        {
            Provider = "OpenAI",
            Model = "text-embedding-3-small",
            OpenAiApiKey = "  "
        });
        var logger = Substitute.For<ILogger<OpenAiEmbeddingProvider>>();

        var act = () => new OpenAiEmbeddingProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*API key*required*");
    }

    [Fact]
    public void Constructor_ValidApiKey_SetsPropertiesCorrectly()
    {
        var settings = new TestOptionsSnapshot<EmbeddingSettings>(new EmbeddingSettings
        {
            Provider = "OpenAI",
            Model = "text-embedding-3-small",
            OpenAiApiKey = "sk-test-key-123",
            Dimensions = 1536
        });
        var logger = Substitute.For<ILogger<OpenAiEmbeddingProvider>>();

        var provider = new OpenAiEmbeddingProvider(settings, logger);

        provider.Dimensions.Should().Be(1536);
        provider.ModelId.Should().Be("text-embedding-3-small");
    }

    [Fact]
    public void Constructor_WithCustomBaseUrl_DoesNotThrow()
    {
        var settings = new TestOptionsSnapshot<EmbeddingSettings>(new EmbeddingSettings
        {
            Provider = "OpenAI",
            Model = "text-embedding-3-small",
            OpenAiApiKey = "sk-test-key-123",
            OpenAiBaseUrl = "https://my-proxy.example.com/v1"
        });
        var logger = Substitute.For<ILogger<OpenAiEmbeddingProvider>>();

        var act = () => new OpenAiEmbeddingProvider(settings, logger);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task EmbedAsync_EmptyText_Throws()
    {
        var settings = new TestOptionsSnapshot<EmbeddingSettings>(new EmbeddingSettings
        {
            Provider = "OpenAI",
            Model = "text-embedding-3-small",
            OpenAiApiKey = "sk-test-key-123"
        });
        var logger = Substitute.For<ILogger<OpenAiEmbeddingProvider>>();
        var provider = new OpenAiEmbeddingProvider(settings, logger);

        var act = () => provider.EmbedAsync("", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*cannot be empty*");
    }

    [Fact]
    public async Task EmbedBatchAsync_EmptyList_ReturnsEmpty()
    {
        var settings = new TestOptionsSnapshot<EmbeddingSettings>(new EmbeddingSettings
        {
            Provider = "OpenAI",
            Model = "text-embedding-3-small",
            OpenAiApiKey = "sk-test-key-123"
        });
        var logger = Substitute.For<ILogger<OpenAiEmbeddingProvider>>();
        var provider = new OpenAiEmbeddingProvider(settings, logger);

        var result = await provider.EmbedBatchAsync([], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ModelId_ReturnsConfiguredModel()
    {
        var settings = new TestOptionsSnapshot<EmbeddingSettings>(new EmbeddingSettings
        {
            Provider = "OpenAI",
            Model = "text-embedding-3-large",
            OpenAiApiKey = "sk-test-key-123",
            Dimensions = 3072
        });
        var logger = Substitute.For<ILogger<OpenAiEmbeddingProvider>>();
        var provider = new OpenAiEmbeddingProvider(settings, logger);

        provider.ModelId.Should().Be("text-embedding-3-large");
        provider.Dimensions.Should().Be(3072);
    }
}
