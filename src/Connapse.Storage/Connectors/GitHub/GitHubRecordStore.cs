using System.Text.Json;

namespace Connapse.Storage.Connectors.GitHub;

/// <summary>One comment as kept in a record: an issue comment, or a review comment on a file.</summary>
internal sealed record GitHubStoredComment(
    string Login,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ReviewPath,
    bool Minimized);

/// <summary>
/// Everything known about one issue or pull request. Assembled from separate sweeps, so any part
/// can arrive first: a comment can be seen before its issue, and a sub-issue's parent before the
/// sub-issue itself.
/// </summary>
internal sealed class GitHubStoredRecord
{
    public int Number { get; set; }

    /// <summary>Null until the issues sweep reaches it; a record without one is not emitted.</summary>
    public GitHubIssue? Issue { get; set; }

    /// <summary>Keyed <c>c{id}</c> for issue comments and <c>r{id}</c> for review comments — separate id spaces.</summary>
    public Dictionary<string, GitHubStoredComment> Comments { get; set; } = [];

    public int? Parent { get; set; }

    public List<int> Children { get; set; } = [];

    /// <summary>
    /// When <see cref="Parent"/> or <see cref="Children"/> last changed. Part of the record's
    /// modification time: a sub-issue moved between parents changes the rendered record without
    /// touching its own <c>updated_at</c>, and the sync engine only re-ingests what it sees change.
    /// </summary>
    public DateTimeOffset? EdgesChangedAt { get; set; }

    public int IssueCommentCount => Comments.Keys.Count(k => k.StartsWith('c'));
}

/// <summary>Sync bookkeeping that must survive between cycles but is not part of any record.</summary>
internal sealed class GitHubRecordState
{
    /// <summary>Records changed since they were last handed to the sync engine.</summary>
    public HashSet<int> Dirty { get; set; } = [];

    /// <summary>Paths of records found deleted upstream, not yet handed to the sync engine.</summary>
    public HashSet<string> PendingDeletes { get; set; } = [];

    /// <summary>
    /// The sequence number of the last emission, and what it carried. Cleared from
    /// <see cref="Dirty"/> only once the engine comes back with a cursor bearing this number —
    /// proof it stored the cycle — so an engine failure re-emits instead of losing them.
    /// </summary>
    public long EmittedSeq { get; set; }

    public List<int> EmittedUpserts { get; set; } = [];

    public List<string> EmittedDeletes { get; set; } = [];

    public DateTimeOffset? LastRelistAt { get; set; }

    /// <summary>When both comment sweeps last ran to completion; an idle repository re-runs them hourly.</summary>
    public DateTimeOffset? LastCommentSweepAt { get; set; }

    /// <summary>
    /// Records whose stored issue comments outnumber what GitHub reports — a deletion no sweep
    /// shows. Kept here rather than per cycle, so a budget that runs out mid-refetch does not
    /// forget them.
    /// </summary>
    public HashSet<int> Suspects { get; set; } = [];

    /// <summary>
    /// Set by a fresh start and cleared once the engine acknowledges the first complete emission,
    /// which is sent as a full listing so the engine deletes what the store no longer has.
    /// </summary>
    public bool InitialListingPending { get; set; }

    /// <summary>Whether the emission awaiting acknowledgement was that full listing.</summary>
    public bool EmittedFull { get; set; }

    /// <summary>
    /// ETags from the last completed probes: the repository, and the newest issue, issue comment,
    /// and review comment. Each is kept only once the sweep it gates has finished, so a probe can
    /// never report "unchanged" for work a stopped cycle left undone.
    /// </summary>
    public string? RepositoryETag { get; set; }

    public string? IssuesETag { get; set; }

    public string? CommentsETag { get; set; }

    public string? ReviewCommentsETag { get; set; }
}

/// <summary>
/// The issues source's local copy of its repository's records: one JSON file per record plus a
/// state file, under the source's mirror directory.
/// <para>
/// Kept for the same reason as the docs mirror: the pipeline reads each document through a fresh
/// connector, and reading from GitHub there would spend one anonymous request per record. The
/// sync is the only writer (the per-source gate serializes it), but the pipeline reads while it
/// writes, so every write lands whole via a rename.
/// </para>
/// </summary>
internal sealed class GitHubRecordStore(string root)
{
    private string RecordsDir => Path.Combine(root, "records");

    private string StatePath => Path.Combine(root, "state.json");

    public bool Exists => File.Exists(StatePath);

    /// <summary>Discards every record and starts a fresh state. Used when the cursor is null.</summary>
    public void Reset()
    {
        if (Directory.Exists(RecordsDir))
            Directory.Delete(RecordsDir, recursive: true);

        Directory.CreateDirectory(RecordsDir);
        SaveState(new GitHubRecordState { InitialListingPending = true });
    }

    public GitHubStoredRecord? Load(int number)
    {
        string path = RecordPath(number);
        if (!File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<GitHubStoredRecord>(stream, GitHubApiClient.Json);
    }

    public void Save(GitHubStoredRecord record) =>
        WriteAtomically(RecordPath(record.Number), record);

    public void Delete(int number) => File.Delete(RecordPath(number));

    public IEnumerable<int> Numbers() =>
        Directory.Exists(RecordsDir)
            ? Directory.EnumerateFiles(RecordsDir, "*.json")
                .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out int n) ? n : -1)
                .Where(n => n > 0)
            : [];

    public GitHubRecordState LoadState()
    {
        using var stream = File.OpenRead(StatePath);
        return JsonSerializer.Deserialize<GitHubRecordState>(stream, GitHubApiClient.Json) ?? new();
    }

    public void SaveState(GitHubRecordState state) => WriteAtomically(StatePath, state);

    private string RecordPath(int number) => Path.Combine(RecordsDir, $"{number}.json");

    private static void WriteAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, GitHubApiClient.Json));
        File.Move(temp, path, overwrite: true);
    }
}
