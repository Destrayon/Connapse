using System.Text.Json;
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests;

[Trait("Category", "Unit")]
public class SummarySettingsTests
{
    [Fact]
    public void DefaultInstance_HasOptInDisabledAndNullableOverrides()
    {
        SummarySettings settings = new();

        settings.Enabled.Should().BeFalse("opt-in by default");
        settings.LlmProvider.Should().BeNull();
        settings.LlmModel.Should().BeNull();
        settings.PerDocSystemPrompt.Should().BeNull();
        settings.ContainerRollupSystemPrompt.Should().BeNull();
        settings.MaxInputTokens.Should().BeNull();
    }

    [Fact]
    public void SerializesAndRoundTripsAllFields()
    {
        SummarySettings original = new()
        {
            Enabled = true,
            LlmProvider = "Anthropic",
            LlmModel = "claude-haiku-4-5",
            PerDocSystemPrompt = "Custom per-doc prompt.",
            ContainerRollupSystemPrompt = "Custom container prompt.",
            MaxInputTokens = 8000
        };

        string json = JsonSerializer.Serialize(original);
        SummarySettings? round = JsonSerializer.Deserialize<SummarySettings>(json);

        round.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void DeserializesPartialJsonWithDefaultsForMissingFields()
    {
        string json = """{ "enabled": true, "llmProvider": "Ollama" }""";
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        SummarySettings? settings = JsonSerializer.Deserialize<SummarySettings>(json, opts);

        settings.Should().NotBeNull();
        settings!.Enabled.Should().BeTrue();
        settings.LlmProvider.Should().Be("Ollama");
        settings.LlmModel.Should().BeNull();
        settings.PerDocSystemPrompt.Should().BeNull();
        settings.MaxInputTokens.Should().BeNull();
    }
}
