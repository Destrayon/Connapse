using System.Security.Cryptography;

namespace Connapse.CustomSource.Example;

public sealed record JournalEntry(string Id, bool Deleted);

/// <summary>One page of the journal: either more pages follow (<c>NextPageToken</c>) or this is the last (<c>NextCursor</c>).</summary>
public sealed record ChangeWindow(IReadOnlyList<JournalEntry> Entries, string? NextPageToken, string? NextCursor);

/// <summary>
/// The example system has no change log of its own, so this builds one: each call rescans the scope, diffs
/// it against the last snapshot, and appends what changed. A cursor is <c>{epoch}.{sequence}</c>; the epoch
/// is new on every start, so a cursor from before a restart is refused with 410 and Connapse re-lists.
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
            return Format(_entries[scopeId].Count);
        }
    }

    /// <summary>Null when the cursor was not issued by this process; the caller answers 410.</summary>
    public ChangeWindow? Read(string scopeId, string cursor)
    {
        lock (_gate)
        {
            Refresh(scopeId);
            List<JournalEntry> entries = _entries[scopeId];
            if (!TryParse(cursor, out int sequence) || sequence > entries.Count) return null;

            int end = Math.Min(sequence + config.PageSize, entries.Count);
            List<JournalEntry> slice = entries.GetRange(sequence, end - sequence);
            return end < entries.Count
                ? new ChangeWindow(slice, Format(end), null)
                : new ChangeWindow(slice, null, Format(end));
        }
    }

    private void Refresh(string scopeId)
    {
        Dictionary<string, string> current = catalog.List(scopeId)
            .ToDictionary(d => d.Id, d => $"{d.Version}|{d.AccessVersion}", StringComparer.Ordinal);

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
                entries.Add(new JournalEntry(id, Deleted: false));
        }
        foreach (string id in previous.Keys.Where(k => !current.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            entries.Add(new JournalEntry(id, Deleted: true));

        _snapshots[scopeId] = current;
    }

    private string Format(int sequence) => $"{_epoch}.{sequence}";

    private bool TryParse(string cursor, out int sequence)
    {
        sequence = 0;
        int dot = cursor.IndexOf('.');
        return dot > 0
            && cursor[..dot] == _epoch
            && int.TryParse(cursor[(dot + 1)..], out sequence)
            && sequence >= 0;
    }
}
