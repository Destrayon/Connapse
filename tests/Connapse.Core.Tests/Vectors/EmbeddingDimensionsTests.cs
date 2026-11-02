using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Utilities;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Vectors;

/// <summary>
/// Whether the OpenAI and Azure OpenAI providers ask the model for the configured size, and what they do
/// when the vectors that come back are another size (#666).
/// </summary>
[Trait("Category", "Unit")]
public class EmbeddingDimensionsTests
{
    public static TheoryData<string> Providers => new() { "OpenAI", "AzureOpenAI" };

    private static IEmbeddingProvider Create(string provider, EmbeddingSettings settings, FakeEmbeddingEndpoint endpoint)
    {
        settings.Provider = provider;
        var snapshot = new TestOptionsSnapshot<EmbeddingSettings>(settings);
        return provider == "OpenAI"
            ? new OpenAiEmbeddingProvider(snapshot, Substitute.For<ILogger<OpenAiEmbeddingProvider>>(), endpoint.Transport)
            : new AzureOpenAiEmbeddingProvider(snapshot, Substitute.For<ILogger<AzureOpenAiEmbeddingProvider>>(), endpoint.Transport);
    }

    private static EmbeddingSettings Settings(string model, int dimensions, bool sendDimensions = false) => new()
    {
        Model = model,
        Dimensions = dimensions,
        SendDimensions = sendDimensions,
        UseModelPrefixes = false,
        OpenAiApiKey = "sk-test",
        OpenAiBaseUrl = "https://compatible.example.com/v1",
        AzureApiKey = "azure-test",
        AzureEndpoint = "https://test.openai.azure.com",
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EmbedAsync_OtherModelWithSendDimensions_SendsConfiguredSize(string provider)
    {
        var endpoint = new FakeEmbeddingEndpoint { ReturnedLength = 1536 };
        var embedder = Create(provider, Settings("Qwen/Qwen3-Embedding-8B", 1536, sendDimensions: true), endpoint);

        float[] vector = await embedder.EmbedAsync("hello", EmbeddingInputType.Document);

        vector.Should().HaveCount(1536);
        endpoint.Requests.Should().ContainSingle()
            .Which["dimensions"]!.GetValue<int>().Should().Be(1536);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EmbedAsync_OtherModelWithoutSendDimensions_DoesNotSendSize(string provider)
    {
        var endpoint = new FakeEmbeddingEndpoint { ReturnedLength = 1024 };
        var embedder = Create(provider, Settings("bge-large-en", 1024), endpoint);

        await embedder.EmbedAsync("hello", EmbeddingInputType.Document);

        endpoint.Requests.Should().ContainSingle().Which.ContainsKey("dimensions").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EmbedAsync_TextEmbedding3WithoutSendDimensions_StillSendsSize(string provider)
    {
        var endpoint = new FakeEmbeddingEndpoint { ReturnedLength = 512 };
        var embedder = Create(provider, Settings("text-embedding-3-small", 512), endpoint);

        await embedder.EmbedAsync("hello", EmbeddingInputType.Document);

        endpoint.Requests.Should().ContainSingle()
            .Which["dimensions"]!.GetValue<int>().Should().Be(512);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EmbedAsync_NoSizeConfigured_SendsNoSizeAndAcceptsAnyLength(string provider)
    {
        var endpoint = new FakeEmbeddingEndpoint { ReturnedLength = 4096 };
        var embedder = Create(provider, Settings("Qwen/Qwen3-Embedding-8B", 0, sendDimensions: true), endpoint);

        float[] vector = await embedder.EmbedAsync("hello", EmbeddingInputType.Document);

        vector.Should().HaveCount(4096);
        endpoint.Requests.Should().ContainSingle().Which.ContainsKey("dimensions").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EmbedBatchAsync_ModelIgnoresSize_ThrowsDimensionMismatch(string provider)
    {
        var endpoint = new FakeEmbeddingEndpoint { ReturnedLength = 4096 };
        var embedder = Create(provider, Settings("Qwen/Qwen3-Embedding-8B", 1536, sendDimensions: true), endpoint);

        var act = () => embedder.EmbedBatchAsync(["one", "two"], EmbeddingInputType.Document);

        var thrown = await act.Should().ThrowAsync<EmbeddingDimensionMismatchException>()
            .WithMessage("*returned 4096-dimension vectors*Dimensions is 1536*asked for 1536 and ignored it*");
        thrown.Which.Expected.Should().Be(1536);
        thrown.Which.Actual.Should().Be(4096);
        thrown.Which.ModelId.Should().Be("Qwen/Qwen3-Embedding-8B");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EmbedAsync_SizeNotSentAndWrong_ThrowsMismatchSuggestingTheSwitch(string provider)
    {
        var endpoint = new FakeEmbeddingEndpoint { ReturnedLength = 4096 };
        var embedder = Create(provider, Settings("Qwen/Qwen3-Embedding-8B", 1536), endpoint);

        var act = () => embedder.EmbedAsync("hello", EmbeddingInputType.Query);

        await act.Should().ThrowAsync<EmbeddingDimensionMismatchException>()
            .WithMessage("*Set Dimensions to 4096*Ask the model for this size*");
    }

    [Fact]
    public void Deserialize_SettingsSavedBeforeSendDimensions_LeaveItOff()
    {
        const string saved = """{"provider":"OpenAI","model":"Qwen/Qwen3-Embedding-8B","dimensions":1536}""";

        var settings = JsonSerializer.Deserialize<EmbeddingSettings>(saved, JsonSerializerOptions.Web)!;

        settings.SendDimensions.Should().BeFalse();
        settings.Dimensions.Should().Be(1536);
    }
}
