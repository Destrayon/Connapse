using System.Globalization;
using Connapse.Core;

namespace Connapse.Web.Components.Memory;

/// <summary>
/// One subject of the memory list's grouped view: its facts loaded so far, the cursor for the rest, and its count
/// once known, with the words the list shows for it.
/// </summary>
public sealed class MemorySubjectGroup(MemorySubject subject, bool open, long? total)
{
    /// <summary>Subjects open when a grouped page first shows, and the facts each shows before "Show more".</summary>
    public const int OpenFirst = 3;
    public const int FirstFacts = 3;

    public MemorySubject Subject { get; } = subject;
    public string Key { get; } = KeyOf(subject.Subject);
    public bool Open { get; set; } = open;
    public List<RecalledFact> Facts { get; } = [];
    public string? Next { get; set; }
    public bool Loaded { get; set; }
    public bool Loading { get; set; }
    public long? Total { get; set; } = total;
    public bool Capped { get; set; }

    /// <summary>The key subjects are matched by when a page is refreshed.</summary>
    public static string KeyOf(string subject) => subject.ToLowerInvariant();

    /// <summary>Records a page of this subject's facts; with no more after it, the facts loaded are all of them.</summary>
    public void Add(IReadOnlyList<RecalledFact> facts, string? next)
    {
        Facts.AddRange(facts);
        (Next, Loaded) = (next, true);
        if (next is null)
            (Total, Capped) = (Facts.Count, false);
    }

    /// <summary>"1 fact", "12 facts", "500+ facts", or "3+ facts" while the count is unknown.</summary>
    public string CountLabel => Total switch
    {
        null => $"{Facts.Count}+ facts",
        1 when !Capped => "1 fact",
        { } n => $"{N(n)}{(Capped ? "+" : "")} facts",
    };

    /// <summary>"Show 9 more in Halcyon", or "Show more in Halcyon" when how many is unknown.</summary>
    public string MoreLabel => Total is { } total && !Capped && total > Facts.Count
        ? $"Show {N(total - Facts.Count)} more in {Subject.Subject}"
        : $"Show more in {Subject.Subject}";

    /// <summary>"All 9 subjects matching “refund” · 20 facts", or the page's subjects when there are more pages.</summary>
    public static string Range(int subjects, int page, bool more, string? text, MemoryFactCount? count)
    {
        string shown = subjects == 1 ? "1 subject" : $"{N(subjects)} subjects";
        string range = page == 1 && !more ? $"All {shown}" : $"{shown} on page {page}";
        string matching = text is not null ? $" matching “{text}”" : "";
        string total = count is null ? ""
            : $" · {N(count.Count)}{(count.Capped ? "+" : "")} {(count.Count == 1 && !count.Capped ? "fact" : "facts")}";
        return range + matching + total;
    }

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
