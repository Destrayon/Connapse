using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Memory;

/// <summary>
/// What copying a fact would do: the current version that would be copied, the destination, and a current fact the
/// destination already holds with the same subject and attribute, if the caller can see one.
/// </summary>
public sealed record MemoryCopyPlan(RecalledFact Current, Container Destination, RecalledFact? Existing);

/// <summary>A fact copied to a team space or container: the new team fact, and the personal version it was copied from.</summary>
public sealed record MemoryCopied(MemoryFact Copy, Guid CopiedFrom, Container SavedTo);

/// <summary>
/// Copies the current version of a fact in the caller's own personal space to a team space or document container.
/// Only that version goes, as a new fact with a new id whose author is the copier, and it is saved the normal way, so
/// the copier must still read every document it cites. The personal fact is left as it was, and the two are not
/// linked. A destination already holding a current fact with the same subject and attribute is refused unless
/// <c>anyway</c> is set; nothing in the destination is overwritten. The audit log records both ids, never the content.
/// </summary>
[Operation("memory.copy", Scope = MemoryRules.WriteScope, Risk = RiskTier.Write)]
public sealed class CopyMemoryFact(
    IContainerStore containers, IMemoryService memory, RememberFact remember, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    /// <summary>The most facts with the copied fact's subject looked through for one with the same attribute.</summary>
    private const int ExistingScan = 500;

    /// <summary>Team spaces and document containers the caller can copy to, A to Z; empty when they can't write.</summary>
    public async Task<IReadOnlyList<Container>> DestinationsAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await MemoryRules.WriterAsync(authorizer, context))
            return [];
        IReadOnlyList<Container> all = await containers.ListAsync(0, ListContainers.MaxTake, MemoryRules.Caller(context.User), ct);
        return [.. all.Where(c => c.Audience == MemoryScope.Team).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>What a copy of <paramref name="factId"/> to <paramref name="destination"/> would hold, without copying.</summary>
    public async Task<Attempt<MemoryCopyPlan, MemoryStatus>> PlanAsync(
        OperationContext context, string factId, string destination, CancellationToken ct = default)
    {
        if (!await MemoryRules.WriterAsync(authorizer, context))
            return MemoryRules.Fail<MemoryCopyPlan>(MemoryStatus.Forbidden, "copying memory needs write access (knowledge:write).");
        MemoryCaller caller = MemoryRules.Caller(context.User);

        if (!Guid.TryParse(factId, out Guid id) || await memory.SpaceOfAsync(id, ct) is not { } spaceId
            || await containers.GetAsync(spaceId, caller, ct) is not { } space)
            return MemoryRules.FactNotFound<MemoryCopyPlan>(factId);
        if (space.Kind != ContainerKind.Memory || space.Audience != MemoryScope.Personal)
            return MemoryRules.Fail<MemoryCopyPlan>(MemoryStatus.Invalid,
                $"'{space.Name}' is already shared. Only a fact in one of your personal spaces can be copied to a team.", "id");

        MemoryHistoryPage newest = await memory.HistoryAsync(spaceId, id, null, 1, caller, ct);
        if (newest.Denied)
            return MemoryRules.Fail<MemoryCopyPlan>(MemoryStatus.Unchecked, "access to the fact's sources could not be checked.");
        if (newest.NotFound || newest.Versions.Count == 0)
            return MemoryRules.FactNotFound<MemoryCopyPlan>(factId);
        RecalledFact current = newest.Versions[0];

        (Container? to, string? ambiguous) = await MemoryRules.SpaceAsync(containers, destination, caller, ct);
        if (ambiguous is not null)
            return MemoryRules.Fail<MemoryCopyPlan>(MemoryStatus.Invalid, ambiguous, "destination");
        if (to is null)
            return MemoryRules.WriteContainerNotFound<MemoryCopyPlan>(destination);
        if (to.Audience != MemoryScope.Team)
            return MemoryRules.Fail<MemoryCopyPlan>(MemoryStatus.Invalid,
                $"'{to.Name}' is a personal space. Copy to a team space or a document container.", "destination");

        return Attempt<MemoryCopyPlan, MemoryStatus>.Succeed(
            new MemoryCopyPlan(current, to, await ExistingAsync(Guid.Parse(to.Id), current.Fact, caller, ct)));
    }

    /// <summary>
    /// Copies the current version of <paramref name="factId"/> to <paramref name="destination"/>. With a matching
    /// current fact already there, refused unless <paramref name="anyway"/>.
    /// </summary>
    public async Task<Attempt<MemoryCopied, MemoryStatus>> RunAsync(
        OperationContext context, string factId, string destination, bool anyway = false, CancellationToken ct = default)
    {
        Attempt<MemoryCopyPlan, MemoryStatus> planned = await PlanAsync(context, factId, destination, ct);
        if (!planned.Succeeded)
            return Attempt<MemoryCopied, MemoryStatus>.Fail(planned.Status, planned.Detail!, planned.Field);
        (RecalledFact current, Container to, RecalledFact? existing) = planned.Result!;
        if (existing is not null && !anyway)
            return MemoryRules.Fail<MemoryCopied>(MemoryStatus.Exists,
                $"'{to.Name}' already has a current fact for {existing.Fact.Subject} {existing.Fact.Attribute} ({existing.Fact.Id}). "
                + "Set anyway to copy it as well; nothing there is changed.", "anyway");

        MemoryFact fact = current.Fact;
        Attempt<MemoryWritten, MemoryStatus> saved = await remember.RunAsync(context, MemoryTarget.Space(to.Id),
            new NewMemoryFact(fact.Subject, fact.Attribute, fact.Value, fact.ValidFrom, fact.ValidTo, fact.Sources, fact.Reason), ct);
        if (!saved.Succeeded)
            return Attempt<MemoryCopied, MemoryStatus>.Fail(saved.Status, saved.Detail!, saved.Field);

        MemoryFact copy = saved.Result!.Fact;
        await audit.LogAsync("memory_fact.copied", "memory_fact", copy.Id.ToString(),
            new { CopiedFrom = fact.Id, Copy = copy.Id, Destination = to.Id }, ct);
        return Attempt<MemoryCopied, MemoryStatus>.Succeed(new MemoryCopied(copy, fact.Id, to));
    }

    /// <summary>A current fact in the destination with the same subject and attribute that the caller can see, or null.</summary>
    private async Task<RecalledFact?> ExistingAsync(Guid destination, MemoryFact fact, MemoryCaller caller, CancellationToken ct)
    {
        MemoryFactFilter filter = new(Subject: fact.Subject, Status: MemoryFactStatus.Current);
        MemoryFactKey? after = null;
        int scanned = 0;
        do
        {
            MemoryFactPage page = await memory.ListAsync(destination, filter, MemoryFactSort.Subject, after, 100, caller, ct);
            if (page.Facts.FirstOrDefault(f => string.Equals(f.Fact.Attribute, fact.Attribute, StringComparison.OrdinalIgnoreCase)) is { } same)
                return same;
            scanned += page.Facts.Count;
            after = page.Next;
        }
        while (after is not null && scanned < ExistingScan);
        return null;
    }
}
