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
}
