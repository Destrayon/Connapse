namespace Connapse.Core;

/// <summary>
/// Who is reading and which sources they can read, worked out once per request (link nodes resolved to their
/// documents). The store shows a fact only when every source it cites is in <paramref name="Readable"/> and the fact is
/// team or <paramref name="Caller"/> wrote it. A source not in the set, such as one cited after the set was worked out,
/// hides the fact.
/// </summary>
public sealed record MemoryViewer(MemoryCaller Caller, IReadOnlySet<MemorySource> Readable);

/// <summary>How a space's facts are ordered. <see cref="Relevance"/> needs search text.</summary>
public enum MemoryFactSort { Recorded = 0, Subject = 1, ValidFrom = 2, Relevance = 3 }

/// <summary><see cref="Current"/>: facts not superseded and true now. <see cref="All"/>: every version.</summary>
public enum MemoryFactStatus { Current = 0, All = 1 }

/// <summary>Whether a fact cites anything: <see cref="Some"/> at least one source, <see cref="None"/> none.</summary>
public enum MemoryCites { Any = 0, Some = 1, None = 2 }

/// <summary>
/// Which facts of a space to list or count. <paramref name="Subject"/> matches exactly, in any case;
/// <paramref name="Author"/> is a user or agent id; <paramref name="From"/> and <paramref name="To"/> bound when the
/// fact was recorded (inclusive).
/// </summary>
public sealed record MemoryFactFilter(
    string? Text = null,
    string? Subject = null,
    Guid? Author = null,
    MemoryCites Cites = MemoryCites.Any,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    MemoryFactStatus Status = MemoryFactStatus.Current);

/// <summary>
/// Where the last page ended: the last fact's sort values and id. Only the values its sort uses are set:
/// <paramref name="Recorded"/> for recorded, <paramref name="Subject"/> and <paramref name="Attribute"/> for subject,
/// <paramref name="ValidFrom"/> for true-from, <paramref name="Rank"/> for best match. A search also keeps the
/// <paramref name="Terms"/> it ranked by, so later pages rank the same way however the space changes, and
/// <paramref name="Position"/>, how many matches were listed through this one, so it stops at the best 1,000.
/// </summary>
public sealed record MemoryFactKey(
    Guid Id,
    DateTimeOffset? Recorded = null,
    string? Subject = null,
    string? Attribute = null,
    DateTimeOffset? ValidFrom = null,
    float? Rank = null,
    string? Terms = null,
    int Position = 0);

/// <summary>A fact as listed, with the key a next page starts after.</summary>
public sealed record MemoryListedFact(MemoryFact Fact, MemoryFactKey Key);

/// <summary>One page of facts. <paramref name="Denied"/>: access could not be checked, so nothing is shown.</summary>
public sealed record MemoryFactPage(IReadOnlyList<RecalledFact> Facts, MemoryFactKey? Next, bool Denied = false)
{
    public static MemoryFactPage DeniedResult { get; } = new([], null, Denied: true);
}

/// <summary>How many facts the caller can see. <paramref name="Capped"/>: counting stopped early, so it is at least this many.</summary>
public sealed record MemoryFactCount(long Count, bool Capped, bool Denied = false)
{
    public static MemoryFactCount DeniedResult { get; } = new(0, false, Denied: true);
}

/// <summary>A subject and how many visible facts it has. Subjects that differ only in case are one subject.</summary>
public sealed record MemorySubject(string Subject, long Facts);

/// <summary>One page of subjects, A to Z. <paramref name="Next"/> is the lower-cased subject a next page starts after.</summary>
public sealed record MemorySubjectPage(IReadOnlyList<MemorySubject> Subjects, string? Next, bool Denied = false)
{
    public static MemorySubjectPage DeniedResult { get; } = new([], null, Denied: true);
}

/// <summary>Where the last history page ended: the last version's recorded time and id.</summary>
public sealed record MemoryVersionKey(DateTimeOffset Recorded, Guid Id);

/// <summary>
/// One page of a fact's versions, newest first, and how many versions the caller can see in all.
/// <paramref name="NotFound"/>: the fact is not there or the caller cannot see it.
/// </summary>
public sealed record MemoryHistoryPage(
    IReadOnlyList<RecalledFact> Versions, long Total, MemoryVersionKey? Next, bool NotFound = false, bool Denied = false)
{
    public static MemoryHistoryPage Missing { get; } = new([], 0, null, NotFound: true);
    public static MemoryHistoryPage DeniedResult { get; } = new([], 0, null, Denied: true);
}
