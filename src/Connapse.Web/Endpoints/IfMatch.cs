using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Reads an <c>If-Match</c> header (RFC 9110 §13.1.1) into the versions a write may go ahead against, with
/// ASP.NET Core's <see cref="EntityTagHeaderValue"/> parser. If-Match uses strong comparison, so a weak tag
/// never matches anything; <c>*</c> matches any current representation, and settings always have one.
/// </summary>
public static class IfMatch
{
    /// <summary>
    /// False when the header is present but isn't a list of entity tags. Otherwise <paramref name="versions"/> is
    /// null when the write is unconditional (no header, or <c>*</c>), or the strong tags' values, unquoted.
    /// </summary>
    public static bool TryRead(StringValues header, out IReadOnlyCollection<string>? versions)
    {
        versions = null;
        if (header.Count == 0)
            return true;
        if (!EntityTagHeaderValue.TryParseList(header, out var tags) || tags.Count == 0)
            return false;
        if (tags.Any(t => t.Equals(EntityTagHeaderValue.Any)))
            return true;

        versions = tags.Where(t => !t.IsWeak).Select(t => t.Tag.ToString()[1..^1]).ToList();
        return true;
    }

    /// <summary>The ETag header value for a version.</summary>
    public static string ETag(string version) => $"\"{version}\"";
}
