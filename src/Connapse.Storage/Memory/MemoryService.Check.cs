using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Memory;

// Check-by dates (CON-340): confirming a fact, re-saving one memory already holds, and a space's default periods.
public sealed partial class MemoryService
{
    /// <summary>The most current facts on one subject and attribute a re-save compares against.</summary>
    internal const int MaxResaveCandidates = 100;

    public async Task<MemoryWriteResult> ConfirmAsync(
        Guid factId, MemoryCheckBy? check, MemoryCaller caller, bool teamWriter, CancellationToken ct = default)
    {
        if (CheckError(check) is { } invalid)
            return MemoryWriteResult.Failed(invalid);
        // A fact the caller cannot see is reported as missing, so a confirm cannot probe for one.
        if (await VisibleFactAsync(factId, caller, ct) is null)
            return MemoryWriteResult.Failed($"Fact '{factId}' not found.", MemoryWriteFailure.NotFound);

        MemoryWriteResult result = await store.ConfirmAsync(factId, check,
            chain => ConfirmErrorAsync(chain.Single(f => f.Id == factId), caller, teamWriter, ct), ct);
        if (result.Fact is { } confirmed)
        {
            await audit.LogAsync("memory.confirmed", "memory_fact", confirmed.Id.ToString(),
                new { confirmed.ContainerId, caller.UserId, caller.AgentId, Resaved = false }, ct);
        }
        return result;
    }

    public Task<IReadOnlyDictionary<string, int>> CheckDefaultsAsync(Guid containerId, CancellationToken ct = default) =>
        store.CheckDefaultsAsync(containerId, ct);

    public async Task<string?> SetCheckDefaultsAsync(
        Guid containerId, IReadOnlyDictionary<string, int> defaults, MemoryCaller caller, CancellationToken ct = default)
    {
        if (CheckDefaultsError(defaults) is { } invalid)
            return invalid;
        await store.SetCheckDefaultsAsync(containerId, defaults, ct);
        await audit.LogAsync("memory.check_defaults_set", "container", containerId.ToString(),
            new { Defaults = defaults.Count, caller.UserId, caller.AgentId }, ct);
        return null;
    }

    /// <summary>
    /// Null when <paramref name="fact"/>, as it is under the chain lock, may be confirmed by <paramref name="caller"/>,
    /// or the error refusing it: missing when the caller cannot see it, forbidden for a team fact without write access.
    /// </summary>
    internal async Task<MemoryWriteResult?> ConfirmErrorAsync(MemoryFact fact, MemoryCaller caller, bool teamWriter, CancellationToken ct)
    {
        IReadOnlySet<MemorySource> readable;
        try
        {
            readable = await ReadableAsync(fact.Sources, caller.UserId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not check memory sources; refusing the confirm");
            return MemoryWriteResult.Failed("Error: could not check access to the sources; nothing was written.", MemoryWriteFailure.Unchecked);
        }
        if (!Visible(fact, readable, caller))
            return MemoryWriteResult.Failed($"Fact '{fact.Id}' not found.", MemoryWriteFailure.NotFound);
        return fact.Scope == MemoryScope.Team && !teamWriter
            ? MemoryWriteResult.Failed("Error: confirming a team fact needs write access (knowledge:write).", MemoryWriteFailure.Forbidden)
            : null;
    }

    /// <summary>
    /// When memory already holds <paramref name="fact"/> (cleaned, its sources checked readable) as a current fact the
    /// caller can see, confirms that fact and returns the result; otherwise null, and the fact is added as new. A
    /// duplicate has the same subject, attribute and value in any case, the same scope and sources, and the dates the
    /// new fact gives, if any. When the confirm loses a race (the fact was corrected or ended meanwhile), the save adds.
    /// </summary>
    private async Task<MemoryWriteResult?> ResavedAsync(Guid containerId, NewMemoryFact fact, MemoryCaller caller, CancellationToken ct)
    {
        IReadOnlyList<MemoryFact> current = await store.QueryAsync(
            new MemoryQuery(containerId, Subject: fact.Subject, Attribute: fact.Attribute, Limit: MaxResaveCandidates),
            0, MaxResaveCandidates, ct);
        if (current.FirstOrDefault(c => IsResaveOf(c, fact, caller)) is not { } existing)
            return null;

        MemoryWriteResult result = await store.ConfirmAsync(existing.Id, fact.Check, _ => Task.FromResult<MemoryWriteResult?>(null), ct);
        if (result.Fact is not { } confirmed)
            return null;
        await audit.LogAsync("memory.confirmed", "memory_fact", confirmed.Id.ToString(),
            new { confirmed.ContainerId, caller.UserId, caller.AgentId, Resaved = true }, ct);
        return result;
    }

    /// <summary>Whether saving <paramref name="fact"/> would repeat <paramref name="existing"/>, a fact the caller can see.</summary>
    internal static bool IsResaveOf(MemoryFact existing, NewMemoryFact fact, MemoryCaller caller) =>
        string.Equals(existing.Subject, fact.Subject, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.Attribute, fact.Attribute, StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.Value, fact.Value, StringComparison.OrdinalIgnoreCase)
        && existing.Scope == fact.ScopeOrDefault
        && existing.Sources.ToHashSet().SetEquals(fact.Sources ?? [])
        && (fact.ValidFrom is null || fact.ValidFrom == existing.ValidFrom)
        && (fact.ValidTo is null || fact.ValidTo == existing.ValidTo)
        && (existing.Scope == MemoryScope.Team || caller.IsAuthorOf(existing.Author));

    /// <summary>An error when <paramref name="check"/> asks for a period outside 1–<see cref="MemoryCheckBy.MaxDays"/> days.</summary>
    internal static string? CheckError(MemoryCheckBy? check) =>
        check?.Days is { } days && (days < 1 || days > MemoryCheckBy.MaxDays)
            ? $"Error: a check-by period must be 1 to {MemoryCheckBy.MaxDays} days."
            : null;

    /// <summary>An error when a space's default periods cannot be saved as given, or null.</summary>
    internal static string? CheckDefaultsError(IReadOnlyDictionary<string, int> defaults)
    {
        if (defaults.Count > IMemoryService.MaxCheckDefaults)
            return $"Error: at most {IMemoryService.MaxCheckDefaults} default check-by periods.";
        if (defaults.Keys.Any(a => string.IsNullOrWhiteSpace(a) || a.Trim().Length > 200))
            return "Error: every attribute needs a name of at most 200 characters.";
        if (defaults.Values.Any(d => d < 1 || d > MemoryCheckBy.MaxDays))
            return $"Error: a check-by period must be 1 to {MemoryCheckBy.MaxDays} days.";
        if (defaults.Keys.Select(PostgresMemoryStore.CheckKey).Distinct(StringComparer.Ordinal).Count() != defaults.Count)
            return "Error: two attributes differ only in case or spaces; give each once.";
        return null;
    }
}
