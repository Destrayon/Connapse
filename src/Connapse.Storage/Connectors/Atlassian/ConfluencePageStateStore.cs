using System.Text.Json;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>An attachment of a page as last listed.</summary>
internal sealed class ConfluenceStoredAttachment
{
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public string Filename { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>What the last listing said about one page or blog post.</summary>
internal sealed class ConfluenceStoredPage
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "page";         // page | blogpost
    public int Version { get; set; }
    public DateTimeOffset VersionAt { get; set; }
    public DateTimeOffset? LastCommentAt { get; set; }
    public string Title { get; set; } = "";
    public string? ParentId { get; set; }
    public string? ParentType { get; set; }             // page | folder | null
    public List<ConfluenceStoredAttachment> Attachments { get; set; } = [];
}

/// <summary>A folder's place in the tree, so a breadcrumb can climb past it.</summary>
internal sealed record ConfluenceFolderParent(string? ParentId, string? ParentType);

/// <summary>Sync bookkeeping for one space that is not part of any page.</summary>
internal sealed class ConfluenceSyncState
{
    public DateTimeOffset? Watermark { get; set; }
    public DateTimeOffset? LastAttachmentSweepAt { get; set; }
    public Dictionary<string, string> FolderTitles { get; set; } = [];

    /// <summary>Each known folder's own parent. Folders nest, and a breadcrumb walks through them.</summary>
    public Dictionary<string, ConfluenceFolderParent> FolderParents { get; set; } = [];

    public string SpaceName { get; set; } = "";
}

/// <summary>
/// A Confluence space source's local record of its pages: one JSON file per page or blog post plus
/// a state file, under the source's state directory.
/// <para>
/// Kept for the reason <see cref="GitHub.GitHubRecordStore"/> is: the pipeline reads each document
/// through a fresh connector, and the breadcrumb needs every ancestor's title, which would otherwise
/// cost a request per level. The sync is the only writer, but the pipeline reads while it writes,
/// so every write lands whole via a rename.
/// </para>
/// </summary>
internal sealed class ConfluencePageStateStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string PagesDir => Path.Combine(root, "pages");

    private string StatePath => Path.Combine(root, "state.json");

    /// <summary>Discards every page and starts a fresh state. Used when the cursor is null.</summary>
    public void Reset()
    {
        if (Directory.Exists(PagesDir))
            Directory.Delete(PagesDir, recursive: true);

        Directory.CreateDirectory(PagesDir);
        SaveState(new ConfluenceSyncState());
    }

    public ConfluenceStoredPage? Load(string id)
    {
        if (!IsContentId(id))
            return null;

        string path = PagePath(id);
        if (!File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ConfluenceStoredPage>(stream, Json);
    }

    public void Save(ConfluenceStoredPage page) => WriteAtomically(PagePath(page.Id), page);

    public void Delete(string id) => File.Delete(PagePath(id));

    public IEnumerable<string> Ids() =>
        Directory.Exists(PagesDir)
            ? Directory.EnumerateFiles(PagesDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(id => id is not null && IsContentId(id))!
            : [];

    public ConfluenceSyncState LoadState()
    {
        if (!File.Exists(StatePath))
            return new ConfluenceSyncState();

        using var stream = File.OpenRead(StatePath);
        return JsonSerializer.Deserialize<ConfluenceSyncState>(stream, Json) ?? new();
    }

    public void SaveState(ConfluenceSyncState state) => WriteAtomically(StatePath, state);

    /// <summary>Confluence content ids are digits; anything else never reaches a file name.</summary>
    public static bool IsContentId(string? id) =>
        id is { Length: > 0 and <= 19 } && id.All(char.IsAsciiDigit);

    private string PagePath(string id) =>
        IsContentId(id)
            ? Path.Combine(PagesDir, $"{id}.json")
            : throw new ArgumentException("Not a Confluence content id.", nameof(id));

    private static void WriteAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}
