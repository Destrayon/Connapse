namespace Connapse.Web.Endpoints;

/// <summary>What GET /api/v1/info tells a client: which edition this is, its version, and its features.</summary>
public sealed record ServerInfo(string Edition, string Version, IReadOnlyList<string> Features);
