using System.Globalization;
using System.Text;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Core;

namespace Connapse.Web.Mcp;

/// <summary>
/// The compact form of memory_recall and memory_timeline (CON-322): one line per fact under its subject, each led by a
/// short handle the save tools accept. An ended or corrected fact names the fact that replaced it, found in the same
/// answer or by a read of its chain that applies the same visibility rule, so a hidden fact is never named.
/// </summary>
public partial class McpTools
{
    /// <summary>The most chains one answer looks up to name what replaced an ended fact.</summary>
    internal const int SuccessorLookups = 10;

    private static async Task<string> RecallAnswerAsync(
        RecallMemory recall, OperationContext context, Attempt<SpacedRecall, MemoryStatus> recalled, bool detailed, CancellationToken ct)
    {
        if (!recalled.Succeeded)
            return Refused(recalled);
        SpacedRecall result = recalled.Result!;
        string? note = result.SkippedSpaces > 0
            ? $"Read the {RecallMemory.MaxSpaces} most recently changed spaces; {result.SkippedSpaces} more were not read. Name a `space` to read one of them."
            : NearestNote(result);

        if (detailed)
        {
            IReadOnlyDictionary<Guid, int> shown = await MemoryHandles.ShowAsync(recall.Handles, context, result.Facts.Select(f => f.Fact.Id), ct);
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                notice = MemoryNotice,
                facts = result.Facts.Select(f => Shown(f.Fact, f.Sources, result.Spaces.GetValueOrDefault(f.Fact.ContainerId), HandleOf(shown, f.Fact.Id))),
                note,
                nextCursor = result.NextCursor,
            }, MemoryJson);
        }

        Dictionary<Guid, Guid> now = await SuccessorsAsync(recall, context, result.Facts.Select(f => f.Fact).ToList(), ct);
        IReadOnlyDictionary<Guid, int> handles = await MemoryHandles.ShowAsync(
            recall.Handles, context, result.Facts.Select(f => f.Fact.Id).Concat(now.Values), ct);
        return CompactText(result, now, handles, note, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// For each ended or corrected fact, the newest fact that replaced it: by following <c>replaces</c> links among the
    /// facts shown, then, for the first few chains still open, among the versions of the chain the caller may see.
    /// </summary>
    private static async Task<Dictionary<Guid, Guid>> SuccessorsAsync(
        RecallMemory recall, OperationContext context, IReadOnlyList<MemoryFact> facts, CancellationToken ct)
    {
        Dictionary<Guid, Guid> next = [];
        foreach (MemoryFact f in facts)
            if (f.ReplacesId is { } replaced)
                next.TryAdd(replaced, f.Id);

        List<MemoryFact> open = facts.Where(f => Ended(f, DateTimeOffset.UtcNow) && !next.ContainsKey(f.Id)).ToList();
        foreach (MemoryFact f in open.DistinctBy(f => f.ChainId).Take(SuccessorLookups))
        {
            var chain = await recall.AcrossAsync(context, [f.ContainerId.ToString()],
                new MemoryQuery(f.ContainerId, History: true, Limit: 100, ChainId: f.ChainId), ct);
            if (!chain.Succeeded)
                continue;
            foreach (RecalledFact version in chain.Result!.Facts)
                if (version.Fact.ReplacesId is { } replaced)
                    next.TryAdd(replaced, version.Fact.Id);
        }

        Dictionary<Guid, Guid> newest = [];
        foreach (MemoryFact f in facts.Where(f => Ended(f, DateTimeOffset.UtcNow)))
        {
            Guid at = f.Id;
            for (int step = 0; step < 100 && next.TryGetValue(at, out Guid later); step++)
                at = later;
            if (at != f.Id)
                newest[f.Id] = at;
        }
        return newest;
    }

    /// <summary>The facts as plain lines grouped by subject, in the order they were found.</summary>
    internal static string CompactText(
        SpacedRecall result, IReadOnlyDictionary<Guid, Guid> now, IReadOnlyDictionary<Guid, int> handles, string? note, DateTimeOffset at)
    {
        StringBuilder text = new(MemoryNotice);
        text.Append('\n');
        if (result.Facts.Count == 0 && note is null)
            text.Append("\nNo facts found.\n");

        // Whether every subject shown is shown whole, so what a next page holds is only weaker matches.
        bool complete = true;
        foreach (IGrouping<(Guid, string), RecalledFact> subject in result.Facts.GroupBy(f => (f.Fact.ContainerId, f.Fact.Subject.ToLowerInvariant())))
        {
            MemoryFact first = subject.First().Fact;
            text.Append('\n').Append(Line(first.Subject));
            if (result.Spaces.GetValueOrDefault(first.ContainerId) is { } space)
                text.Append(CultureInfo.InvariantCulture, $" (space {space.Name}, {SpaceKind(space)})");
            bool whole = true;
            if (result.SubjectTotals?.TryGetValue(subject.Key.Item2, out long total) == true)
            {
                int current = subject.Count(f => !Ended(f.Fact, at) && (f.Fact.ValidFrom is null || f.Fact.ValidFrom <= at));
                whole = current >= total;
                text.Append(whole
                    ? total == 1 ? " - its only current fact" : string.Create(CultureInfo.InvariantCulture, $" - all {total} current facts")
                    : string.Create(CultureInfo.InvariantCulture, $" - {current} of {total} current facts"));
            }
            else if (result.SubjectTotals is not null)
            {
                whole = false;
            }
            complete &= whole;
            text.Append('\n');
            foreach (RecalledFact f in subject)
                text.Append(FactLine(f, now, handles, result.Spaces.GetValueOrDefault(f.Fact.ContainerId), at)).Append('\n');
        }

        if (note is not null)
            text.Append('\n').Append(note).Append('\n');
        // When every subject shown is whole, a next page holds only weaker matches, which cost more to offer than
        // they are worth; the detailed form still carries the cursor.
        if (result.NextCursor is not null && !(complete && result.SubjectTotals is not null))
            text.Append($"\nMore facts: pass cursor=\"{result.NextCursor}\" for the next page.\n");
        return text.ToString();
    }

    /// <summary>
    /// One fact: "f7 database: MySQL (2023-01-10 to 2025-03-01, ended; now f12) reason: ... sources: ...". A fact with no
    /// handle (a caller with no user or agent) shows its full ID instead.
    /// </summary>
    internal static string FactLine(
        RecalledFact recalled, IReadOnlyDictionary<Guid, Guid> now, IReadOnlyDictionary<Guid, int> handles, Container? space, DateTimeOffset at)
    {
        MemoryFact f = recalled.Fact;
        StringBuilder line = new("  ");
        line.Append(Ref(f.Id, handles)).Append(' ').Append(Line(f.Attribute)).Append(": ").Append(Line(f.Value));

        List<string> about = [];
        if (Dates(f) is { } dates)
            about.Add(dates);
        if (f.Scope == MemoryScope.Personal && space?.Audience != MemoryScope.Personal)
            about.Add("personal");
        string? status = f.SupersededAt is not null ? "corrected, was wrong" : Ended(f, at) ? "ended" : null;
        if (status is not null)
            about.Add(now.TryGetValue(f.Id, out Guid later) ? $"{status}; now {Ref(later, handles)}" : status);
        if (about.Count > 0)
            line.Append(" (").Append(string.Join(", ", about)).Append(')');

        if (!string.IsNullOrWhiteSpace(f.Reason))
            line.Append(" reason: ").Append(Line(f.Reason));
        IEnumerable<string> sources = recalled.Sources.Count > 0
            ? recalled.Sources.Select(s => s.Title is { Length: > 0 } title ? $"{Line(title)} [{s.Ref}]" : s.Ref)
            : f.Sources.Select(s => s.Ref);
        string cited = string.Join("; ", sources);
        if (cited.Length > 0)
            line.Append(" sources: ").Append(cited);
        return line.ToString();
    }

    /// <summary>Ended: corrected, or its end date has passed.</summary>
    private static bool Ended(MemoryFact f, DateTimeOffset at) => f.SupersededAt is not null || f.ValidTo is { } to && to <= at;

    private static string Ref(Guid id, IReadOnlyDictionary<Guid, int> handles) =>
        handles.TryGetValue(id, out int handle) ? MemoryHandles.Text(handle) : id.ToString();

    private static string? HandleOf(IReadOnlyDictionary<Guid, int> handles, Guid id) =>
        handles.TryGetValue(id, out int handle) ? MemoryHandles.Text(handle) : null;

    private static string? Dates(MemoryFact f) => (f.ValidFrom, f.ValidTo) switch
    {
        ({ } from, { } to) => $"{Day(from)} to {Day(to)}",
        ({ } from, null) => $"since {Day(from)}",
        (null, { } to) => $"until {Day(to)}",
        _ => null,
    };

    /// <summary>A date as YYYY-MM-DD, with the UTC time only when it isn't midnight.</summary>
    private static string Day(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return utc.TimeOfDay == TimeSpan.Zero
            ? utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }

    /// <summary>Text kept on one line, so a value can't start a line that reads as another fact.</summary>
    private static string Line(string text) => text.ReplaceLineEndings(" ");
}
