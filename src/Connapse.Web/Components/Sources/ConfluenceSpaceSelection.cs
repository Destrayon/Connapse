using System.Text.Json;
using Connapse.Core;
using Connapse.Web.Services;

namespace Connapse.Web.Components.Sources;

/// <summary>
/// The spaces chosen on the New source dialog for an Atlassian site: one source out per space.
/// Kept as a type of its own, like <c>GitHubRepositoryForm</c>, so the scope keys written here are
/// tested against the ones <c>ConnectorFactory</c> reads.
/// </summary>
public sealed class ConfluenceSpaceSelection
{
    public const int DefaultMaxAttachmentMb = 25;
    private const int MaxSourceName = 128;
    private const int MaxAttachmentMbLimit = 500;

    /// <summary>The spaces the site offers, as last listed.</summary>
    public IReadOnlyList<ConfluenceSpaceInfo> Available { get; set; } = [];

    /// <summary>Ids of the spaces ticked.</summary>
    public HashSet<string> Selected { get; } = [];

    public bool IncludePersonal { get; set; }

    public bool IncludeAttachments { get; set; } = true;

    public int MaxAttachmentMb { get; set; } = DefaultMaxAttachmentMb;

    public string? Validate()
    {
        if (Selected.Count == 0)
            return "Choose at least one space.";
        if (IncludeAttachments && MaxAttachmentMb is < 1 or > MaxAttachmentMbLimit)
            return $"Max attachment size must be between 1 and {MaxAttachmentMbLimit} MB.";
        return null;
    }

    public static string ScopeJson(ConfluenceSpaceInfo space, bool includeAttachments, int maxAttachmentMb) =>
        JsonSerializer.Serialize(new
        {
            kind = "confluence-space",
            spaceId = space.Id,
            spaceKey = space.Key,
            includeAttachments,
            maxAttachmentMb,
        });

    public static string SourceName(string siteHost, string spaceName)
    {
        string name = $"{siteHost} / {spaceName}";
        return name.Length <= MaxSourceName ? name : name[..(MaxSourceName - 1)] + "…";
    }

    /// <summary>
    /// One create request per ticked space not already a source. Spaces already sourced on the
    /// connection (matched on id) are returned in <paramref name="skipped"/> instead.
    /// </summary>
    public IReadOnlyList<CreateSourceRequest> ToRequests(
        Guid connectionId, string siteHost, IReadOnlySet<string> existingSpaceIds, out IReadOnlyList<string> skipped)
    {
        var requests = new List<CreateSourceRequest>();
        var skippedNames = new List<string>();

        foreach (var space in Available.Where(s => Selected.Contains(s.Id)))
        {
            if (existingSpaceIds.Contains(space.Id))
            {
                skippedNames.Add(space.Name);
                continue;
            }

            requests.Add(new CreateSourceRequest(
                SourceName(siteHost, space.Name), connectionId, ScopeJson(space, IncludeAttachments, MaxAttachmentMb)));
        }

        skipped = skippedNames;
        return requests;
    }

    /// <summary>The space ids that already have a source, from the scopes of a connection's sources.</summary>
    public static HashSet<string> SpaceIdsOf(IEnumerable<Source> sources)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            try
            {
                using var doc = JsonDocument.Parse(source.ScopeJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("spaceId", out var id))
                {
                    string? value = id.ValueKind switch
                    {
                        JsonValueKind.String => id.GetString(),
                        JsonValueKind.Number => id.GetRawText(),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(value))
                        ids.Add(value);
                }
            }
            catch (JsonException)
            {
            }
        }

        return ids;
    }
}
