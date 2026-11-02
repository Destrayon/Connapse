using Connapse.Application;

namespace Connapse.Web.Mcp;

/// <summary>
/// How dangerous an MCP tool is, on the same tiers as operations. A Destructive or Credential tool
/// called by a token that requires approval doesn't run until a person approves it (#782).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class McpToolRiskAttribute(RiskTier risk) : Attribute
{
    public RiskTier Risk { get; } = risk;

    public bool NeedsApproval => Risk is RiskTier.Destructive or RiskTier.Credential;
}
