using Connapse.Core;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Systems;

[Trait("Category", "Unit")]
public class SystemConfigTests
{
    [Theory]
    [InlineData("ConnectionStrings:DefaultConnection")]
    [InlineData("connectionstrings:defaultconnection")]
    [InlineData("Knowledge:Storage:MinIO:Endpoint")]
    [InlineData("Identity:Jwt:Secret")]
    [InlineData("CONNAPSE_ADMIN_EMAIL")]
    [InlineData("RateLimiting:ApiPermitLimit")]
    public void Construct_InfrastructureSettingKey_ThrowsArgumentException(string key)
    {
        Action act = () => new SystemConfig("name", SearchMode.Hybrid, new Dictionary<string, string> { [key] = "x" });

        act.Should().Throw<ArgumentException>().WithMessage($"*{key}*");
    }

    [Fact]
    public void Construct_SearchOrEmbeddingSettingKey_Allowed()
    {
        SystemConfig config = new("name", SearchMode.Hybrid, new Dictionary<string, string> { ["Knowledge:Search:FusionAlpha"] = "0.5" });

        config.Settings.Should().ContainKey("Knowledge:Search:FusionAlpha");
    }

    [Fact]
    public void Hash_ChunkingStrategy_ChangesTheHashOnlyWhenSet()
    {
        SystemConfig plain = new("c", SearchMode.Keyword, new Dictionary<string, string>());
        SystemConfig recursive = plain with { ChunkingStrategy = "Recursive" };
        SystemConfig semantic = plain with { ChunkingStrategy = "Semantic" };

        recursive.Hash.Should().NotBe(plain.Hash).And.NotBe(semantic.Hash);
        (plain with { ChunkingStrategy = null }).Hash.Should().Be(plain.Hash);
    }

    // #667: configs that differ only in search-time settings share one index.
    [Fact]
    public void IndexKey_SearchTimeSettingsAndModeDiffer_IsShared()
    {
        SystemConfig a = new("a", SearchMode.Hybrid, new Dictionary<string, string> { ["Knowledge:Search:FusionAlpha"] = "0.5" });
        SystemConfig b = new("b", SearchMode.Keyword, new Dictionary<string, string>
        {
            ["knowledge:search:reranker"] = "CrossEncoder",
            ["Knowledge:Search:RerankCandidates"] = "50",
        }) { CaptureCandidates = 100 };

        a.IndexKey.Should().Be(b.IndexKey);
    }

    [Theory]
    [InlineData("Knowledge:Embedding:Model", "qwen3-embedding:0.6b")]
    [InlineData("Knowledge:Chunking:MaxChunkSize", "256")]
    [InlineData("Knowledge:Search:VectorIndexMinVectors", "1000")]
    [InlineData("Knowledge:Search:SomethingNew", "x")]
    public void IndexKey_IndexTimeOrUnknownSettingDiffers_IsNotShared(string key, string value)
    {
        SystemConfig plain = new("a", SearchMode.Hybrid, new Dictionary<string, string>());
        SystemConfig other = new("b", SearchMode.Hybrid, new Dictionary<string, string> { [key] = value });

        other.IndexKey.Should().NotBe(plain.IndexKey);
    }

    [Fact]
    public void IndexKey_ChunkingStrategyDiffers_IsNotShared()
    {
        SystemConfig plain = new("a", SearchMode.Hybrid, new Dictionary<string, string>());

        (plain with { ChunkingStrategy = "Recursive" }).IndexKey.Should().NotBe(plain.IndexKey);
    }

    [Fact]
    public void IndexOnly_KeepsIndexTimeSettingsAndDropsSearchTimeOnes()
    {
        SystemConfig config = new("a", SearchMode.Hybrid, new Dictionary<string, string>
        {
            ["Knowledge:Embedding:Model"] = "m",
            ["Knowledge:Search:FusionAlpha"] = "0.5",
        }) { ChunkingStrategy = "Recursive", CaptureCandidates = 100 };

        SystemConfig indexOnly = config.IndexOnly();

        indexOnly.Settings.Should().Equal(new Dictionary<string, string> { ["Knowledge:Embedding:Model"] = "m" });
        indexOnly.ChunkingStrategy.Should().Be("Recursive");
        indexOnly.CaptureCandidates.Should().BeNull();
        config.SearchTimeSettings.Should().Equal(new Dictionary<string, string> { ["Knowledge:Search:FusionAlpha"] = "0.5" });
    }

    [Fact]
    public void Differences_NullAndEmptyStringsAreEqualAndKeysAreNotPrinted()
    {
        SearchSettings expected = new() { CrossEncoderApiKey = null, FusionAlpha = 0.75f };
        SearchSettings actual = expected with { CrossEncoderApiKey = "" };

        ConnapseSearchSystem.Differences(expected, actual).Should().BeEmpty();
        ConnapseSearchSystem.Differences(expected, actual with { FusionAlpha = 0.65f, CrossEncoderApiKey = "secret" })
            .Should().Equal("FusionAlpha: 0.75 → 0.65", "CrossEncoderApiKey differs");
    }
}
