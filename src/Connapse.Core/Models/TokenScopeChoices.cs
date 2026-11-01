namespace Connapse.Core;

/// <summary>
/// The scopes the token forms offer. A token passes a scope check only with a scope it was issued with (#932),
/// so the forms ask for them and start with <see cref="Default"/>. A scope never lets a token do more than its
/// owner's role allows.
/// </summary>
public static class TokenScopeChoices
{
    public const string Default = "knowledge:read";

    /// <summary>What a person may give their own personal access token.</summary>
    public static readonly IReadOnlyList<string> PersonalToken =
        ["knowledge:read", "knowledge:write", "admin:users", "admin:audit", "admin:oauth-clients", "admin:settings", "admin:sources"];

    /// <summary>What an agent key may hold: an agent reaches Connapse over MCP only.</summary>
    public static readonly IReadOnlyList<string> AgentKey = ["knowledge:read", "knowledge:write"];

    /// <summary>
    /// A deprecated scope no check ever required: uploads and deletes need knowledge:write (CON-171). A request
    /// that still asks for it is accepted, so a client that worked keeps working, but the scope is never stored.
    /// </summary>
    public const string DeprecatedAgentIngest = "agent:ingest";

    /// <summary>The requested scopes without <see cref="DeprecatedAgentIngest"/>, which is dropped rather than refused.</summary>
    public static IEnumerable<string?> WithoutDeprecated(IEnumerable<string?> scopes) =>
        scopes.Where(s => !string.Equals(s, DeprecatedAgentIngest, StringComparison.Ordinal));
}
