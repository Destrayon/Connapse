namespace Connapse.Application;

/// <summary>
/// Describes one failure value of an operation status enum: the HTTP status REST answers with,
/// and the problem it is. <paramref name="slug"/> names the problem's entry in docs/problems.md
/// and becomes the stable ProblemDetails <c>type</c>, so it must never change once shipped;
/// <paramref name="title"/> is the same for every occurrence, and per-occurrence text goes in
/// the detail instead.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class FailureAttribute(int httpStatus, string slug, string title) : Attribute
{
    public int HttpStatus { get; } = httpStatus;

    public string Slug { get; } = slug;

    public string Title { get; } = title;
}
