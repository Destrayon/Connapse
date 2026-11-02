using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Connapse.Core.Tests.Vectors;

/// <summary>
/// Settings > Embedding > Test connection asks the model for the size ingestion asks for, and says so when the
/// vectors come back another size instead of reporting success (#666).
/// </summary>
[Trait("Category", "Unit")]
public class ConnectionTesterDimensionsTests
{
    public static TheoryData<string> Providers => new() { "OpenAI", "AzureOpenAI" };

    private static IConnectionTester Create(string provider, FakeEmbeddingEndpoint endpoint) =>
        provider == "OpenAI"
            ? new OpenAiConnectionTester(Substitute.For<ILogger<OpenAiConnectionTester>>(), endpoint.Transport)
            : new AzureOpenAiConnectionTester(Substitute.For<ILogger<AzureOpenAiConnectionTester>>(), endpoint.Transport);

    private static EmbeddingSettings Qwen(string provider, bool sendDimensions) => new()
    {
        Provider = provider,
        Model = "Qwen/Qwen3-Embedding-8B",
        Dimensions = 1536,
        SendDimensions = sendDimensions,
        OpenAiApiKey = "sk-test",
        OpenAiBaseUrl = "https://compatible.example.com/v1",
        AzureApiKey = "azure-test",
        AzureEndpoint = "https://test.openai.azure.com",
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TestConnection_AskTheModelForThisSizeOn_SendsDimensionsAndReportsTheConfiguredSize(string provider)
    {
        var endpoint = new FakeEmbeddingEndpoint { ReturnedLength = 4096, HonorsDimensions = true };

        ConnectionTestResult result = await Create(provider, endpoint).TestConnectionAsync(Qwen(provider, sendDimensions: true));

        result.Success.Should().BeTrue(result.Message);
        result.Details!["dimensions"].Should().Be(1536);
        endpoint.Requests.Should().ContainSingle()
            .Which["dimensions"]!.GetValue<int>().Should().Be(1536);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TestConnection_SizeDiffersFromDimensions_FailsNamingBothSizesAndTheSetting(string provider)
    {
        var endpoint = new FakeEmbeddingEndpoint { ReturnedLength = 4096, HonorsDimensions = true };

        ConnectionTestResult result = await Create(provider, endpoint).TestConnectionAsync(Qwen(provider, sendDimensions: false));

        endpoint.Requests.Should().ContainSingle().Which.ContainsKey("dimensions").Should().BeFalse();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("returned 4096-dimension vectors").And.Contain("Dimensions is 1536")
            .And.Contain("Ask the model for this size");
        result.Details!["dimensions"].Should().Be(4096);
        result.Details["expectedDimensions"].Should().Be(1536);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TestConnection_EndpointIgnoresTheAskedForSize_FailsSayingTheModelIgnoredIt(string provider)
    {
        var endpoint = new FakeEmbeddingEndpoint { ReturnedLength = 4096 };

        ConnectionTestResult result = await Create(provider, endpoint).TestConnectionAsync(Qwen(provider, sendDimensions: true));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("asked for 1536 and ignored it");
    }
}
