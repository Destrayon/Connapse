using Connapse.Core;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Llm;

[Trait("Category", "Unit")]
public class AzureOpenAiLlmProviderTests
{
    [Fact]
    public void Constructor_MissingEndpoint_Throws()
    {
        var settings = Options.Create(new LlmSettings
        {
            Provider = "AzureOpenAI",
            Model = "gpt-4o",
            ApiKey = "test-key",
            BaseUrl = null
        });
        var logger = Substitute.For<ILogger<AzureOpenAiLlmProvider>>();

        var act = () => new AzureOpenAiLlmProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*endpoint URL*required*");
    }

    [Fact]
    public void Constructor_MissingApiKey_Throws()
    {
        var settings = Options.Create(new LlmSettings
        {
            Provider = "AzureOpenAI",
            Model = "gpt-4o",
            ApiKey = null,
            BaseUrl = "https://my-resource.openai.azure.com"
        });
        var logger = Substitute.For<ILogger<AzureOpenAiLlmProvider>>();

        var act = () => new AzureOpenAiLlmProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*API key*required*");
    }

    [Fact]
    public void ModelId_UsesDeploymentName_WhenProvided()
    {
        var settings = Options.Create(new LlmSettings
        {
            Provider = "AzureOpenAI",
            Model = "gpt-4o",
            ApiKey = "test-key",
            BaseUrl = "https://my-resource.openai.azure.com",
            AzureDeploymentName = "my-deployment"
        });
        var logger = Substitute.For<ILogger<AzureOpenAiLlmProvider>>();

        var provider = new AzureOpenAiLlmProvider(settings, logger);

        provider.ModelId.Should().Be("my-deployment");
    }

    [Fact]
    public void ModelId_FallsBackToModel_WhenNoDeploymentName()
    {
        var settings = Options.Create(new LlmSettings
        {
            Provider = "AzureOpenAI",
            Model = "gpt-4o",
            ApiKey = "test-key",
            BaseUrl = "https://my-resource.openai.azure.com",
            AzureDeploymentName = null
        });
        var logger = Substitute.For<ILogger<AzureOpenAiLlmProvider>>();

        var provider = new AzureOpenAiLlmProvider(settings, logger);

        provider.ModelId.Should().Be("gpt-4o");
    }

    [Fact]
    public void Provider_ReturnsAzureOpenAI()
    {
        var settings = Options.Create(new LlmSettings
        {
            Provider = "AzureOpenAI",
            Model = "gpt-4o",
            ApiKey = "test-key",
            BaseUrl = "https://my-resource.openai.azure.com"
        });
        var logger = Substitute.For<ILogger<AzureOpenAiLlmProvider>>();

        var provider = new AzureOpenAiLlmProvider(settings, logger);

        provider.Provider.Should().Be("AzureOpenAI");
    }
}
