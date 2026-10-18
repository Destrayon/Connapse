using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace Connapse.CustomSource.Example;

/// <summary>A browser that presented a valid link token and has not yet signed in to the example system.</summary>
public sealed record LinkSession(string ConnapseUserId, string ReturnUrl, DateTimeOffset ExpiresAt);

/// <summary>A finished sign-in waiting for Connapse to confirm it is the same Connapse user.</summary>
public sealed record PendingLink(string ConnapseUserId, string ExternalUser, DateTimeOffset ExpiresAt);

/// <summary>
/// Which example-system user each Connapse user is linked to. This is the middle man's own storage —
/// Connapse keeps no copy — persisted to <c>links.json</c>, with <c>preset_links</c> from
/// <c>example.json</c> added at start.
/// </summary>
public sealed class LinkStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, string> _links;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, LinkSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingLink> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _usedTokenIds = new(StringComparer.Ordinal);

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
            if (!_links.ContainsKey(connapseUserId)) return;

            Dictionary<string, string> next = new(_links, StringComparer.Ordinal);
            next.Remove(connapseUserId);
            Commit(next);
        }
    }

    /// <summary>False when this token ID was already used. Expired IDs are forgotten.</summary>
    public bool TryUseTokenId(string tokenId, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            DateTimeOffset now = Time.GetUtcNow();
            foreach (string stale in _usedTokenIds.Where(p => p.Value < now - Lifetime).Select(p => p.Key).ToList())
                _usedTokenIds.Remove(stale);
            return _usedTokenIds.TryAdd(tokenId, expiresAt);
        }
    }

    public string StartSession(string connapseUserId, string returnUrl)
    {
        string state = NewSecret();
        lock (_gate) _sessions[state] = new LinkSession(connapseUserId, returnUrl, Time.GetUtcNow() + Lifetime);
        return state;
    }

    public LinkSession? TakeSession(string state)
    {
        lock (_gate)
        {
            return _sessions.Remove(state, out LinkSession? session) && session.ExpiresAt > Time.GetUtcNow()
                ? session
                : null;
        }
    }

    public string AddPending(string connapseUserId, string externalUser)
    {
        string code = NewSecret();
        lock (_gate) _pending[code] = new PendingLink(connapseUserId, externalUser, Time.GetUtcNow() + Lifetime);
        return code;
    }

    /// <summary>
    /// Activates the pending link only for the Connapse user it was started for. The code is consumed
    /// either way, so a wrong guess cannot be followed by the right one.
    /// </summary>
    public bool Confirm(string code, string connapseUserId)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(code, out PendingLink? pending)) return false;
            if (pending.ConnapseUserId != connapseUserId || pending.ExpiresAt <= Time.GetUtcNow())
            {
                _pending.Remove(code);
                return false;
            }

            // The code is spent only once the link is saved, so a failed save can be retried.
            Commit(new Dictionary<string, string>(_links, StringComparer.Ordinal) { [connapseUserId] = pending.ExternalUser });
            _pending.Remove(code);
            return true;
        }
    }

    private static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Saves <paramref name="next"/> and only then makes it current. Written to a temporary file and moved
    /// over <c>links.json</c>, so a failed save changes nothing — neither memory nor disk — and a crash
    /// mid-write cannot leave a half-written file.
    /// </summary>
    private void Commit(Dictionary<string, string> next)
    {
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next, ExampleConfig.JsonOptions));
        File.Move(temporary, _path, overwrite: true);
        _links = next;
    }
}
