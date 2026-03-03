using Connapse.Core;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Llm;

[Trait("Category", "Unit")]
public class AnthropicLlmProviderTests
{
    [Fact]
    public void Constructor_MissingApiKey_Throws()
    {
        var settings = Options.Create(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-sonnet-4-20250514",
            ApiKey = null
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();

        var act = () => new AnthropicLlmProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*API key*required*");
    }

    [Fact]
    public void Constructor_ValidApiKey_SetsProperties()
    {
        var settings = Options.Create(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-sonnet-4-20250514",
            ApiKey = "sk-ant-test-key"
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();

        var provider = new AnthropicLlmProvider(settings, logger);

        provider.Provider.Should().Be("Anthropic");
        provider.ModelId.Should().Be("claude-sonnet-4-20250514");
    }

    [Fact]
    public void Constructor_WithCustomBaseUrl_DoesNotThrow()
    {
        var settings = Options.Create(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-sonnet-4-20250514",
            ApiKey = "sk-ant-test-key",
            BaseUrl = "https://custom-proxy.example.com"
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();

        var act = () => new AnthropicLlmProvider(settings, logger);

        act.Should().NotThrow();
    }

    [Fact]
    public void ModelId_ReturnsConfiguredModel()
    {
        var settings = Options.Create(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-opus-4-20250514",
            ApiKey = "sk-ant-test-key"
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();

        var provider = new AnthropicLlmProvider(settings, logger);

        provider.ModelId.Should().Be("claude-opus-4-20250514");
    }
}
