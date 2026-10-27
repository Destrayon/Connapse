using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Memory;

/// <summary>
/// Agent memory's rules over <see cref="IMemoryStore"/>. A source is readable when its document is (a link node:
/// when a document standing for it is), asked of <paramref name="access"/> on every call; nothing is cached.
/// <list type="bullet">
/// <item>A writer must be able to read every source it cites, so a fact cannot claim a source nobody gave it.</item>
/// <item>A reader sees a fact only when it can read every one of its sources, so citing one public document beside a
/// private one does not widen who sees it. A fact with no sources is seen only by the user who wrote it.</item>
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
        fact = Clean(fact);
        if (await UnreadableSourcesAsync(fact.Sources, caller, ct) is { } refused)
            return MemoryWriteResult.Failed(refused);

        MemoryFact written = await store.AddAsync(containerId, fact, caller, ct);
        await audit.LogAsync("memory.remembered", "memory_fact", written.Id.ToString(),
            new { ContainerId = containerId, caller.UserId, caller.AgentId, Sources = written.Sources.Count }, ct);
        return new MemoryWriteResult(written, null, null);
    }

    public async Task<MemoryWriteResult> CorrectAsync(
        Guid containerId, Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller caller, CancellationToken ct = default)
    {
        // A fact the caller cannot see is reported as missing, so a correction cannot probe for one.
        if (await VisibleFactAsync(containerId, factId, caller, ct) is not { } old)
            return MemoryWriteResult.Failed($"Fact '{factId}' not found.");
        // A blank subject or attribute keeps the old fact's.
        replacement = replacement with
        {
            Subject = string.IsNullOrWhiteSpace(replacement.Subject) ? old.Subject : replacement.Subject,
            Attribute = string.IsNullOrWhiteSpace(replacement.Attribute) ? old.Attribute : replacement.Attribute,
        };
        if (Validate(replacement) is { } invalid)
            return MemoryWriteResult.Failed(invalid);
        replacement = Clean(replacement);
        if (await UnreadableSourcesAsync(replacement.Sources, caller, ct) is { } refused)
            return MemoryWriteResult.Failed(refused);

        MemoryWriteResult result = await store.CorrectAsync(containerId, factId, kind, replacement, caller, ct);
        if (result.Fact is { } written)
        {
            await audit.LogAsync("memory.corrected", "memory_fact", written.Id.ToString(),
                new { ContainerId = containerId, Replaced = factId, Kind = kind.ToString(), caller.UserId, caller.AgentId }, ct);
        }
        return result;
    }

    public async Task<(int Deleted, string? Error)> ForgetAsync(Guid containerId, Guid factId, MemoryCaller caller, CancellationToken ct = default)
    {
        // Every version must be visible: corrections can cite other sources, and forgetting deletes them all. A chain
        // with any hidden version is reported as missing, so the answer does not reveal that it exists.
        async Task<bool> AllVisible(IReadOnlyList<MemoryFact> chain)
        {
            try
            {
                IReadOnlySet<MemorySource> readable = await ReadableAsync(chain.SelectMany(f => f.Sources), caller.UserId, ct);
                return chain.All(f => Visible(f, readable, caller));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not check memory sources; refusing the forget");
                return false;
            }
        }

        if (await store.ForgetAsync(containerId, factId, AllVisible, ct) is not { } deleted)
            return (0, $"Fact '{factId}' not found.");

        await audit.LogAsync("memory.forgotten", "memory_fact", factId.ToString(),
            new { ContainerId = containerId, Deleted = deleted, caller.UserId, caller.AgentId }, ct);
        return (deleted, null);
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

    internal static bool Visible(MemoryFact fact, IReadOnlySet<MemorySource> readable, MemoryCaller caller) =>
        fact.Sources.Count == 0
            ? caller.UserId is { } user && fact.Author.UserId == user
            : fact.Sources.All(readable.Contains);

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
    private async Task<string?> UnreadableSourcesAsync(IReadOnlyList<MemorySource> sources, MemoryCaller caller, CancellationToken ct)
    {
        IReadOnlySet<MemorySource> readable;
        try
        {
            readable = await ReadableAsync(sources, caller.UserId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not check memory sources; refusing the write");
            return "Error: could not check access to the sources; nothing was written.";
        }
        List<string> missing = [.. sources.Where(s => !readable.Contains(s)).Select(s => s.Ref).Distinct(StringComparer.Ordinal)];
        return missing.Count == 0
            ? null
            : $"Error: these sources were not found or you cannot read them: {string.Join(", ", missing)}. Cite only documents or link nodes you can read.";
    }

    /// <summary>The sources among <paramref name="sources"/> that <paramref name="userId"/> can read now. Throws when it cannot tell.</summary>
    private async Task<IReadOnlySet<MemorySource>> ReadableAsync(IEnumerable<MemorySource> sources, Guid? userId, CancellationToken ct)
    {
        MemorySource[] all = [.. sources.Distinct()];
        string[] nodes = [.. all.Where(s => s.Kind == MemorySourceKind.Node).Select(s => s.Ref).Distinct(StringComparer.Ordinal)];
        ILookup<string, string> nodeDocuments = await links.DocumentsOfAsync(nodes, ct);

        HashSet<string> documents = new(StringComparer.Ordinal);
        documents.UnionWith(all.Where(s => s.Kind == MemorySourceKind.Document).Select(s => s.Ref));
        foreach (string node in nodes)
            documents.UnionWith(nodeDocuments[node]);

        // Asked even when empty: a failing check must deny, not let an empty answer through.
        IReadOnlySet<string> open = await access.ReadableAsync(userId, documents, ct);
        return all.Where(s => s.Kind == MemorySourceKind.Document
                ? open.Contains(s.Ref)
                : nodeDocuments[s.Ref].Any(open.Contains))
            .ToHashSet();
    }

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
        if (fact.Sources.Count > MaxSources)
            return $"Error: at most {MaxSources} sources.";
        if (fact.Sources.Any(s => string.IsNullOrWhiteSpace(s.Ref) || s.Ref.Length > 512))
            return "Error: every source needs an id of at most 512 characters.";
        return null;
    }

    private static NewMemoryFact Clean(NewMemoryFact fact) => fact with
    {
        Subject = fact.Subject.Trim(),
        Attribute = fact.Attribute.Trim(),
        Value = fact.Value.Trim(),
        Reason = string.IsNullOrWhiteSpace(fact.Reason) ? null : fact.Reason.Trim(),
        Sources = [.. fact.Sources.Select(s => s with { Ref = s.Ref.Trim() }).Distinct()],
    };
}
