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
/// <paramref name="CheckBy"/> is when the fact should be checked against its real source again (null: never), and
/// <paramref name="CheckAfterDays"/> the period a confirm or a re-save sets it to from then. <paramref name="Unconfirmed"/>:
/// the fact is current and past its check-by date, as of when it was read. It is still a fact: being unconfirmed never
/// hides or ends it (CON-340).
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
    MemoryScope Scope = MemoryScope.Personal,
    DateTimeOffset? CheckBy = null,
    int? CheckAfterDays = null,
    DateTimeOffset? ConfirmedAt = null,
    bool Unconfirmed = false);

/// <summary>
/// When a saved fact should be checked again (CON-340): <paramref name="At"/> a set date, <paramref name="Days"/> a period
/// from when it is saved or confirmed, or neither for never. A set date also resets by the whole days it lay ahead.
/// </summary>
public sealed record MemoryCheckBy(DateTimeOffset? At, int? Days)
{
    /// <summary>The longest period a check may be put off: ten years.</summary>
    public const int MaxDays = 3650;

    /// <summary>Never checked: the fact has no check-by date.</summary>
    public static MemoryCheckBy Never { get; } = new(null, null);

    /// <summary>Checked <paramref name="days"/> after it is saved or confirmed.</summary>
    public static MemoryCheckBy After(int days) => new(null, days);

    public bool IsNever => At is null && Days is null;

    /// <summary>The check-by date and reset period for a fact saved at <paramref name="now"/>.</summary>
    public (DateTimeOffset? CheckBy, int? Days) From(DateTimeOffset now) => At is { } at
        ? (at, Math.Clamp((int)Math.Ceiling((at - now).TotalDays), 1, MaxDays))
        : Days is { } days ? (now.AddDays(days), days) : (null, null);
}

/// <summary>
/// A fact to write. Dates are when it became (and stopped being) true; null means not known or still open.
/// <paramref name="Sources"/> null means none were given: a new fact has none, and a correction keeps the old fact's.
/// <paramref name="Scope"/> null on a new fact means the scope its sources gave before scopes existed (team with
/// sources, personal without); a correction always keeps the old fact's scope. <paramref name="Check"/> null means the
/// space's default for the attribute (a correction without one: that, else the old fact's period).
/// </summary>
public sealed record NewMemoryFact(
    string Subject,
    string Attribute,
    string Value,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    IReadOnlyList<MemorySource>? Sources,
    string? Reason = null,
    MemoryScope? Scope = null,
    MemoryCheckBy? Check = null)
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
/// <paramref name="Check"/> keeps only the facts past their check-by date, or only those that aren't.
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
    MemoryFactCheck Check = MemoryFactCheck.Any);

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

/// <summary>The outcome of a write: the fact written (and the one it replaced), or why nothing was written.</summary>
public sealed record MemoryWriteResult(MemoryFact? Fact, MemoryFact? Replaced, string? Error, MemoryWriteFailure Failure = MemoryWriteFailure.None)
{
    public static MemoryWriteResult Failed(string error, MemoryWriteFailure failure = MemoryWriteFailure.Invalid) => new(null, null, error, failure);
}

/// <summary>
/// Why a write was refused: the fact named is missing (or hidden), the write is invalid, access could not be checked,
/// or the caller lacks the write access a team fact needs.
/// </summary>
public enum MemoryWriteFailure { None = 0, Invalid = 1, NotFound = 2, Unchecked = 3, Forbidden = 4 }
