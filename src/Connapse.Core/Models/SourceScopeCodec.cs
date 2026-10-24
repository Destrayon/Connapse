using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connapse.Core;

/// <summary>
/// Reads and writes a source's stored scope JSON as a <see cref="SourceScope"/>, by the source's
/// provider (#859). The stored keys stay what the connectors read, so no row needs migrating.
/// </summary>
public static class SourceScopeCodec
{
    private const string JiraKind = "jira-project";
    private const string ConfluenceKind = "confluence-space";
    private const string GitHubHost = "github.com";

    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    /// <summary>The provider a scope's sources use.</summary>
    public static ConnectionProvider ProviderOf(SourceScope scope) => scope switch
    {
        S3BucketScope => ConnectionProvider.S3,
        AzureBlobContainerScope => ConnectionProvider.AzureBlob,
        FilesystemFolderScope => ConnectionProvider.Filesystem,
        SftpFolderScope => ConnectionProvider.Sftp,
        GitHubRepositoryScope => ConnectionProvider.GitHub,
        JiraProjectScope or ConfluenceSpaceScope => ConnectionProvider.Atlassian,
        CustomSourceScope => ConnectionProvider.Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope.GetType().Name, "No provider reads this kind of scope."),
    };

    /// <summary>
    /// The typed scope a stored row holds, or null when the JSON doesn't name a scope of the provider's
    /// kind; such a row is left to the connector, which says what is wrong with it.
    /// </summary>
    /// <remarks>
    /// Each member is read the way the connector factory reads it, so a row means here what it means to the
    /// sync. Where other readers are looser (an Atlassian <c>kind</c> in another case, a kindless Confluence
    /// row), the factory refuses the row, so this does too.
    /// </remarks>
    public static SourceScope? Read(ConnectionProvider provider, string? storedJson)
    {
        if (Parse(storedJson) is not { } o)
            return null;

        return provider switch
        {
            ConnectionProvider.S3 => NonBlank(o, "bucketName") is { } bucket ? new S3BucketScope(bucket, Str(o, "prefix")) : null,
            ConnectionProvider.AzureBlob => NonBlank(o, "containerName") is { } container
                ? new AzureBlobContainerScope(container, Str(o, "prefix"))
                : null,
            ConnectionProvider.Filesystem => new FilesystemFolderScope(Str(o, "subPath"), Arr(o, "includePatterns"), Arr(o, "excludePatterns")),
            ConnectionProvider.Sftp => new SftpFolderScope(Str(o, "subPath"), Arr(o, "includePatterns"), Arr(o, "excludePatterns")),
            ConnectionProvider.GitHub => ReadGitHub(o),
            ConnectionProvider.Atlassian => Str(o, "kind") switch
            {
                JiraKind => Id(o, "projectId") is { } projectId
                    ? new JiraProjectScope(projectId, Str(o, "projectKey"), Str(o, "projectName"), Bool(o, "includeAttachments"), Int(o, "maxAttachmentMb"))
                    : null,
                ConfluenceKind => Id(o, "spaceId") is { } spaceId
                    ? new ConfluenceSpaceScope(spaceId, Str(o, "spaceKey"), Str(o, "spaceName"), Bool(o, "includeAttachments"), Int(o, "maxAttachmentMb"))
                    : null,
                _ => null,
            },
            // The connector reads a custom scope with CustomSourceScope.TryParse, whose member names ignore case.
            ConnectionProvider.Custom => CustomSourceScope.TryParse(storedJson),
            _ => null,
        };
    }

    /// <summary>
    /// The JSON to store for <paramref name="scope"/>. Given the row's current JSON, keys the type
    /// doesn't model are kept, and a row whose scope is unchanged comes back exactly as it was.
    /// </summary>
    public static string Write(SourceScope scope, string? storedJson = null)
    {
        JsonObject? stored = Parse(storedJson);
        if (stored is not null && Read(ProviderOf(scope), storedJson) is { } current && Same(current, scope))
            return storedJson!;

        JsonObject written = stored ?? new JsonObject();
        foreach (var (key, value) in Members(scope))
        {
            if (value is null)
                written.Remove(key);
            else
                written[key] = value;
        }
        return written.ToJsonString();
    }

    private static GitHubRepositoryScope? ReadGitHub(JsonObject o)
    {
        if (NonBlank(o, "owner") is not { } owner || NonBlank(o, "repo") is not { } repo)
            return null;

        // The connector reads only github.com, and refuses a row naming any other host. The host isn't
        // modeled, since there is only one, so a stored one is kept as it is.
        if (!string.Equals(Str(o, "host") ?? GitHubHost, GitHubHost, StringComparison.OrdinalIgnoreCase))
            return null;

        // A scope from before content kinds were stored is the repository's docs.
        string kindName = Str(o, "kind") ?? nameof(GitHubContentKind.Docs);
        if (!Enum.TryParse(kindName, ignoreCase: true, out GitHubContentKind kind) || !Enum.IsDefined(kind))
            return null;

        return new GitHubRepositoryScope(
            owner, repo, kind,
            RepoId: Long(o, "repoId"),
            Private: Bool(o, "private") ?? false,
            IncludePatterns: Arr(o, "includePatterns"),
            ExcludePatterns: Arr(o, "excludePatterns"),
            IncludeComments: Bool(o, "includeComments"),
            IncludeCommentAuthors: Arr(o, "includeCommentAuthors"),
            ExcludeCommentAuthors: Arr(o, "excludeCommentAuthors"));
    }

    /// <summary>Every stored key a kind of scope models, with the value to store or null to leave it out.</summary>
    private static IEnumerable<(string Key, JsonNode? Value)> Members(SourceScope scope) => scope switch
    {
        S3BucketScope s => [("bucketName", s.BucketName), ("prefix", s.Prefix)],
        AzureBlobContainerScope s => [("containerName", s.ContainerName), ("prefix", s.Prefix)],
        FilesystemFolderScope s => Folder(s.SubPath, s.IncludePatterns, s.ExcludePatterns),
        SftpFolderScope s => Folder(s.SubPath, s.IncludePatterns, s.ExcludePatterns),
        GitHubRepositoryScope s =>
        [
            ("owner", s.Owner), ("repo", s.Repo), ("kind", s.Kind.ToString()), ("repoId", s.RepoId),
            ("private", s.Private ? true : null), ("includePatterns", List(s.IncludePatterns)), ("excludePatterns", List(s.ExcludePatterns)),
            ("includeComments", s.IncludeComments), ("includeCommentAuthors", List(s.IncludeCommentAuthors)),
            ("excludeCommentAuthors", List(s.ExcludeCommentAuthors)),
        ],
        JiraProjectScope s =>
        [
            ("kind", JiraKind), ("projectId", s.ProjectId), ("projectKey", s.ProjectKey), ("projectName", s.ProjectName),
            ("includeAttachments", s.IncludeAttachments), ("maxAttachmentMb", s.MaxAttachmentMb),
        ],
        ConfluenceSpaceScope s =>
        [
            ("kind", ConfluenceKind), ("spaceId", s.SpaceId), ("spaceKey", s.SpaceKey), ("spaceName", s.SpaceName),
            ("includeAttachments", s.IncludeAttachments), ("maxAttachmentMb", s.MaxAttachmentMb),
        ],
        CustomSourceScope s => [("baseUrl", s.BaseUrl), ("scopeId", s.ScopeId)],
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope.GetType().Name, "No stored form for this kind of scope."),
    };

    private static (string, JsonNode?)[] Folder(string? subPath, IReadOnlyList<string>? include, IReadOnlyList<string>? exclude) =>
        [("subPath", subPath), ("includePatterns", List(include)), ("excludePatterns", List(exclude))];

    private static JsonArray? List(IReadOnlyList<string>? values) =>
        values is null ? null : new JsonArray([.. values.Select(v => (JsonNode?)v)]);

    /// <summary>Whether two scopes say the same thing; lists compare by their items.</summary>
    private static bool Same(SourceScope a, SourceScope b) =>
        a.GetType() == b.GetType()
        && JsonSerializer.Serialize(a, Api) == JsonSerializer.Serialize(b, Api);

    private static JsonObject? Parse(string? json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static string? NonBlank(JsonObject o, string name) =>
        Str(o, name) is { } s && !string.IsNullOrWhiteSpace(s) ? s : null;

    /// <summary>An id given as a JSON string or number, as the Atlassian connectors accept it.</summary>
    private static string? Id(JsonObject o, string name) => o[name] switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => NonBlank(o, name),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        _ => null,
    };

    private static bool? Bool(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    private static int? Int(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && int.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i)
            ? i
            : null;

    private static long? Long(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && long.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)
            ? l
            : null;

    /// <summary>A string array, dropping anything that isn't a string, as the connectors read patterns.</summary>
    private static IReadOnlyList<string>? Arr(JsonObject o, string name) =>
        o[name] is JsonArray a
            ? [.. a.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>())]
            : null;
}
