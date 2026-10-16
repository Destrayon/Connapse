namespace Connapse.CustomSource.Conformance;

/// <summary>What to test and how to reach it.</summary>
public sealed record ConformanceOptions
{
    /// <summary>The middle man's address, as entered in Connapse.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>The bearer token the middle man expects.</summary>
    public required string Token { get; init; }

    /// <summary>The scope to test; the first scope offered when null.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// A Connapse user ID the middle man maps to an account that can read at least one document in the scope.
    /// Without it, the checks that need a reader are skipped.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>The link secret Connapse generated for this provider; linking checks are skipped without it.</summary>
    public string? LinkSecret { get; init; }

    /// <summary>The <c>aud</c> link tokens carry: Connapse's ID for this provider.</summary>
    public string? ProviderId { get; init; }

    /// <summary>The <c>iss</c> link tokens carry: Connapse's address.</summary>
    public string? Issuer { get; init; }
}

/// <summary>
/// What only the middle man's own system can do. The suite calls these to make a change it can then look for
/// in the change feed, and to finish a sign-in on the linking page. A hook that returns false (or null) is
/// unsupported, and the checks that need it are skipped rather than failed.
/// </summary>
public interface IConformanceHooks
{
    /// <summary>Whether both <see cref="CreateDocumentAsync"/> and <see cref="DeleteDocumentAsync"/> work: the change checks create nothing without both.</summary>
    bool CanChangeDocuments { get; }

    /// <summary>
    /// Adds a new readable document to the scope and returns the ID the middle man gives it. The suite deletes
    /// only IDs returned here, never a document it merely did not see before.
    /// </summary>
    Task<string?> CreateDocumentAsync(string scope, CancellationToken ct);

    /// <summary>Deletes the document with this ID from the scope.</summary>
    Task<bool> DeleteDocumentAsync(string scope, string documentId, CancellationToken ct);

    /// <summary>
    /// Presents <paramref name="linkToken"/> to the linking page at <paramref name="linkUrl"/> as a browser would,
    /// finishes the middle man's sign-in, and returns the <c>code</c> it put on <paramref name="returnUrl"/>.
    /// </summary>
    Task<string?> CompleteSignInAsync(Uri linkUrl, string linkToken, Uri returnUrl, CancellationToken ct);
}

/// <summary>Hooks for a middle man the suite cannot drive: every check that needs one is skipped.</summary>
public sealed class NoHooks : IConformanceHooks
{
    public bool CanChangeDocuments => false;
    public Task<string?> CreateDocumentAsync(string scope, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<bool> DeleteDocumentAsync(string scope, string documentId, CancellationToken ct) => Task.FromResult(false);
    public Task<string?> CompleteSignInAsync(Uri linkUrl, string linkToken, Uri returnUrl, CancellationToken ct) => Task.FromResult<string?>(null);
}

public enum CheckOutcome { Pass, Fail, Skip }

/// <param name="Id">A stable name, such as <c>listing.page-size</c>.</param>
/// <param name="Title">What the protocol requires, in a sentence.</param>
/// <param name="Detail">Why it failed or was skipped.</param>
public sealed record CheckResult(string Id, string Title, CheckOutcome Outcome, string? Detail = null);

/// <summary>Thrown inside a check: the middle man broke the requirement.</summary>
public sealed class CheckFailedException(string message) : Exception(message);

/// <summary>Thrown inside a check: it cannot run here (a capability or option is missing).</summary>
public sealed class CheckSkippedException(string message) : Exception(message);
