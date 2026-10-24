namespace Connapse.Application;

/// <summary>
/// What an operation returns: a status from the operation's own enum, and the result when it
/// succeeded. Each status enum's zero value is its success; every other value is a reason the
/// operation did not do what was asked, which REST maps to a ProblemDetails response, MCP to an
/// error result, and the UI to a message.
/// </summary>
public sealed record Attempt<TResult, TStatus>
    where TStatus : struct, Enum
{
    private Attempt(TStatus status, TResult? result)
    {
        Status = status;
        Result = result;
    }

    public TStatus Status { get; }

    /// <summary>The operation's result; set only when <see cref="Succeeded"/>.</summary>
    public TResult? Result { get; }

    public bool Succeeded => EqualityComparer<TStatus>.Default.Equals(Status, default);

    public static Attempt<TResult, TStatus> Succeed(TResult result) => new(default, result);

    /// <summary>A failed attempt; <paramref name="status"/> must not be the success value.</summary>
    public static Attempt<TResult, TStatus> Fail(TStatus status)
    {
        if (EqualityComparer<TStatus>.Default.Equals(status, default))
            throw new ArgumentException($"{status} is {typeof(TStatus).Name}'s success value, so it can't describe a failure.", nameof(status));
        return new(status, default);
    }
}
