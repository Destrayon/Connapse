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
    /// Linked from restricted comments that disagree, from one Connapse cannot interpret, or from
    /// one whose restriction changed while it was read: indexed so that nobody sees it.
    /// </summary>
    public bool DenyAll { get; set; }
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
    public DateTimeOffset? CommentsReadAt { get; set; }
}

/// <summary>Sync bookkeeping for one project that is not part of any issue.</summary>
internal sealed class JiraSyncState
{
    public string ProjectName { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public List<string> RichTextFieldIds { get; set; } = [];        // from GET /field, refreshed each cycle
    public Dictionary<string, string> FieldNames { get; set; } = [];
    public Dictionary<string, string> AttachmentIssues { get; set; } = [];
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
