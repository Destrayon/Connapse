using Connapse.Core;
using Connapse.Core.Tests.Utilities;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Llm;

[Trait("Category", "Unit")]
public class AnthropicLlmProviderTests
{
    [Fact]
    public void Constructor_MissingApiKey_Throws()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-sonnet-4-20250514",
            AnthropicApiKey = null
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();

        var act = () => new AnthropicLlmProvider(settings, logger);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*API key*required*");
    }

    [Fact]
    public void Constructor_ValidApiKey_SetsProperties()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-sonnet-4-20250514",
            AnthropicApiKey = "sk-ant-test-key"
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();

        var provider = new AnthropicLlmProvider(settings, logger);

        provider.Provider.Should().Be("Anthropic");
        provider.ModelId.Should().Be("claude-sonnet-4-20250514");
    }

    [Fact]
    public void Constructor_WithCustomBaseUrl_DoesNotThrow()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-sonnet-4-20250514",
            AnthropicApiKey = "sk-ant-test-key",
            AnthropicBaseUrl = "https://custom-proxy.example.com"
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();

        var act = () => new AnthropicLlmProvider(settings, logger);

        act.Should().NotThrow();
    }

    [Fact]
    public void ModelId_ReturnsConfiguredModel()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-opus-4-20250514",
            AnthropicApiKey = "sk-ant-test-key"
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();

        var provider = new AnthropicLlmProvider(settings, logger);

        provider.ModelId.Should().Be("claude-opus-4-20250514");
    }

    [Fact]
    public void BuildParams_NoOverride_UsesConfiguredModel()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-haiku-4-5",
            AnthropicApiKey = "sk-ant-test-key"
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();
        var provider = new AnthropicLlmProvider(settings, logger);

        var parameters = provider.BuildParams("sys", "usr", options: null);

        parameters.Model.Raw().Should().Be("claude-haiku-4-5");
    }

    [Fact]
    public void BuildParams_WithModelOverride_UsesOverrideInsteadOfConfigured()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-haiku-4-5",
            AnthropicApiKey = "sk-ant-test-key"
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();
        var provider = new AnthropicLlmProvider(settings, logger);

        var parameters = provider.BuildParams(
            "sys",
            "usr",
            options: new LlmCompletionOptions(Model: "claude-sonnet-4-6"));

        parameters.Model.Raw().Should().Be("claude-sonnet-4-6");
    }
}
