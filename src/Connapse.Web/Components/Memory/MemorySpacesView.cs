using System.Globalization;
using Connapse.Application.Memory;
using Microsoft.AspNetCore.WebUtilities;

namespace Connapse.Web.Components.Memory;

/// <summary>
/// What the Memory page's table shows: its find text, kind or starred filter, sort and page, read from and written to
/// the page address so a link or Back reopens the same view. <see cref="Cursor"/> is the page's opaque server cursor and
/// <see cref="Page"/> its number, used only to say which rows are on screen. Values that don't parse are dropped.
/// </summary>
public sealed record MemorySpacesView(
    string? Text = null,
    MemorySpaceKind? Kind = null,
    bool Starred = false,
    MemorySpaceSort Sort = MemorySpaceSort.Updated,
    string? Cursor = null,
    int Page = 1)
{
    /// <summary>Spaces a page; the plan's 50.</summary>
    public const int PageSize = ListMemorySpaces.DefaultLimit;

    /// <summary>Up to this many spaces show as cards; past it, the paged table.</summary>
    public const int MostCards = 9;

    /// <summary>The same view on its first page.</summary>
    public MemorySpacesView FirstPage => this with { Cursor = null, Page = 1 };

    public MemorySpaceQuery Query => new(Kind, Text, Starred, Sort, PageSize, Cursor);

    /// <summary>The view in the query of <paramref name="uri"/>.</summary>
    public static MemorySpacesView Parse(string uri)
    {
        int at = uri.IndexOf('?', StringComparison.Ordinal);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = at < 0 ? [] : QueryHelpers.ParseQuery(uri[at..]);
        string? Get(string key) => query.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString().Trim() : null;

        string? cursor = Get("after");
        string? kind = Get("kind");
        return new MemorySpacesView(
            Get("q"),
            kind switch { "personal" => MemorySpaceKind.Personal, "team" => MemorySpaceKind.Team, "container" => MemorySpaceKind.Container, _ => null },
            // Starred is a filter of its own in the pills, so it never shares the address with a kind.
            kind is null && Get("starred") == "true",
            Get("sort") switch { "name" => MemorySpaceSort.Name, "facts" => MemorySpaceSort.Facts, _ => MemorySpaceSort.Updated },
            cursor,
            // A page number without its cursor would label page 1's rows as another page's.
            cursor is not null && int.TryParse(Get("page"), NumberStyles.None, CultureInfo.InvariantCulture, out int page) && page > 1 ? page : 1);
    }

    /// <summary>The address query for this view: every key the view owns, null where it's unset so the address drops it.</summary>
    public IReadOnlyDictionary<string, object?> Address() => new Dictionary<string, object?>
    {
        ["q"] = Text,
        ["kind"] = Kind switch
        {
            MemorySpaceKind.Personal => "personal",
            MemorySpaceKind.Team => "team",
            MemorySpaceKind.Container => "container",
            _ => null,
        },
        ["starred"] = Starred ? "true" : null,
        ["sort"] = Sort switch { MemorySpaceSort.Name => "name", MemorySpaceSort.Facts => "facts", _ => null },
        ["after"] = Cursor,
        ["page"] = Cursor is null || Page <= 1 ? null : Page.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The words for a sort in the sort menu.</summary>
    public static string SortLabel(MemorySpaceSort sort) => sort switch
    {
        MemorySpaceSort.Name => "Name",
        MemorySpaceSort.Facts => "Most facts",
        _ => "Recently updated",
    };

    /// <summary>"1–50 of 165 spaces".</summary>
    public string Range(int rows, int total)
    {
        long first = (long)(Page - 1) * PageSize + (rows == 0 ? 0 : 1);
        long last = (long)(Page - 1) * PageSize + rows;
        return string.Create(CultureInfo.InvariantCulture, $"{first:N0}–{last:N0} of {total:N0} {(total == 1 ? "space" : "spaces")}");
    }
}
