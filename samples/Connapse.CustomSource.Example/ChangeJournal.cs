using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Connapse.CustomSource.Example;

/// <summary>A change as seen when it was detected: the document then, or null for a deletion.</summary>
public sealed record JournalEntry(string Id, ExampleDocument? Document);

/// <summary>One page of the journal: either more pages follow (<c>NextPageToken</c>) or this is the last (<c>NextCursor</c>).</summary>
public sealed record ChangeWindow(IReadOnlyList<JournalEntry> Entries, string? NextPageToken, string? NextCursor);

/// <summary>
/// The example system has no change log of its own, so this builds one: each call rescans the scope, diffs
/// it against the last snapshot, and appends what changed. A cursor is <c>{epoch}.{scope}.{sequence}</c>; the
/// epoch is new on every start, so a cursor from before a restart is refused with 410 and Connapse re-lists,
/// and the scope is checked, so a cursor issued for one scope cannot skip another scope's changes.
/// </summary>
public sealed class ChangeJournal(FolderCatalog catalog, ExampleConfig config)
{
    private readonly string _epoch = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JournalEntry>> _entries = new(StringComparer.Ordinal);

    public string HeadCursor(string scopeId)
    {
        lock (_gate)
        {
            Refresh(scopeId);
            return Format(scopeId, _entries[scopeId].Count);
        }
    }

    /// <summary>Null when the cursor was not issued by this process; the caller answers 410.</summary>
    public ChangeWindow? Read(string scopeId, string cursor)
    {
        lock (_gate)
        {
            Refresh(scopeId);
            List<JournalEntry> entries = _entries[scopeId];
            if (!TryParse(scopeId, cursor, out int sequence) || sequence > entries.Count) return null;

            int end = Math.Min(sequence + config.PageSize, entries.Count);
            List<JournalEntry> slice = entries.GetRange(sequence, end - sequence);
            return end < entries.Count
                ? new ChangeWindow(slice, Format(scopeId, end), null)
                : new ChangeWindow(slice, null, Format(scopeId, end));
        }
    }

    private void Refresh(string scopeId)
    {
        Dictionary<string, ExampleDocument> documents = catalog.List(scopeId)
            .ToDictionary(d => d.Id, StringComparer.Ordinal);
        Dictionary<string, string> current = documents
            .ToDictionary(p => p.Key, p => $"{p.Value.Version}|{p.Value.AccessVersion}", StringComparer.Ordinal);

        if (!_snapshots.TryGetValue(scopeId, out Dictionary<string, string>? previous))
        {
            _snapshots[scopeId] = current;
            _entries[scopeId] = [];
            return;
        }

        List<JournalEntry> entries = _entries[scopeId];
        foreach ((string id, string stamp) in current.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!previous.TryGetValue(id, out string? old) || old != stamp)
                entries.Add(new JournalEntry(id, documents[id]));
        }
        foreach (string id in previous.Keys.Where(k => !current.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            entries.Add(new JournalEntry(id, Document: null));

        _snapshots[scopeId] = current;
    }

    private string Format(string scopeId, int sequence) => $"{_epoch}.{ScopeTag(scopeId)}.{sequence}";

    private bool TryParse(string scopeId, string cursor, out int sequence)
    {
        sequence = 0;
        string[] parts = cursor.Split('.');
        return parts.Length == 3
            && parts[0] == _epoch
            && parts[1] == ScopeTag(scopeId)
            && int.TryParse(parts[2], out sequence)
            && sequence >= 0;
    }

    private static string ScopeTag(string scopeId) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(scopeId));
}
