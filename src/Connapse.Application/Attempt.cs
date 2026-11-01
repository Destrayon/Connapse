namespace Connapse.Application;

/// <summary>
/// What an operation returns: a status from the operation's own enum, and the result when it
/// succeeded. Each status enum's zero value is its success; every other value is a reason the
/// operation did not do what was asked, which REST maps to a ProblemDetails response, MCP to an
/// error result, and the UI to a message.
/// </summary>
/// <remarks>
/// A status enum must name its zero value Success and must not be [Flags]; the first use of an
/// Attempt over any other enum throws, so a failure can never read as a success.
/// </remarks>
public sealed record Attempt<TResult, TStatus>
    where TStatus : struct, Enum
{
    static Attempt()
    {
        if (typeof(TStatus).IsDefined(typeof(FlagsAttribute), inherit: false))
            throw new InvalidOperationException($"{typeof(TStatus).Name} is a [Flags] enum; an operation status is a single outcome.");
        if (Enum.GetName(default(TStatus)) != "Success")
            throw new InvalidOperationException($"{typeof(TStatus).Name} must name its zero value Success, because a status of zero is what Attempt reads as success.");
    }

    private Attempt(TStatus status, TResult? result, string? detail, string? field)
    {
        Status = status;
        Result = result;
        Detail = detail;
        Field = field;
    }

    public TStatus Status { get; }

    /// <summary>The operation's result; set only when <see cref="Succeeded"/>.</summary>
    public TResult? Result { get; }

    /// <summary>What went wrong this time, in words a person can act on; set only on a failure, and never exception text.</summary>
    public string? Detail { get; }

    /// <summary>The input at fault, when one is ("name", "scopeId"), so a form can show the error beside it.</summary>
    public string? Field { get; }

    public bool Succeeded => EqualityComparer<TStatus>.Default.Equals(Status, default);

    public static Attempt<TResult, TStatus> Succeed(TResult result) => new(default, result, null, null);

    /// <summary>A failed attempt; <paramref name="status"/> must not be the success value.</summary>
    public static Attempt<TResult, TStatus> Fail(TStatus status, string? detail = null, string? field = null)
    {
        if (EqualityComparer<TStatus>.Default.Equals(status, default))
            throw new ArgumentException($"{status} is {typeof(TStatus).Name}'s success value, so it can't describe a failure.", nameof(status));
        return new(status, default, detail, field);
    }
}
