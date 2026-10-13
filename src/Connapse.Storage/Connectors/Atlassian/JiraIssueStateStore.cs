using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>A comment as last read. Only its restriction and time are kept; the text is read with the issue.</summary>
internal sealed class JiraStoredComment
{
    public string Id { get; set; } = "";
    public DateTimeOffset Updated { get; set; }
    public string? VisibilityType { get; set; }      // "group" | "role" | null
    public string? VisibilityIdentifier { get; set; } // groupId, or role name
    public bool Internal { get; set; }                // jsdPublic == false

    /// <summary>No group or role visibility and not a JSM internal note: rendered inside the issue document.</summary>
    [JsonIgnore]
    public bool IsPublic => VisibilityType is null && !Internal;
}

/// <summary>An attachment of an issue as last fetched.</summary>
internal sealed class JiraStoredAttachment
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? MediaType { get; set; }
    public long Size { get; set; }
    public DateTimeOffset Created { get; set; }
    public string? RestrictingCommentId { get; set; }

    /// <summary>
    /// Every restricted comment that has ever linked the attachment, or may have shown it (one
    /// whose files Connapse cannot name), carried over from record to record. While one of them
    /// is missing, or is restricted otherwise than the tie, the attachment is deny-all.
    /// </summary>
    public List<string> TiedBy { get; set; } = [];

    /// <summary>
    /// Linked from restricted comments that disagree, from one Connapse cannot interpret, or from
    /// one whose restriction changed while it was read: indexed so that nobody sees it.
    /// </summary>
    public bool DenyAll { get; set; }
}

/// <summary>
/// A restricted comment, internal note, or attachment tied to one, that Jira's answer for its issue
/// left out. It is listed with a deny-all URI, under its last signature, until a cycle after the
/// one that recorded it has been taken up: a deletion the sync engine withholds then leaves a
/// document nobody can see, not one under a restriction Jira no longer applies.
/// </summary>
internal sealed class JiraStoredTombstone
{
    public string Id { get; set; } = "";
    public string? AttachmentFileName { get; set; }   // null for a comment
    public bool Internal { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset LastModified { get; set; }
    public string? ContentType { get; set; }
    public long RecordedMs { get; set; }               // the start of the cycle that recorded it
}

/// <summary>
/// What Connapse has ever learnt about one attachment's restriction, kept apart from the issue
/// records: a fetch that leaves the attachment out, an answer without the attachment field, or the
/// issue dropping out of the listing must not forget it. Dropped only with the source's state.
/// </summary>
internal sealed class JiraAttachmentHistory
{
    /// <summary>Every restricted comment that has ever tied the attachment, or may have shown it.</summary>
    public List<string> TiedBy { get; set; } = [];

    /// <summary>
    /// Deny-all until re-verified: released only by a cycle that reads the one comment in
    /// <see cref="TiedBy"/> and finds its restriction settled. More than one tie never releases.
    /// </summary>
    public bool DenyAll { get; set; }

    /// <summary>
    /// Deny-all for good: what made it deny-all names no governing comment Connapse can re-check.
    /// Set for a deny-all seeded from a record written before the history existed (which kept at
    /// most its first tie, not the comment that caused the deny-all) and for one seeded from an
    /// attachment tombstone. The comment behind it may stay hidden from the service account
    /// forever, so no surviving or later tie releases it.
    /// </summary>
    public bool Unresolvable { get; set; }
}

/// <summary>A comment's rendered read this cycle: whether it is restricted, and whether its restriction is settled.</summary>
/// <param name="Settled">Unchanged since the page read, not deny-all, and with no file reference Connapse cannot name.</param>
internal sealed record JiraCommentRead(bool Restricted, bool Settled);

/// <summary>The attachment history file: its format version and the entries by attachment id.</summary>
internal sealed class JiraAttachmentHistoryFile
{
    public int Version { get; set; }
    public Dictionary<string, JiraAttachmentHistory> Attachments { get; set; } = [];
}

/// <summary>What the last fetch said about one issue.</summary>
internal sealed class JiraStoredIssue
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTimeOffset Updated { get; set; }
    public List<JiraStoredComment> Comments { get; set; } = [];
    public List<JiraStoredAttachment> Attachments { get; set; } = [];
    public List<JiraStoredTombstone> Tombstones { get; set; } = [];
    public DateTimeOffset? CommentsReadAt { get; set; }

