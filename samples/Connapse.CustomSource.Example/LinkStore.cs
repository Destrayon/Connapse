using System.Text.Json;

namespace Connapse.CustomSource.Example;

/// <summary>
/// Which example-system user each Connapse user is linked to. This is the middle man's own storage —
/// Connapse keeps no copy — persisted to <c>links.json</c>, with <c>preset_links</c> from
/// <c>example.json</c> added at start.
/// </summary>
public sealed class LinkStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _links;

    public LinkStore(ExampleDataDirectory data, ExampleConfig config, TimeProvider time)
    {
        Time = time;
        _path = Path.Combine(data.Path, "links.json");
        _links = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? []
            : [];
        foreach ((string connapseUserId, string user) in config.PresetLinks)
            _links.TryAdd(connapseUserId, user);
    }

    private TimeProvider Time { get; }

    public string? ExternalUser(string connapseUserId)
    {
        lock (_gate) return _links.GetValueOrDefault(connapseUserId);
    }

    public void Remove(string connapseUserId)
    {
        lock (_gate)
        {
            if (_links.Remove(connapseUserId)) Save();
        }
    }

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_links, ExampleConfig.JsonOptions));
}
