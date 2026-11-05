using System.Globalization;
using Connapse.Application.Memory;
using Connapse.Core;
using Microsoft.AspNetCore.WebUtilities;

namespace Connapse.Web.Components.Memory;

/// <summary>
/// What a memory list shows: its search, filters, sort and page, read from and written to the page address so a link
/// or Back reopens the same view. <see cref="Cursor"/> is the page's opaque server cursor and <see cref="Page"/> its
/// number, used only to say which rows are on screen. Values that don't parse are dropped, never guessed.
/// </summary>
public sealed record MemoryListView(
    string? Text = null,
    string? Subject = null,
    Guid? Author = null,
    MemoryCites Cites = MemoryCites.Any,
    DateOnly? From = null,
    DateOnly? To = null,
    bool All = false,
    MemoryFactSort? Sort = null,
    string? Cursor = null,
    int Page = 1)
{
    /// <summary>Facts a page; the plan's 50.</summary>
    public const int PageSize = 50;

    /// <summary>Every address key the view owns, so writing a view clears the ones it leaves unset.</summary>
    public static readonly IReadOnlyList<string> Keys = ["q", "subject", "author", "cites", "from", "to", "status", "sort", "after", "page"];

    /// <summary>The sort used: the one chosen, else best match while searching and newest first otherwise.</summary>
    public MemoryFactSort Order => Sort switch
    {
        MemoryFactSort.Relevance when Text is null => MemoryFactSort.Recorded,
        { } chosen => chosen,
        null => Text is null ? MemoryFactSort.Recorded : MemoryFactSort.Relevance,
    };

    /// <summary>The same view on its first page.</summary>
    public MemoryListView FirstPage => this with { Cursor = null, Page = 1 };

    /// <summary>Whether any filter beyond Current/All narrows the list.</summary>
    public bool Narrowed => Text is not null || Subject is not null || Author is not null || Cites != MemoryCites.Any
        || From is not null || To is not null;

    public MemoryFactFilter Filter => new(
        Text,
        Subject,
        Author,
        Cites,
        From is { } from ? new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        // Through the end of the day chosen, as "recorded between" reads.
        To is { } to ? new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero) : null,
        All ? MemoryFactStatus.All : MemoryFactStatus.Current);

    public MemoryFactListing Listing => new(Filter, Order, Cursor, PageSize);

    /// <summary>The first and last row numbers of a page holding <paramref name="rows"/> facts.</summary>
    public (long First, long Last) Rows(int rows) =>
        ((long)(Page - 1) * PageSize + (rows == 0 ? 0 : 1), (long)(Page - 1) * PageSize + rows);

    /// <summary>The view in the query of <paramref name="uri"/>.</summary>
    public static MemoryListView Parse(string uri)
    {
        int at = uri.IndexOf('?', StringComparison.Ordinal);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = at < 0 ? [] : QueryHelpers.ParseQuery(uri[at..]);
        string? Get(string key) => query.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString().Trim() : null;

        string? text = Get("q");
        string? cursor = Get("after");
        return new MemoryListView(
            text,
            Get("subject"),
            Guid.TryParse(Get("author"), out Guid author) ? author : null,
            Get("cites") switch { "some" => MemoryCites.Some, "none" => MemoryCites.None, _ => MemoryCites.Any },
            Day(Get("from")),
            Day(Get("to")),
            Get("status") == "all",
            Get("sort") switch
            {
                "recorded" => MemoryFactSort.Recorded,
                "subject" => MemoryFactSort.Subject,
                "validFrom" => MemoryFactSort.ValidFrom,
                "relevance" when text is not null => MemoryFactSort.Relevance,
                _ => null,
            },
            cursor,
            // A page number without its cursor would label page 1's rows as another page's.
            cursor is not null && int.TryParse(Get("page"), NumberStyles.None, CultureInfo.InvariantCulture, out int page) && page > 1 ? page : 1);

        static DateOnly? Day(string? value) =>
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day) ? day : null;
    }

    /// <summary>The address query for this view: every key the view owns, null where it's unset so the address drops it.</summary>
    public IReadOnlyDictionary<string, object?> Query() => new Dictionary<string, object?>
    {
        ["q"] = Text,
        ["subject"] = Subject,
        ["author"] = Author?.ToString(),
        ["cites"] = Cites switch { MemoryCites.Some => "some", MemoryCites.None => "none", _ => null },
        ["from"] = From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["to"] = To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["status"] = All ? "all" : null,
        ["sort"] = Sort switch
        {
            MemoryFactSort.Recorded => "recorded",
            MemoryFactSort.Subject => "subject",
            MemoryFactSort.ValidFrom => "validFrom",
            MemoryFactSort.Relevance => "relevance",
            _ => null,
        },
        ["after"] = Cursor,
        ["page"] = Cursor is null || Page <= 1 ? null : Page.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The words for a sort in the sort menu.</summary>
    public static string SortLabel(MemoryFactSort sort) => sort switch
    {
        MemoryFactSort.Subject => "Subject A–Z",
        MemoryFactSort.ValidFrom => "True from",
        MemoryFactSort.Relevance => "Best match",
        _ => "Recently recorded",
    };

    /// <summary>"1–50 of 4,812 facts you can see", with the total once it has loaded ("4,000+" when counting stopped early).</summary>
    public string Range(int rows, MemoryFactCount? count)
    {
        (long first, long last) = Rows(rows);
        string range = string.Create(CultureInfo.InvariantCulture, $"{first:N0}–{last:N0}");
        if (count is null)
            return $"{range} of …";
        string total = count.Capped
            ? string.Create(CultureInfo.InvariantCulture, $"{count.Count:N0}+")
            : count.Count.ToString("N0", CultureInfo.InvariantCulture);
        return $"{range} of {total} {(count.Count == 1 && !count.Capped ? "fact" : "facts")} you can see";
    }
}
