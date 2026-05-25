using Connapse.Storage.Llm;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.Llm;

[Trait("Category", "Unit")]
public class ModelPricingTests
{
    [Theory]
    [InlineData("claude-haiku-4-5", 1.0, 5.0)]
    [InlineData("gpt-4.1-nano", 0.10, 0.40)]
    [InlineData("gemini-2.5-flash", 0.30, 2.50)]
    [InlineData("mistral-small-3", 0.10, 0.30)]
    public void GetPricing_ReturnsKnownModelRates(string modelId, decimal inPer1M, decimal outPer1M)
    {
        ModelPricing.Pricing pricing = ModelPricing.Get(modelId);
        pricing.InputPricePerMillionTokens.Should().Be(inPer1M);
        pricing.OutputPricePerMillionTokens.Should().Be(outPer1M);
    }

    [Fact]
    public void GetPricing_UnknownModel_ReturnsZero()
    {
        ModelPricing.Pricing pricing = ModelPricing.Get("nonexistent-model");
        pricing.InputPricePerMillionTokens.Should().Be(0);
        pricing.OutputPricePerMillionTokens.Should().Be(0);
    }

    [Fact]
    public void EstimateCost_ComputesExpectedUsd()
    {
        // Haiku 4.5: 5000 input tokens × $1/M + 100 output tokens × $5/M
        // = 0.005 + 0.0005 = $0.0055
        decimal cost = ModelPricing.EstimateCostUsd("claude-haiku-4-5", 5000, 100);
        cost.Should().BeApproximately(0.0055m, 0.0001m);
    }
}
