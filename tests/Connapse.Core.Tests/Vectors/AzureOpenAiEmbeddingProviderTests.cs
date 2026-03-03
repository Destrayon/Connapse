using Connapse.Core;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Vectors;

[Trait("Category", "Unit")]
public class AzureOpenAiEmbeddingProviderTests
{
    [Fact]
    public void Constructor_MissingBaseUrl_Throws()
    {
        var settings = Options.Create(new EmbeddingSettings
        {
            Provider = "AzureOpenAI",
            Model = "text-embedding-3-small",
            ApiKey = "test-key",
            BaseUrl = null
        });
        var logger = Substitute.For<ILogger<AzureOpenAiEmbeddingProvider>>();

        var act = () => new AzureOpenAiEmbeddingProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*endpoint URL*required*");
    }

    [Fact]
    public void Constructor_MissingApiKey_Throws()
    {
        var settings = Options.Create(new EmbeddingSettings
        {
            Provider = "AzureOpenAI",
            Model = "text-embedding-3-small",
            BaseUrl = "https://my-resource.openai.azure.com",
            ApiKey = null
        });
        var logger = Substitute.For<ILogger<AzureOpenAiEmbeddingProvider>>();

        var act = () => new AzureOpenAiEmbeddingProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*API key*required*");
    }

    [Fact]
    public void Constructor_ValidSettings_SetsProperties()
    {
        var settings = Options.Create(new EmbeddingSettings
        {
            Provider = "AzureOpenAI",
            Model = "text-embedding-3-small",
            ApiKey = "test-key",
            BaseUrl = "https://my-resource.openai.azure.com",
            AzureDeploymentName = "my-embedding-deployment",
            Dimensions = 1536
        });
        var logger = Substitute.For<ILogger<AzureOpenAiEmbeddingProvider>>();

        var provider = new AzureOpenAiEmbeddingProvider(settings, logger);

        provider.Dimensions.Should().Be(1536);
        provider.ModelId.Should().Be("my-embedding-deployment");
    }

    [Fact]
    public void ModelId_FallsBackToModel_WhenDeploymentNameEmpty()
    {
        var settings = Options.Create(new EmbeddingSettings
        {
            Provider = "AzureOpenAI",
            Model = "text-embedding-3-small",
            ApiKey = "test-key",
            BaseUrl = "https://my-resource.openai.azure.com",
            AzureDeploymentName = null,
            Dimensions = 1536
        });
        var logger = Substitute.For<ILogger<AzureOpenAiEmbeddingProvider>>();

        var provider = new AzureOpenAiEmbeddingProvider(settings, logger);

        provider.ModelId.Should().Be("text-embedding-3-small");
    }

    [Fact]
    public async Task EmbedAsync_EmptyText_Throws()
    {
        var settings = Options.Create(new EmbeddingSettings
        {
            Provider = "AzureOpenAI",
            Model = "text-embedding-3-small",
            ApiKey = "test-key",
            BaseUrl = "https://my-resource.openai.azure.com"
        });
        var logger = Substitute.For<ILogger<AzureOpenAiEmbeddingProvider>>();
        var provider = new AzureOpenAiEmbeddingProvider(settings, logger);

        var act = () => provider.EmbedAsync("", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*cannot be empty*");
    }

    [Fact]
    public async Task EmbedBatchAsync_EmptyList_ReturnsEmpty()
    {
        var settings = Options.Create(new EmbeddingSettings
        {
            Provider = "AzureOpenAI",
            Model = "text-embedding-3-small",
            ApiKey = "test-key",
            BaseUrl = "https://my-resource.openai.azure.com"
        });
        var logger = Substitute.For<ILogger<AzureOpenAiEmbeddingProvider>>();
        var provider = new AzureOpenAiEmbeddingProvider(settings, logger);

        var result = await provider.EmbedBatchAsync([], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_EmptyBaseUrl_Throws()
    {
        var settings = Options.Create(new EmbeddingSettings
        {
            Provider = "AzureOpenAI",
            Model = "text-embedding-3-small",
            ApiKey = "test-key",
            BaseUrl = "  "
        });
        var logger = Substitute.For<ILogger<AzureOpenAiEmbeddingProvider>>();

        var act = () => new AzureOpenAiEmbeddingProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*endpoint URL*required*");
    }
}
