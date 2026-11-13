namespace Connapse.Core;

/// <summary>What a memory source names: a Connapse document, or a link node (a ticket, a PR, a function) that documents stand for.</summary>
public enum MemorySourceKind { Document = 0, Node = 1 }

/// <summary>One thing a fact was learned from. <paramref name="Ref"/> is a document id or a link node id.</summary>
public sealed record MemorySource(MemorySourceKind Kind, string Ref);

/// <summary>
/// Who sees a fact. <see cref="Personal"/>: only its author, meaning the user and the agents acting for that user (an
/// agent with no user: that agent). <see cref="Team"/>: everyone who can read the container. Either way a reader must
/// also be able to read every source the fact cites.
/// </summary>
public enum MemoryScope { Personal = 0, Team = 1 }

/// <summary>A source as a reader is shown it, with the document's name when it is one.</summary>
public sealed record ShownMemorySource(MemorySourceKind Kind, string Ref, string? Title);

/// <summary>
/// Who wrote a fact or is asking for one. <paramref name="UserId"/> is the person (for an agent, the user it acts
/// for), set by the server from the authenticated caller, never from a request body.
/// </summary>
public sealed record MemoryCaller(Guid? UserId, Guid? AgentId, string? Name)
{
    /// <summary>
    /// Whether this caller counts as <paramref name="author"/>: the same user, or, for an author with no user, the same
    /// agent also acting for no user.
    /// </summary>
    public bool IsAuthorOf(MemoryCaller author) =>
        author.UserId is { } user ? UserId == user : UserId is null && author.AgentId is { } agent && AgentId == agent;
}

/// <summary>
/// One fact as memory holds it. <paramref name="ValidFrom"/> and <paramref name="ValidTo"/> are when it was true
/// in the world (null: open); <paramref name="RecordedAt"/> and <paramref name="SupersededAt"/> are when memory
/// held it (superseded: a correction said it was wrong). Every fact of one correction chain shares
/// <paramref name="ChainId"/>, which is what forgetting deletes. <paramref name="Scope"/> is stored on each version.
/// </summary>
public sealed record MemoryFact(
    Guid Id,
    Guid ChainId,
    Guid ContainerId,
    string Subject,
    string Attribute,
    string Value,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt,
    DateTimeOffset? SupersededAt,
    Guid? ReplacesId,
    string? Reason,
    MemoryCaller Author,
    IReadOnlyList<MemorySource> Sources,
    MemoryScope Scope = MemoryScope.Personal);

/// <summary>
/// A fact to write. Dates are when it became (and stopped being) true; null means not known or still open.
/// <paramref name="Sources"/> null means none were given: a new fact has none, and a correction keeps the old fact's.
/// <paramref name="Scope"/> null on a new fact means the scope its sources gave before scopes existed (team with
/// sources, personal without); a correction always keeps the old fact's scope.
/// </summary>
public sealed record NewMemoryFact(
    string Subject,
    string Attribute,
    string Value,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    IReadOnlyList<MemorySource>? Sources,
    string? Reason = null,
    MemoryScope? Scope = null)
{
    /// <summary>The scope a new fact is saved with.</summary>
    public MemoryScope ScopeOrDefault => Scope ?? (Sources is { Count: > 0 } ? MemoryScope.Team : MemoryScope.Personal);
}

/// <summary>
/// How a correction treats the fact it replaces. <see cref="Changed"/>: the world changed, so the old fact ends
/// where the new one starts. <see cref="Wrong"/>: the old fact was never right, so it is superseded and the new one
/// takes its dates. Either way the old fact is kept.
/// </summary>
public enum MemoryCorrection { Changed = 0, Wrong = 1 }

/// <summary>
/// Which facts to read. With <paramref name="AsOf"/> null and <paramref name="History"/> false, the facts true now;
/// with <paramref name="AsOf"/>, the facts true then. <paramref name="History"/> also returns ended and superseded
/// facts. <paramref name="ChangedFrom"/>/<paramref name="ChangedTo"/> keep facts that started or ended in that window.
/// <paramref name="ChainId"/> keeps one fact's versions, every one of them (a fact's history).
/// <paramref name="Order"/> <see cref="MemoryRecallOrder.Newest"/> returns the latest start dates first.
/// </summary>
public sealed record MemoryQuery(
    Guid ContainerId,
    string? Text = null,
    string? Subject = null,
    string? Attribute = null,
    DateTimeOffset? AsOf = null,
    bool History = false,
    DateTimeOffset? ChangedFrom = null,
    DateTimeOffset? ChangedTo = null,
    int Limit = 10,
    Guid? ChainId = null,
    MemoryRecallOrder Order = MemoryRecallOrder.Usual);

/// <summary>
/// How recalled facts are ordered. <see cref="Usual"/>: as recall always has, by relevance when there is text, else by
/// when they were recorded or, for history and windows, by start date. <see cref="Newest"/>: the latest start date
/// first (facts with none last), ties newest recorded first, so a small limit keeps the facts that took effect last.
/// A recall with text keeps its relevance ranking and ignores <see cref="Newest"/>.
/// </summary>
public enum MemoryRecallOrder { Usual = 0, Newest = 1 }

/// <summary>
/// A fact as one reader may see it. Only facts whose every source the reader can read are returned, and a personal
/// fact only to its author.
/// </summary>
public sealed record RecalledFact(MemoryFact Fact, IReadOnlyList<ShownMemorySource> Sources);

/// <summary>What a read found. <paramref name="Denied"/> is true when access could not be checked, and then nothing else is returned.</summary>
public sealed record MemoryRecall(IReadOnlyList<RecalledFact> Facts, bool Denied = false)
{
    public static MemoryRecall DeniedResult { get; } = new([], Denied: true);
}

/// <summary>
/// The outcome of a write: the fact written (and the one it replaced), or why nothing was written.
/// <paramref name="Existing"/>, on a new fact, lists other facts with its subject and attribute that the writer can see
/// and that are current, so a writer that meant to replace one can correct it (CON-341); empty when there are none.
/// </summary>
public sealed record MemoryWriteResult(
    MemoryFact? Fact, MemoryFact? Replaced, string? Error, MemoryWriteFailure Failure = MemoryWriteFailure.None,
    IReadOnlyList<MemoryFact>? Existing = null)
{
    public static MemoryWriteResult Failed(string error, MemoryWriteFailure failure = MemoryWriteFailure.Invalid) => new(null, null, error, failure);
}

/// <summary>
/// Why a write was refused: the fact named is missing (or hidden), the write is invalid, access could not be checked,
/// or the caller lacks the write access a team fact needs.
/// </summary>
public enum MemoryWriteFailure { None = 0, Invalid = 1, NotFound = 2, Unchecked = 3, Forbidden = 4 }
