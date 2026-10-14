using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connapse.CustomSource.Example;

/// <summary>The folder holding <c>example.json</c>, <c>links.json</c> and <c>scopes/</c>.</summary>
public sealed record ExampleDataDirectory(string Path);

public sealed record ScopeConfig(string Id, string Name, string? Description);

/// <summary>
/// Everything the example "target system" knows, read from <c>example.json</c>. Users are plain names;
/// grants are the access keys each user holds; denies are document paths (<c>scope/relative/path</c>) a
/// user may not read even though they hold the document's key — the per-document exceptions that only the
/// check can express.
/// </summary>
public sealed record ExampleConfig
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public required string Name { get; init; }
    public required string Token { get; init; }
    public int PageSize { get; init; } = 100;
    public bool PermissionsEnabled { get; init; } = true;
    public int MaxCheckBatch { get; init; } = 100;
    public string? LinkSecret { get; init; }
    public string? ConnapseIssuer { get; init; }
    public string? ProviderId { get; init; }
    public string? PublicBaseUrl { get; init; }
    public List<ScopeConfig> Scopes { get; init; } = [];
    public Dictionary<string, List<string>> Grants { get; init; } = [];
    public Dictionary<string, List<string>> Denies { get; init; } = [];
    public Dictionary<string, string> PresetLinks { get; init; } = [];
    public List<string> Users { get; init; } = [];

    public bool HasScope(string scopeId) => Scopes.Any(s => s.Id == scopeId);

    public static ExampleConfig Load(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "example.json");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ExampleConfig>(stream, JsonOptions)
            ?? throw new InvalidDataException($"{path} is empty.");
    }
}
