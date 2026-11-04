using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Memory;

/// <summary>
/// Agent memory's rules over <see cref="IMemoryStore"/>. A source is readable when its document is (a link node:
/// when it stands for at least one document and every one of them is, since one node can span files with different
/// permissions), asked of <paramref name="access"/> on every call; nothing is cached.
/// <list type="bullet">
/// <item>A writer must be able to read every source it cites, so a fact cannot claim a source nobody gave it.</item>
/// <item>A reader sees a fact only when it can read every one of its sources, so citing one public document beside a
/// private one does not widen who sees it, and only when the fact is team or the reader wrote it.</item>
/// <item>A correction keeps the fact's scope and, when it names no sources, the fact's sources. A correction of a team
/// fact needs write access and must cite every source the old fact cites, so it can never be seen more widely.</item>
/// <item>When access cannot be checked, reads show nothing and writes are refused.</item>
/// <item>Every write is audited by id, never by content, so forgetting leaves no copy behind.</item>
/// </list>
/// </summary>
public sealed class MemoryService(
    IMemoryStore store,
    IDocumentAccess access,
    ILinkIndex links,
    IAuditLogger audit,
    ILogger<MemoryService> logger) : IMemoryService
{
    internal const int MaxSources = 20;
    private const int Page = 200;

    public async Task<MemoryWriteResult> RememberAsync(Guid containerId, NewMemoryFact fact, MemoryCaller caller, CancellationToken ct = default)
    {
        if (Validate(fact) is { } invalid)
            return MemoryWriteResult.Failed(invalid);
        fact = Clean(fact) with { Scope = fact.ScopeOrDefault };
        if (await UnreadableSourcesAsync(fact.Sources!, caller, ct) is { } refused)
            return refused;

        MemoryFact written = await store.AddAsync(containerId, fact, caller, ct);
        await audit.LogAsync("memory.remembered", "memory_fact", written.Id.ToString(),
            new { ContainerId = containerId, caller.UserId, caller.AgentId, Sources = written.Sources.Count, Scope = written.Scope.ToString() }, ct);
        return new MemoryWriteResult(written, null, null);
    }

    public async Task<MemoryWriteResult> CorrectAsync(
        Guid containerId, Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller caller, bool teamWriter,
        CancellationToken ct = default)
    {
        // A fact the caller cannot see is reported as missing, so a correction cannot probe for one.
        if (await VisibleFactAsync(containerId, factId, caller, ct) is not { } old)
            return MemoryWriteResult.Failed($"Fact '{factId}' not found.", MemoryWriteFailure.NotFound);
        if (ScopeChangeError(old, replacement.Scope) is { } scopeChange)
            return scopeChange;
        // A blank subject or attribute keeps the old fact's, and sources not given keep the old fact's: an omitted list
        // read as "no sources" would drop a team fact's restriction and show the new value to every container reader.
        replacement = replacement with
        {
            Subject = string.IsNullOrWhiteSpace(replacement.Subject) ? old.Subject : replacement.Subject,
            Attribute = string.IsNullOrWhiteSpace(replacement.Attribute) ? old.Attribute : replacement.Attribute,
            Sources = replacement.Sources ?? old.Sources,
        };
        if (Validate(replacement) is { } invalid)
            return MemoryWriteResult.Failed(invalid);
        replacement = Clean(replacement);
        IReadOnlyList<MemorySource> sources = replacement.Sources!;
        if (await UnreadableSourcesAsync(sources, caller, ct) is { } refused)
            return refused;

        MemoryWriteResult result = await store.CorrectAsync(containerId, factId, kind, replacement, caller,
            async chain => await WholeChainVisibleAsync(factId, chain, sources, caller, ct)
                ?? TeamCorrectionError(chain.Single(f => f.Id == factId), sources, teamWriter), ct);
        if (result.Fact is { } written)
        {
            await audit.LogAsync("memory.corrected", "memory_fact", written.Id.ToString(),
                new { ContainerId = containerId, Replaced = factId, Kind = kind.ToString(), caller.UserId, caller.AgentId }, ct);
        }
        return result;
    }

    public async Task<(int Deleted, MemoryWriteFailure Failure)> ForgetAsync(
        Guid containerId, Guid factId, MemoryCaller caller, bool teamWriter, CancellationToken ct = default)
    {
        bool forbidden = false, sourcesUnchecked = false;

        // Every version must be visible: corrections can cite other sources, and forgetting deletes them all. A chain
        // with any hidden version is reported as missing, so the answer does not reveal that it exists.
        async Task<bool> AllVisible(IReadOnlyList<MemoryFact> chain)
        {
            try
            {
                IReadOnlySet<MemorySource> readable = await ReadableAsync(chain.SelectMany(f => f.Sources), caller.UserId, ct);
                if (!chain.All(f => Visible(f, readable, caller)))
                    return false;
                // Deleting a team version changes what everyone sees, so it needs write access; one's own personal ones don't.
                forbidden = !teamWriter && chain.Any(f => f.Scope == MemoryScope.Team);
                return !forbidden;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not check memory sources; refusing the forget");
                sourcesUnchecked = true;
                return false;
            }
        }

        if (await store.ForgetAsync(containerId, factId, AllVisible, ct) is not { } deleted)
            return (0, sourcesUnchecked ? MemoryWriteFailure.Unchecked : forbidden ? MemoryWriteFailure.Forbidden : MemoryWriteFailure.NotFound);

        await audit.LogAsync("memory.forgotten", "memory_fact", factId.ToString(),
            new { ContainerId = containerId, Deleted = deleted, caller.UserId, caller.AgentId }, ct);
        return (deleted, MemoryWriteFailure.None);
    }

    public async Task<MemoryRecall> RecallAsync(MemoryQuery query, MemoryCaller caller, CancellationToken ct = default)
    {
        int limit = Math.Clamp(query.Limit, 1, 100);
        List<RecalledFact> shown = [];
        try
        {
            // Hidden facts are skipped, so read further pages until the limit is met or memory runs out.
            for (int page = 0; shown.Count < limit; page++)
            {
                IReadOnlyList<MemoryFact> rows = await store.QueryAsync(query, page * Page, Page, ct);
                IReadOnlySet<MemorySource> readable = await ReadableAsync(rows.SelectMany(f => f.Sources), caller.UserId, ct);
                shown.AddRange(rows.Where(f => Visible(f, readable, caller)).Take(limit - shown.Count)
                    .Select(f => new RecalledFact(f, [])));
                if (rows.Count < Page)
                    break;
            }

            IReadOnlyDictionary<string, string> titles = await store.DocumentTitlesAsync(
                [.. shown.SelectMany(f => f.Fact.Sources).Where(s => s.Kind == MemorySourceKind.Document).Select(s => s.Ref)], ct);
            return new MemoryRecall([.. shown.Select(f => f with
            {
                Sources = [.. f.Fact.Sources.Select(s => new ShownMemorySource(s.Kind, s.Ref, titles.GetValueOrDefault(s.Ref)))],
            })]);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A check that cannot answer has not said "readable". Nothing is shown rather than a guess.
            logger.LogWarning(ex, "Could not check memory sources; showing nothing");
            return MemoryRecall.DeniedResult;
        }
    }

    /// <summary>
    /// Null when <paramref name="caller"/> can see every version of the chain, or the error refusing the correction.
    /// Forgetting deletes a whole chain and needs every version visible, so a correction by someone who cannot see them
    /// all could leave a chain no one can forget: A's private version beside B's hidden one. A corrector always sees its
    /// own version, so a chain built this way always has someone who sees all of it. The replacement's sources are
    /// checked again here, under the chain lock: a link node can gain a document the caller cannot read after the
    /// first check, and the new version must stay visible to whoever sees the rest.
    /// </summary>
    internal async Task<MemoryWriteResult?> WholeChainVisibleAsync(
        Guid factId, IReadOnlyList<MemoryFact> chain, IReadOnlyList<MemorySource> replacement, MemoryCaller caller, CancellationToken ct)
    {
        IReadOnlySet<MemorySource> readable;
        try
        {
            readable = await ReadableAsync(chain.SelectMany(f => f.Sources).Concat(replacement), caller.UserId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not check memory sources; refusing the write");
            return MemoryWriteResult.Failed("Error: could not check access to the sources; nothing was written.", MemoryWriteFailure.Unchecked);
        }
        if (!replacement.All(readable.Contains))
            return MemoryWriteResult.Failed("Error: you can no longer read every source you cited; nothing was written.");
        return chain.All(f => Visible(f, readable, caller))
            ? null
            : MemoryWriteResult.Failed($"Error: fact '{factId}' has versions you cannot see, so you cannot correct it. Remember a new fact instead.");
    }

    /// <summary>
    /// Null when a correction of <paramref name="old"/> (as it is under the chain lock) may cite
    /// <paramref name="sources"/>, or the error refusing it. A team fact's correction needs write access and must cite
    /// every source the old fact cites: leaving one out would show the new value to people that source is hidden from.
    /// </summary>
    /// <summary>
    /// Null when a correction asking for <paramref name="requested"/> keeps <paramref name="old"/>'s scope (or asks
    /// for none), or the error refusing it: a correction never changes who sees a fact.
    /// </summary>
    internal static MemoryWriteResult? ScopeChangeError(MemoryFact old, MemoryScope? requested) =>
        requested is { } scope && scope != old.Scope
            ? MemoryWriteResult.Failed("Error: a correction keeps the fact's scope. Remember a new fact with the scope you want instead.")
            : null;

    internal static MemoryWriteResult? TeamCorrectionError(MemoryFact old, IReadOnlyList<MemorySource> sources, bool teamWriter)
    {
        if (old.Scope != MemoryScope.Team)
            return null;
        if (!teamWriter)
            return MemoryWriteResult.Failed("Error: correcting a team fact needs write access (knowledge:write).", MemoryWriteFailure.Forbidden);
        List<string> dropped = [.. old.Sources.Where(s => !sources.Contains(s)).Select(s => s.Ref)];
        return dropped.Count == 0
            ? null
            : MemoryWriteResult.Failed($"Error: a correction of a team fact must keep every source the old fact cites; this one leaves out {string.Join(", ", dropped)}, "
                + "which would show the new value to people who cannot read it. To share it more widely, forget the fact and remember a new team fact.");
    }

    internal static bool Visible(MemoryFact fact, IReadOnlySet<MemorySource> readable, MemoryCaller caller) =>
        fact.Sources.All(readable.Contains) && (fact.Scope == MemoryScope.Team || caller.IsAuthorOf(fact.Author));

    /// <summary>The fact, when it exists and the caller may see it; otherwise null.</summary>
    private async Task<MemoryFact?> VisibleFactAsync(Guid containerId, Guid factId, MemoryCaller caller, CancellationToken ct)
    {
        if (await store.GetAsync(containerId, factId, ct) is not { } fact)
            return null;
        try
        {
            return Visible(fact, await ReadableAsync(fact.Sources, caller.UserId, ct), caller) ? fact : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not check memory sources; refusing the write");
            return null;
        }
    }

    /// <summary>An error naming the sources the writer cannot read (only ones it named itself), or null when it can read them all.</summary>
    private async Task<MemoryWriteResult?> UnreadableSourcesAsync(IReadOnlyList<MemorySource> sources, MemoryCaller caller, CancellationToken ct)
    {
        IReadOnlySet<MemorySource> readable;
        try
        {
            readable = await ReadableAsync(sources, caller.UserId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not check memory sources; refusing the write");
            return MemoryWriteResult.Failed("Error: could not check access to the sources; nothing was written.", MemoryWriteFailure.Unchecked);
        }
        List<string> missing = [.. sources.Where(s => !readable.Contains(s)).Select(s => s.Ref).Distinct(StringComparer.Ordinal)];
        return missing.Count == 0
            ? null
            : MemoryWriteResult.Failed($"Error: these sources were not found or you cannot read them: {string.Join(", ", missing)}. Cite only documents or link nodes you can read.");
    }

    /// <summary>The sources among <paramref name="sources"/> that <paramref name="userId"/> can read now. Throws when it cannot tell.</summary>
    private Task<IReadOnlySet<MemorySource>> ReadableAsync(IEnumerable<MemorySource> sources, Guid? userId, CancellationToken ct) =>
        MemorySourceAccess.ReadableAsync(access, links, sources, userId, ct);

    private static string? Validate(NewMemoryFact fact)
    {
        if (string.IsNullOrWhiteSpace(fact.Subject) || string.IsNullOrWhiteSpace(fact.Attribute) || string.IsNullOrWhiteSpace(fact.Value))
            return "Error: 'subject', 'attribute' and 'value' are required.";
        if (fact.Subject.Trim().Length > 200 || fact.Attribute.Trim().Length > 200)
            return "Error: 'subject' and 'attribute' must be at most 200 characters.";
        if (fact.Value.Trim().Length > 2000)
            return "Error: 'value' must be at most 2000 characters; store long text as a document and cite it.";
        if (fact.Reason?.Trim().Length > 1000)
            return "Error: 'reason' must be at most 1000 characters.";
        if (fact.ValidFrom is { } from && fact.ValidTo is { } to && to <= from)
            return "Error: 'validTo' must be after 'validFrom'.";
        if (fact.Scope is { } scope && !Enum.IsDefined(scope))
            return "Error: scope must be \"personal\" or \"team\".";
        IReadOnlyList<MemorySource> sources = fact.Sources ?? [];
        if (sources.Count > MaxSources)
            return $"Error: at most {MaxSources} sources.";
        if (sources.Any(s => string.IsNullOrWhiteSpace(s.Ref) || s.Ref.Length > 512))
            return "Error: every source needs an id of at most 512 characters.";
        return null;
    }

    private static NewMemoryFact Clean(NewMemoryFact fact) => fact with
    {
        Subject = fact.Subject.Trim(),
        Attribute = fact.Attribute.Trim(),
        Value = fact.Value.Trim(),
        Reason = string.IsNullOrWhiteSpace(fact.Reason) ? null : fact.Reason.Trim(),
        Sources = [.. (fact.Sources ?? []).Select(s => s with { Ref = s.Ref.Trim() }).Distinct()],
    };
}
