using Connapse.Core;
using Connapse.Core.Tests.Utilities;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Llm;

[Trait("Category", "Unit")]
public class OpenAiLlmProviderTests
{
    [Fact]
    public void Constructor_MissingApiKey_Throws()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "OpenAI",
            Model = "gpt-4o",
            OpenAiApiKey = null
        });
        var logger = Substitute.For<ILogger<OpenAiLlmProvider>>();

        var act = () => new OpenAiLlmProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*API key*required*");
    }

    [Fact]
    public void Constructor_EmptyApiKey_Throws()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "OpenAI",
            Model = "gpt-4o",
            OpenAiApiKey = "  "
        });
        var logger = Substitute.For<ILogger<OpenAiLlmProvider>>();

        var act = () => new OpenAiLlmProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*API key*required*");
    }

    [Fact]
    public void Constructor_ValidApiKey_SetsProperties()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "OpenAI",
            Model = "gpt-4o",
            OpenAiApiKey = "sk-test-key-123"
        });
        var logger = Substitute.For<ILogger<OpenAiLlmProvider>>();

        var provider = new OpenAiLlmProvider(settings, logger);

        provider.Provider.Should().Be("OpenAI");
        provider.ModelId.Should().Be("gpt-4o");
    }

    [Fact]
    public void Constructor_WithCustomBaseUrl_DoesNotThrow()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "OpenAI",
            Model = "gpt-4o",
            OpenAiApiKey = "sk-test-key-123",
            OpenAiBaseUrl = "https://my-proxy.example.com/v1"
        });
        var logger = Substitute.For<ILogger<OpenAiLlmProvider>>();

        var act = () => new OpenAiLlmProvider(settings, logger);

        act.Should().NotThrow();
    }

    [Fact]
    public void Provider_ReturnsOpenAI()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "OpenAI",
            Model = "gpt-4o-mini",
            OpenAiApiKey = "sk-test-key"
        });
        var logger = Substitute.For<ILogger<OpenAiLlmProvider>>();
        var provider = new OpenAiLlmProvider(settings, logger);

        provider.Provider.Should().Be("OpenAI");
    }

    [Fact]
    public void ModelId_ReturnsConfiguredModel()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "OpenAI",
            Model = "gpt-4o-mini",
            OpenAiApiKey = "sk-test-key"
        });
        var logger = Substitute.For<ILogger<OpenAiLlmProvider>>();
        var provider = new OpenAiLlmProvider(settings, logger);

        provider.ModelId.Should().Be("gpt-4o-mini");
    }
}
