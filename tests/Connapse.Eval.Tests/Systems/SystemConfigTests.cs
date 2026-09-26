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
}
