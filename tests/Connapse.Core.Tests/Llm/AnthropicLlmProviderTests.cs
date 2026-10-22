using Connapse.Core;
using Connapse.Core.Tests.Utilities;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Llm;

[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
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

    [Theory]
    [InlineData("claude-sonnet-4-20250514", 0.2f, null, 0.2f)]
    [InlineData("claude-haiku-4-5", 0.2f, 0f, 0f)]
    [InlineData("claude-3-5-sonnet-20241022", 0.7f, null, 0.7f)]
    [InlineData("claude-opus-4-6", 0.2f, 0.5f, 0.5f)]
    public void BuildParams_ModelThatAcceptsTemperature_SendsTheRequestedOrConfiguredOne(
        string model, float configured, float? requested, float expected)
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "Anthropic",
            Model = model,
            AnthropicApiKey = "sk-ant-test-key",
            Temperature = configured,
        });
        var provider = new AnthropicLlmProvider(settings, Substitute.For<ILogger<AnthropicLlmProvider>>());

        var parameters = provider.BuildParams("sys", "usr", options: new LlmCompletionOptions(Temperature: requested));

#pragma warning disable CS0618
        parameters.Temperature.Should().BeApproximately(expected, 1e-6);
#pragma warning restore CS0618
    }

    [Fact]
    public void BuildParams_ModelNewerThanOpus46_IsSentNoTemperature()
    {
        var settings = new TestOptionsSnapshot<LlmSettings>(new LlmSettings
        {
            Provider = "Anthropic",
            Model = "claude-opus-5-5",
            AnthropicApiKey = "sk-ant-test-key",
            Temperature = 0.2,
        });
        var logger = Substitute.For<ILogger<AnthropicLlmProvider>>();
        var provider = new AnthropicLlmProvider(settings, logger);

        var parameters = provider.BuildParams("sys", "usr", options: new LlmCompletionOptions(Temperature: 0f));

        // The property is obsolete because newer models reject it; reading it here is the point.
#pragma warning disable CS0618
        parameters.Temperature.Should().BeNull();
#pragma warning restore CS0618
    }
}