    /// <summary>This cycle's successful rendered reads of comments, by id. Never stored.</summary>
    [JsonIgnore]
    public Dictionary<string, JiraCommentRead> Reads { get; } = new(StringComparer.Ordinal);
}

/// <summary>Sync bookkeeping for one project that is not part of any issue.</summary>
internal sealed class JiraSyncState
{
    public string ProjectName { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public List<string> RichTextFieldIds { get; set; } = [];        // from GET /field, refreshed hourly and on each reconcile
    public Dictionary<string, string> FieldNames { get; set; } = [];
    public DateTimeOffset? FieldsReadAt { get; set; }                // when GET /field was last read

    /// <summary>The cycle id of the last delta returned; written before that cycle changes anything else.</summary>
    public string? LastCycleId { get; set; }
}

/// <summary>
/// A Jira project source's local record of its issues: one JSON file per issue plus a state file,
/// under the source's state directory. Kept for the reason <see cref="ConfluencePageStateStore"/>
/// is, and written the same way: the pipeline reads while the sync writes, so every write lands
/// whole via a rename.
/// </summary>
internal sealed class JiraIssueStateStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string IssuesDir => Path.Combine(root, "issues");

    private string StatePath => Path.Combine(root, "state.json");

    private string AttachmentHistoryPath => Path.Combine(root, "attachment-history.json");

    public JiraStoredIssue? Load(string issueId)
    {
        if (!IsId(issueId))
            return null;

        string path = IssuePath(issueId);
        if (!File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<JiraStoredIssue>(stream, Json);
    }

    public void Save(JiraStoredIssue issue) => WriteAtomically(IssuePath(issue.Id), issue);

    public void Delete(string issueId) => File.Delete(IssuePath(issueId));

    public IEnumerable<string> Ids() =>
        Directory.Exists(IssuesDir)
            ? Directory.EnumerateFiles(IssuesDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(IsId)
            : [];

    public JiraSyncState LoadState()
    {
        if (!File.Exists(StatePath))
            return new JiraSyncState();

        using var stream = File.OpenRead(StatePath);
        return JsonSerializer.Deserialize<JiraSyncState>(stream, Json) ?? new();
    }

    public void SaveState(JiraSyncState state) => WriteAtomically(StatePath, state);

    /// <summary>Records <paramref name="cycleId"/> as the last emitted cycle, leaving the rest of the stored state as it is.</summary>
    public void MarkCycle(string cycleId)
    {
        var state = LoadState();
        state.LastCycleId = cycleId;
        SaveState(state);
    }

    /// <summary>The history format this code writes. A file of an older one, or none, is seeded again from the issue records.</summary>
    public const int AttachmentHistoryVersion = 1;

    /// <summary>Whether the history file exists in the current format.</summary>
    public bool HasCurrentAttachmentHistory() => ReadAttachmentHistory() is { Version: >= AttachmentHistoryVersion };

    /// <summary>Each attachment's restriction history, by attachment id. Never pruned by a listing or an issue's deletion.</summary>
    public Dictionary<string, JiraAttachmentHistory> LoadAttachmentHistory() =>
        new(ReadAttachmentHistory()?.Attachments ?? [], StringComparer.Ordinal);

    public void SaveAttachmentHistory(Dictionary<string, JiraAttachmentHistory> history) =>
        WriteAtomically(AttachmentHistoryPath, new JiraAttachmentHistoryFile { Version = AttachmentHistoryVersion, Attachments = history });

    private JiraAttachmentHistoryFile? ReadAttachmentHistory()
    {
        if (!File.Exists(AttachmentHistoryPath))
            return null;

        using var stream = File.OpenRead(AttachmentHistoryPath);
        return JsonSerializer.Deserialize<JiraAttachmentHistoryFile>(stream, Json);
    }

    /// <summary>Jira issue, comment and project ids are digits; anything else never reaches a file name or a URL.</summary>
    public static bool IsId(string? id) =>
        id is { Length: > 0 and <= 19 } && id.All(char.IsAsciiDigit);

    private string IssuePath(string id) =>
        IsId(id)
            ? Path.Combine(IssuesDir, $"{id}.json")
            : throw new ArgumentException("Not a Jira issue id.", nameof(id));

    private static void WriteAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}
