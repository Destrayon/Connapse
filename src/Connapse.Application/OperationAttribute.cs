namespace Connapse.Application;

/// <summary>What an operation can do, from least to most dangerous. Unspecified is never valid.</summary>
public enum RiskTier
{
    Unspecified,
    Read,
    Write,
    Destructive,
    Credential,
}

/// <summary>Whether MCP may reach an operation. Off unless an operation says otherwise.</summary>
public enum McpExposure
{
    Off,

    /// <summary>Reachable through the generic connapse_api_* MCP tools, within the token's scopes.</summary>
    Generic,
}

/// <summary>
/// Marks an operation class: its id (as in the capability manifest, e.g. "sources.create"), the
/// scope a token needs, its risk, and its MCP exposure. Endpoint helpers, the MCP registration
/// (default off) and the parity tests read it; EveryOperation_HasScopeAndRisk requires all of it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OperationAttribute(string id) : Attribute
{
    public string Id { get; } = id;

    public string Scope { get; set; } = "";

    public RiskTier Risk { get; set; } = RiskTier.Unspecified;

    public McpExposure Mcp { get; set; } = McpExposure.Off;
}
