using System.Text.Json;
using Connapse.Core;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Search.Links;

/// <summary>
/// Which searches run and how much each counts, as the searched container or source chose in its own search
/// settings. Read only while the link layer is on, so turning it off leaves search exactly as it was.
/// </summary>
internal static class SourceSearchChoices
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// <paramref name="global"/> with the owner's choices laid over it: <see cref="SearchSettings.UseVectorSearch"/>,
    /// <see cref="SearchSettings.UseKeywordSearch"/>, <see cref="SearchSettings.FusionAlpha"/> and the link
    /// settings. Everything else, and whether the link layer is on at all, stays the deployment's.
    /// </summary>
    public static async Task<SearchSettings> ResolveAsync(
        KnowledgeDbContext context, string? ownerId, SearchSettings global, CancellationToken ct)
    {
        if (!Guid.TryParse(ownerId, out Guid id))
            return global;

        JsonDocument? json = await context.Containers.AsNoTracking()
            .Where(c => c.Id == id).Select(c => c.SettingsOverridesJson).FirstOrDefaultAsync(ct)
            ?? await context.Sources.AsNoTracking()
            .Where(s => s.Id == id).Select(s => s.SettingsOverridesJson).FirstOrDefaultAsync(ct);

        SearchSettings? own = Parse(json);
        return own is null
            ? global
            : global with
            {
                UseVectorSearch = own.UseVectorSearch,
                UseKeywordSearch = own.UseKeywordSearch,
                FusionAlpha = own.FusionAlpha,
                LinkWeight = own.LinkWeight,
                LinkMaxHops = own.LinkMaxHops,
                LinkSeeds = own.LinkSeeds,
            };
    }

    internal static SearchSettings? Parse(JsonDocument? json)
    {
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<ContainerSettingsOverrides>(json.RootElement.GetRawText(), JsonOptions)?.Search;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The mode the owner's choices allow: both searches leave the requested mode alone, one alone forces it,
    /// and neither is treated as both rather than as a search that cannot run.
    /// </summary>
    public static SearchMode Mode(SearchMode requested, SearchSettings settings) =>
        (settings.UseVectorSearch, settings.UseKeywordSearch) switch
        {
            (true, false) => SearchMode.Semantic,
            (false, true) => SearchMode.Keyword,
            _ => requested,
        };
}
