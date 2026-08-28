using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Pins the fail-closed wiring in <c>HybridSearchService.SearchAsync</c> that
/// <see cref="ScopeResolutionGuardTests"/> cannot reach, because that class tests the pure
/// <c>ScopeResolution.Guard</c> function and has no way to observe whether the service actually
/// calls it.
/// </summary>
/// <remarks>
/// A behavioural test would need a database and a custom service provider to drive
/// <c>HybridSearchService.SearchAsync</c> end to end -- <c>HybridSearchServiceTests</c> already
/// documents that as exactly why service-delegation is not unit tested there, and the same reasoning
/// applies to this wiring. So these tests read the service's source text instead: each one fails if a
/// specific line that makes fail-closed real is deleted or weakened. None of them can tell whether
/// <c>ScopeResolution.Guard</c> itself behaves correctly once it is called -- that is
/// <see cref="ScopeResolutionGuardTests"/>' job, not this file's.
/// </remarks>
[Trait("Category", "Unit")]
public class FailClosedWiringTests
{
    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repository root (marked by Connapse.slnx) above " +
                AppContext.BaseDirectory);
    }

    private static string ReadHybridSearchServiceSource()
    {
        string path = Path.Combine(
            RepositoryRoot(), "src", "Connapse.Search", "Hybrid", "HybridSearchService.cs");

        // A missing file must fail loudly rather than be read as "", which every Contains
        // assertion below would then fail honestly on -- but a test that could not find its
        // subject and still reported a clean result would be worse than no test at all.
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Expected to find HybridSearchService.cs at '{path}' but it was not there. " +
                "These tests pin its source text and cannot run without it.");

        return File.ReadAllText(path);
    }

    private static string Source => ReadHybridSearchServiceSource();

    [Fact]
    public void SearchAsync_Source_CallsScopeResolutionGuard()
    {
        // Pins presence, not correctness: this fails if the Guard call is deleted (for example,
        // someone "simplifies" the try/catch back to a bare await and drops the line after it), but
        // it would still pass if Guard were called with the wrong arguments or its own logic were
        // wrong. Argument correctness and Guard's rule are ScopeResolutionGuardTests' job.
        Source.Should().Contain("ScopeResolution.Guard(scopes, options.UserId)");
    }

    [Fact]
    public void SearchAsync_Source_AssignsFailedScopesInsideACatchBlock()
    {
        // Pins that a resolver failure still produces SearchScopes.Failed rather than an unfiltered
        // search or a bare rethrow. Checking the two substrings' relative order is a best-effort
        // proxy for "inside the same catch block" -- a source-text test cannot parse C#, so it
        // cannot fully rule out someone moving the assignment out of the catch while leaving both
        // strings present elsewhere in the file.
        int catchIndex = Source.IndexOf(
            "catch (Exception ex) when (ex is not OperationCanceledException)",
            StringComparison.Ordinal);
        int assignIndex = Source.IndexOf(
            "scopes = SearchScopes.Failed;", StringComparison.Ordinal);

        catchIndex.Should().BeGreaterThanOrEqualTo(0, "the catch clause must still exist");
        assignIndex.Should().BeGreaterThan(
            catchIndex, "the Failed assignment must sit inside that catch, not before it");
    }

    [Fact]
    public void SearchAsync_Source_CatchFilterStillExcludesOperationCanceledException()
    {
        // The filter is what stops a genuine cancellation from being converted into a denial and
        // logged as a resolver error. Pinning the exact clause catches someone widening the catch to
        // plain "catch (Exception ex)" -- it does not catch a filter that is present but subtly
        // wrong (for example, checking the wrong type), since that would be a different string that
        // this assertion never looks for.
        Source.Should().Contain("when (ex is not OperationCanceledException)");
    }
}
