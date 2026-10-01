namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// Atlassian answered 429. Not a failure of the request itself: the caller keeps what it has and
/// tries again after <see cref="RetryAfter"/> when the response named one.
/// </summary>
public sealed class AtlassianRateLimitedException(TimeSpan? retryAfter)
    : Exception($"Atlassian is rate limiting this site{(retryAfter is { } wait ? $"; retry after {(int)wait.TotalSeconds} seconds" : "")}.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Atlassian refused the service-account credentials: the token endpoint rejected them, or the API
/// answered 401 even with a freshly issued token.
/// </summary>
public sealed class AtlassianAuthException(string message) : Exception(message);

/// <summary>
/// Atlassian answered 200 with a body that is not the shape asked for: a listing with no
/// <c>results</c> array, a null entry, or an entry without a valid id or version. Treated as a
/// failed request, never as an empty or shorter answer, so it cannot read as deletions.
/// </summary>
public sealed class AtlassianMalformedResponseException(string message) : Exception(message);

/// <summary>
/// A download redirected somewhere Connapse will not follow: off Atlassian's hosts, over plain
/// HTTP, or past the hop limit. Permanent, since the same redirect comes back on a retry. Its own
/// type so callers can tell it from every other <see cref="InvalidOperationException"/>, which
/// includes <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class AtlassianRedirectRefusedException()
    : InvalidOperationException("Refusing to follow a download redirect outside Atlassian.");
