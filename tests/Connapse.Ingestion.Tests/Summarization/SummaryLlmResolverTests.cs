using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Llm;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Ingestion.Tests.Summarization;

/// <summary>
/// Tests exercise the real <see cref="SummaryLlmResolver"/> against a substituted
/// <see cref="IServiceProvider"/>. Concrete provider types (e.g., <see cref="OpenAiLlmProvider"/>)
/// have non-mockable constructors (real API config required), so override-path assertions
/// verify that the resolver requests the correct concrete type via <c>Received(1)</c>
/// rather than asserting on a returned mock instance. Default/fallback-path assertions
/// verify the <see cref="ILlmProvider"/> registration is consulted.
/// </summary>
[Trait("Category", "Unit")]
public class SummaryLlmResolverTests
{
    private static IOptionsMonitor<LlmSettings> BuildOptions(string provider, string model)
    {
        LlmSettings settings = new() { Provider = provider, Model = model };
        IOptionsMonitor<LlmSettings> monitor = Substitute.For<IOptionsMonitor<LlmSettings>>();
        monitor.CurrentValue.Returns(settings);
        return monitor;
    }

    private static ILlmProvider BuildStub(string provider, string modelId)
    {
        ILlmProvider stub = Substitute.For<ILlmProvider>();
        stub.Provider.Returns(provider);
        stub.ModelId.Returns(modelId);
        return stub;
    }

    [Fact]
    public void Resolve_NoSummarySettings_FallsBackToILlmProvider_WhenGlobalProviderUnknown()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("UnknownProvider", "model");
        ILlmProvider fallback = BuildStub("Fallback", "model");

        IServiceProvider sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ILlmProvider)).Returns(fallback);

        SummaryLlmResolver resolver = new(options, sp);
        ILlmProvider? result = resolver.Resolve(summarySettings: null);

        result.Should().BeSameAs(fallback);
    }

    [Fact]
    public void Resolve_NoSummarySettings_RequestsConcreteTypeForGlobalProvider()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        IServiceProvider sp = Substitute.For<IServiceProvider>();

        SummaryLlmResolver resolver = new(options, sp);
        _ = resolver.Resolve(summarySettings: null);

        sp.Received(1).GetService(typeof(AnthropicLlmProvider));
    }

    [Fact]
    public void Resolve_SummarySettingsWithoutLlmProvider_UsesGlobalProvider()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("OpenAI", "gpt-4.1-nano");
        IServiceProvider sp = Substitute.For<IServiceProvider>();

        SummaryLlmResolver resolver = new(options, sp);
        SummarySettings settings = new(); // LlmProvider is null
        _ = resolver.Resolve(settings);

        sp.Received(1).GetService(typeof(OpenAiLlmProvider));
    }

    [Fact]
    public void Resolve_OverrideProvider_RequestsOverrideConcreteType()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        IServiceProvider sp = Substitute.For<IServiceProvider>();

        SummaryLlmResolver resolver = new(options, sp);
        SummarySettings settings = new() { LlmProvider = "OpenAI" };
        _ = resolver.Resolve(settings);

        sp.Received(1).GetService(typeof(OpenAiLlmProvider));
        sp.DidNotReceive().GetService(typeof(AnthropicLlmProvider));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("OPENAI")]
    [InlineData("OpenAI")]
    [InlineData("  OpenAI  ")]
    public void Resolve_OverrideProvider_IsCaseInsensitiveAndTrimmed(string overrideProvider)
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "claude-haiku-4-5");
        IServiceProvider sp = Substitute.For<IServiceProvider>();

        SummaryLlmResolver resolver = new(options, sp);
        SummarySettings settings = new() { LlmProvider = overrideProvider };
        _ = resolver.Resolve(settings);

        sp.Received(1).GetService(typeof(OpenAiLlmProvider));
    }

    [Theory]
    [InlineData("AzureOpenAI", typeof(AzureOpenAiLlmProvider))]
    [InlineData("Anthropic", typeof(AnthropicLlmProvider))]
    [InlineData("Ollama", typeof(OllamaLlmProvider))]
    [InlineData("OpenAI", typeof(OpenAiLlmProvider))]
    public void Resolve_AllSupportedProviders_RouteToTheirConcreteType(string providerName, Type expectedType)
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "model");
        IServiceProvider sp = Substitute.For<IServiceProvider>();

        SummaryLlmResolver resolver = new(options, sp);
        SummarySettings settings = new() { LlmProvider = providerName };
        _ = resolver.Resolve(settings);

        sp.Received(1).GetService(expectedType);
    }

    [Fact]
    public void Resolve_UnknownProviderName_FallsBackToILlmProviderRegistration()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "model");
        ILlmProvider fallback = BuildStub("Fallback", "model");

        IServiceProvider sp = Substitute.For<IServiceProvider>();
        // Concrete-type lookups will return null (default for unconfigured Substitute);
        // resolver should fall back to ILlmProvider lookup.
        sp.GetService(typeof(ILlmProvider)).Returns(fallback);

        SummaryLlmResolver resolver = new(options, sp);
        SummarySettings settings = new() { LlmProvider = "DefinitelyNotARealProvider" };
        ILlmProvider? result = resolver.Resolve(settings);

        result.Should().BeSameAs(fallback);
    }

    [Fact]
    public void Resolve_ConcreteTypeUnregistered_FallsBackToILlmProvider()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "model");
        ILlmProvider fallback = BuildStub("Fallback", "model");

        IServiceProvider sp = Substitute.For<IServiceProvider>();
        // AnthropicLlmProvider lookup returns null; resolver should fall back.
        sp.GetService(typeof(ILlmProvider)).Returns(fallback);

        SummaryLlmResolver resolver = new(options, sp);
        ILlmProvider? result = resolver.Resolve(summarySettings: null);

        result.Should().BeSameAs(fallback);
        sp.Received(1).GetService(typeof(AnthropicLlmProvider));
        sp.Received(1).GetService(typeof(ILlmProvider));
    }

    [Fact]
    public void Resolve_NothingRegistered_ReturnsNull()
    {
        IOptionsMonitor<LlmSettings> options = BuildOptions("Anthropic", "model");
        IServiceProvider sp = Substitute.For<IServiceProvider>(); // all GetService calls return null

        SummaryLlmResolver resolver = new(options, sp);
        ILlmProvider? result = resolver.Resolve(summarySettings: null);

        result.Should().BeNull();
    }
}
