# Per-user AWS search permissions — 5a, enforcement correctness

> ## ⚠️ Executed and superseded — do not implement from this document
>
> This plan was carried out on 2026-08-27 and is kept as a record of what was done and why. **Two
> of its instructions are now wrong, and following them would reintroduce a defect this work
> removed:**
>
> - It specifies the search predicate as `resource_uri IS NOT NULL AND (…)`, treating a document
>   with no cloud coordinate as **denied**. Manual testing found that would have hidden 127,892 of
>   127,898 documents on a real deployment, because uploads have no external address by design and
>   only the S3 and Azure connectors ever record one. The shipped rule is the opposite: a document
>   with no coordinate is **not governed by cloud permissions** and falls back to Connapse's own
>   access control.
> - It specifies the coordinate report as covering **every** source. The shipped report covers only
>   sources whose connector can record a coordinate (S3 and Azure). For SFTP, filesystem and MinIO
>   the advice it gave — re-sync — could never work.
>
> `docs/superpowers/specs/2026-08-27-per-user-aws-search-permissions-design.md` is the current
> description of the design. Read that instead.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the already-merged permission filter correct and honest before any cloud resolver is wired to it — close a wildcard leak in grant scopes, distinguish the four ways a search can come back empty, refuse to answer for a caller with no user, and stop the feature from silently hiding every document indexed before coordinates existed.

**Architecture:** Nothing here talks to AWS. `ISearchScopeResolver` and the prefix predicates in both search stores already exist from phase 4; this sub-phase fixes what they do with an answer. A new `GrantScope` type in `Connapse.Core` turns an S3 grant scope string into a match rule, `SearchScopes` learns to carry exact matches alongside prefixes and to say *why* it is empty, `HybridSearchService` fails closed, and a new report names sources holding documents with no recorded coordinate.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, NSubstitute, EF Core with Npgsql, Testcontainers for integration tests, Blazor Server.

**Spec:** `docs/superpowers/specs/2026-08-27-per-user-aws-search-permissions-design.md`

## Global Constraints

- .NET 10, file-scoped namespaces, nullable enabled, implicit usings.
- Records for DTOs; primary constructors for DI.
- Async all the way — never `.Result` or `.Wait()`.
- Parameterized SQL only. Never string interpolation of values into SQL.
- Do not use `var` for primitive types.
- Do not use `dynamic`.
- Always use `IDbContextFactory<T>` and short-lived contexts: `await using var ctx = await factory.CreateDbContextAsync(ct)`. Never share a scoped `DbContext` across threads.
- Tag every test `[Trait("Category", "Unit")]` or `[Trait("Category", "Integration")]`.
- Test naming: `MethodName_Scenario_ExpectedResult`.
- Wrap user-controlled values in logs with `LogSanitizer.Sanitize(...)` from `Connapse.Core.Utilities`.
- Commit messages: `<type>: <summary>` using `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `perf:`, `chore:`.
- Branch already exists: `docs/421-per-user-permissions-design`. Create `feature/421-enforcement-correctness` off `main` for this work.

## File Structure

| File | Responsibility |
|---|---|
| `src/Connapse.Core/Models/GrantScope.cs` *(new)* | Turn an S3 grant scope string into a `GrantMatch` — the value to compare and whether the comparison is exact or prefix. |
| `tests/Connapse.Core.Tests/Search/GrantScopeTests.cs` *(new)* | Every documented grant-scope shape, and the cross-bucket leak. |
| `src/Connapse.Core/Models/SearchScopes.cs` *(modify)* | Carry `GrantMatch` rather than bare strings; carry a `ScopeOutcome` saying why a scope set is empty. |
| `tests/Connapse.Core.Tests/Search/SearchScopesTests.cs` *(modify)* | Update for `Matches` and outcomes. |
| `src/Connapse.Storage/Vectors/PgVectorStore.cs:242-267` *(modify)* | Emit `=` for exact matches, `LIKE ... ESCAPE` for prefixes. |
| `src/Connapse.Search/Keyword/KeywordSearchService.cs:74-92` *(modify)* | Same predicate, same rules. |
| `src/Connapse.Search/Hybrid/HybridSearchService.cs:113-115` *(modify)* | Fail closed when the resolver throws; refuse grants for a null principal. |
| `src/Connapse.Storage/Documents/DocumentCoordinateReport.cs` *(new)* | Count documents with no `resource_uri`, grouped by source. |
| `src/Connapse.Web/Components/Pages/Sources.razor` *(modify)* | Show which sources hold unlocated documents and offer a re-sync. |

---

### Task 1: Grant-scope normalisation

An S3 access grant reports its scope as a string, and AWS documents that string in four different shapes across two pages. The dangerous one is a whole-bucket grant, written `s3://bucket*` with **no separating slash** — trim the asterisk and match it as a prefix, and it also matches `s3://bucket-secrets/...`, a bucket nobody granted. This task turns every shape into an unambiguous match rule.

**Files:**
- Create: `src/Connapse.Core/Models/GrantScope.cs`
- Test: `tests/Connapse.Core.Tests/Search/GrantScopeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Connapse.Core.GrantMatch` — `readonly record struct GrantMatch(string Value, bool IsExact)`; and `Connapse.Core.GrantScope.Parse(string grantScope, bool isObjectScope = false) -> GrantMatch`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Connapse.Core.Tests/Search/GrantScopeTests.cs`:

```csharp
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Turning what AWS reports into something safe to match against a document URI.
/// </summary>
[Trait("Category", "Unit")]
public class GrantScopeTests
{
    [Fact]
    public void Parse_BucketScopeWithoutSlash_GainsOne()
    {
        // The leak this exists to close. AWS writes a whole-bucket grant as "s3://bucket*" with no
        // separating slash, so trimming the asterisk leaves "s3://acme" -- which prefix-matches
        // "s3://acme-secrets/payroll.xlsx" just as happily as "s3://acme/report.pdf".
        var match = GrantScope.Parse("s3://acme*");

        match.Value.Should().Be("s3://acme/");
        match.IsExact.Should().BeFalse();
    }

    [Fact]
    public void Parse_BucketScopeWithSlashStar_IsTheSameThing()
    {
        // The other page documents the identical grant this way. Both must land on one form or
        // the predicate's behaviour depends on which AWS doc the response happened to follow.
        GrantScope.Parse("s3://acme/*").Should().Be(GrantScope.Parse("s3://acme*"));
    }

    [Fact]
    public void Parse_PrefixScope_KeepsThePrefixExactlyAsWritten()
    {
        // "s3://acme/team*" means keys beginning "team" -- including "team-archive/". That is what
        // the administrator wrote, so no slash is added here. Only the bucket-only form is special.
        var match = GrantScope.Parse("s3://acme/team*");

        match.Value.Should().Be("s3://acme/team");
        match.IsExact.Should().BeFalse();
    }

    [Fact]
    public void Parse_PrefixScopeWithNoAsterisk_IsStillAPrefix()
    {
        // AWS's Java example returns the same grant with no trailing asterisk at all.
        var match = GrantScope.Parse("s3://acme/team/");

        match.Value.Should().Be("s3://acme/team/");
        match.IsExact.Should().BeFalse();
    }

    [Fact]
    public void Parse_ObjectScope_MatchesByEqualityNotPrefix()
    {
        // A grant for one object is a grant for one object. As a prefix it would also admit
        // "report.pdf.bak", which is a different object the administrator did not name.
        var match = GrantScope.Parse("s3://acme/reports/q3.pdf", isObjectScope: true);

        match.Value.Should().Be("s3://acme/reports/q3.pdf");
        match.IsExact.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_WithBlank_Throws(string scope)
    {
        FluentActions.Invoking(() => GrantScope.Parse(scope))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_WithNull_Throws()
    {
        FluentActions.Invoking(() => GrantScope.Parse(null!))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_NonS3Scheme_Throws()
    {
        // Rather than silently producing a rule that matches nothing, which would read as a
        // denial and send whoever debugs it looking at permissions instead of at parsing.
        FluentActions.Invoking(() => GrantScope.Parse("azblob://acct/container/"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_SchemeWithNoBucket_Throws()
    {
        FluentActions.Invoking(() => GrantScope.Parse("s3://*"))
            .Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~GrantScopeTests"`
Expected: FAIL — build error, `GrantScope` and `GrantMatch` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Connapse.Core/Models/GrantScope.cs`:

```csharp
namespace Connapse.Core;

/// <summary>One rule from a cloud permission grant, and how to compare a document URI to it.</summary>
/// <param name="Value">The URI, or URI prefix, the grant permits.</param>
/// <param name="IsExact">True when only this exact URI is permitted, false when anything beneath it is.</param>
public readonly record struct GrantMatch(string Value, bool IsExact);

/// <summary>
/// Reads the scope string an S3 access grant reports.
/// </summary>
/// <remarks>
/// Here, beside <see cref="SearchScopes"/>, because the shape produced and the SQL that consumes it
/// have to agree and are written in three files. This repository has been bitten three times by a
/// format decided in one place and parsed in another.
/// <para>
/// AWS documents the same grant in more than one shape. A whole-bucket grant appears as
/// <c>s3://bucket*</c> on one page and <c>s3://bucket/*</c> on another, and a prefix grant appears
/// both with and without a trailing asterisk. All of them have to arrive at one representation, or
/// the filter's behaviour depends on which form the API happened to return.
/// </para>
/// </remarks>
public static class GrantScope
{
    private const string S3Scheme = "s3://";

    /// <param name="grantScope">The <c>GrantScope</c> from an access grant.</param>
    /// <param name="isObjectScope">
    /// True when the grant named a single object — <c>S3PrefixType=Object</c>. Object grants match
    /// by equality, because as a prefix a grant for <c>report.pdf</c> also admits
    /// <c>report.pdf.bak</c>.
    /// </param>
    public static GrantMatch Parse(string grantScope, bool isObjectScope = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantScope);

        string trimmed = grantScope.Trim();
        if (!trimmed.StartsWith(S3Scheme, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Not an S3 grant scope: '{grantScope}'.", nameof(grantScope));
        }

        string value = trimmed.TrimEnd('*');
        string body = value[S3Scheme.Length..];

        if (body.Length == 0)
        {
            throw new ArgumentException(
                $"Grant scope names no bucket: '{grantScope}'.", nameof(grantScope));
        }

        if (isObjectScope)
            return new GrantMatch(value, IsExact: true);

        // A bucket-only scope, and the one shape that cannot be taken literally. Without the
        // trailing slash "s3://acme" prefix-matches "s3://acme-secrets/", so the slash is what
        // confines the grant to the bucket that was actually named.
        if (!body.Contains('/'))
            return new GrantMatch(S3Scheme + body + "/", IsExact: false);

        return new GrantMatch(value, IsExact: false);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~GrantScopeTests"`
Expected: PASS — 9 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/GrantScope.cs tests/Connapse.Core.Tests/Search/GrantScopeTests.cs
git commit -m "feat(search): read an S3 grant scope without widening it"
```

---

### Task 2: Scopes carry match rules, and both stores honour them

`SearchScopes` currently holds bare strings and both predicates treat every one as a prefix. Object-scoped grants therefore admit documents nobody granted. This task replaces `UriPrefixes` with `Matches` and teaches both SQL builders the difference.

**Files:**
- Modify: `src/Connapse.Core/Models/SearchScopes.cs`
- Modify: `src/Connapse.Storage/Vectors/PgVectorStore.cs:242-267`
- Modify: `src/Connapse.Search/Keyword/KeywordSearchService.cs:74-92`
- Test: `tests/Connapse.Core.Tests/Search/SearchScopesTests.cs`
- Test: `tests/Connapse.Integration.Tests/SearchScopeEnforcementTests.cs`

**Interfaces:**
- Consumes: `GrantMatch`, `GrantScope.Parse` from Task 1.
- Produces: `SearchScopes.Of(IReadOnlyList<GrantMatch> matches)`; `SearchScopes.Of(IReadOnlyList<string> uriPrefixes)` (unchanged signature, each string becomes a prefix match); `IReadOnlyList<GrantMatch> SearchScopes.Matches`. `SearchScopes.UriPrefixes` is **removed**.

- [ ] **Step 1: Write the failing unit test**

Add to `tests/Connapse.Core.Tests/Search/SearchScopesTests.cs`, inside the class:

```csharp
    [Fact]
    public void Of_WithMatches_KeepsExactAndPrefixApart()
    {
        // The two kinds cannot be collapsed: an exact match is one object, a prefix is a subtree.
        var scopes = SearchScopes.Of([
            new GrantMatch("s3://acme/team/", IsExact: false),
            new GrantMatch("s3://acme/reports/q3.pdf", IsExact: true),
        ]);

        scopes.Matches.Should().HaveCount(2);
        scopes.Matches.Should().ContainSingle(m => m.IsExact);
    }

    [Fact]
    public void Of_WithStrings_TreatsEachAsAPrefix()
    {
        SearchScopes.Of(["s3://acme/team/"]).Matches
            .Should().BeEquivalentTo([new GrantMatch("s3://acme/team/", IsExact: false)]);
    }
```

Then update the two existing tests that reference `UriPrefixes` (lines 33 and 51) to use `Matches`:

```csharp
    [Fact]
    public void Of_WithPrefixes_RestrictsToThem()
    {
        var scopes = SearchScopes.Of(["s3://acme/team/", "s3://acme/shared/"]);

        scopes.IsUnrestricted.Should().BeFalse();
        scopes.IsEmpty.Should().BeFalse();
        scopes.Matches.Should().HaveCount(2);
    }

    [Fact]
    public void Of_DropsBlankEntriesBetweenRealOnes()
    {
        // A blank prefix would match every URI, so one stray empty string in a resolver's output
        // would silently grant everything to a user who should see one bucket.
        SearchScopes.Of(["s3://acme/team/", "", "s3://acme/shared/"])
            .Matches.Select(m => m.Value)
            .Should().BeEquivalentTo(["s3://acme/team/", "s3://acme/shared/"]);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~SearchScopesTests"`
Expected: FAIL — `Matches` does not exist, and `Of(IReadOnlyList<GrantMatch>)` does not exist.

- [ ] **Step 3: Change `SearchScopes`**

In `src/Connapse.Core/Models/SearchScopes.cs`, replace the constructor, the two factories, the `UriPrefixes` property and `Of`:

```csharp
    private SearchScopes(bool unrestricted, IReadOnlyList<GrantMatch> matches)
    {
        IsUnrestricted = unrestricted;
        Matches = matches;
    }

    public static readonly SearchScopes Unrestricted = new(true, []);

    /// <summary>Nothing is reachable. A resolved user with no grants.</summary>
    public static readonly SearchScopes None = new(false, []);

    /// <summary>Only documents matching one of these rules.</summary>
    public static SearchScopes Of(IReadOnlyList<GrantMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var usable = matches.Where(m => !string.IsNullOrWhiteSpace(m.Value)).ToList();
        return usable.Count == 0 ? None : new SearchScopes(false, usable);
    }

    /// <summary>Only documents whose resource URI starts with one of these.</summary>
    public static SearchScopes Of(IReadOnlyList<string> uriPrefixes)
    {
        ArgumentNullException.ThrowIfNull(uriPrefixes);

        return Of(uriPrefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new GrantMatch(p, IsExact: false))
            .ToList());
    }

    public bool IsUnrestricted { get; }

    /// <summary>The rules a document's resource URI must satisfy one of.</summary>
    public IReadOnlyList<GrantMatch> Matches { get; }

    /// <summary>True when this permits nothing, so a query need not run at all.</summary>
    public bool IsEmpty => !IsUnrestricted && Matches.Count == 0;
```

- [ ] **Step 4: Update the vector predicate**

In `src/Connapse.Storage/Vectors/PgVectorStore.cs`, replace the loop inside the `else` branch (currently lines 254-265):

```csharp
                // A document with no resource URI is denied, never allowed. It is one nothing can
                // locate, so no permission rule can be checked against it — an upload, or a row
                // not synced since the column existed.
                var ors = new List<string>();
                for (int i = 0; i < scopes.Matches.Count; i++)
                {
                    GrantMatch match = scopes.Matches[i];

                    // An object-scoped grant is one object. As a prefix it would also admit
                    // "report.pdf.bak", which is a different object nobody named.
                    ors.Add(match.IsExact
                        ? $"d.resource_uri = @scope{i}"
                        : $"d.resource_uri LIKE @scope{i} ESCAPE '{SearchScopes.LikeEscape}'");

                    parameters.Add(new NpgsqlParameter($"@scope{i}", NpgsqlDbType.Text)
                    {
                        Value = match.IsExact ? match.Value : SearchScopes.ToLikePattern(match.Value)
                    });
                }
```

- [ ] **Step 5: Update the keyword predicate**

In `src/Connapse.Search/Keyword/KeywordSearchService.cs`, replace the loop inside the `else` branch (currently lines 82-88):

```csharp
                var ors = new List<string>();
                foreach (GrantMatch match in scopes.Matches)
                {
                    int scopeIdx = parameters.Count;

                    // The same rule as the vector side, and it has to be: a hit reachable through
                    // one mode and not the other is a leak through whichever the caller chooses.
                    ors.Add(match.IsExact
                        ? $"d.resource_uri = {{{scopeIdx}}}"
                        : $"d.resource_uri LIKE {{{scopeIdx}}} ESCAPE '{SearchScopes.LikeEscape}'");

                    parameters.Add(match.IsExact
                        ? match.Value
                        : SearchScopes.ToLikePattern(match.Value));
                }
```

- [ ] **Step 6: Run unit tests and the build**

Run: `dotnet build && dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~SearchScopesTests"`
Expected: build succeeds, tests PASS.

- [ ] **Step 7: Write the failing integration test**

Add to `tests/Connapse.Integration.Tests/SearchScopeEnforcementTests.cs`, inside the class. Follow the arrangement already used by `Search_WithAnUnderscoreInTheGrant_DoesNotMatchAnyOtherCharacter` in the same file — same container/document/chunk setup, same `Build` and `For` helpers:

```csharp
    [Fact]
    public async Task Search_WithABucketScopedGrant_DoesNotReachASimilarlyNamedBucket()
    {
        // Against real SQL, because this is the shape AWS actually returns for a whole-bucket
        // grant and the leak is invisible in a unit test of the pattern string.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);

        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new[]
                 {
                     ("granted.md", "s3://acme/report.md"),
                     ("sneaky.md", "s3://acme-secrets/payroll.md"),
                 })
        {
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                ContainerId = container.Id,
                FileName = name,
                Path = "/" + name,
                ResourceUri = uri,
                ContentHash = Guid.NewGuid().ToString("N"),
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            };
            db.Documents.Add(document);
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                OwnerId = container.Id,
                Content = $"the {Term} appears here",
                ChunkIndex = 0,
                Metadata = [],
            });
        }

        await db.SaveChangesAsync();

        var hits = await Build(db).SearchAsync(
            Term, For(container.Id), SearchScopes.Of([GrantScope.Parse("s3://acme*")]));

        hits.Should().ContainSingle("a grant for one bucket does not reach another whose name starts the same");
        hits[0].Metadata.GetValueOrDefault("fileName").Should().Be("granted.md");
    }

    [Fact]
    public async Task Search_WithAnObjectScopedGrant_DoesNotReachASuffixedSibling()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);

        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new[]
                 {
                     ("granted.md", "s3://acme/reports/q3.pdf"),
                     ("sneaky.md", "s3://acme/reports/q3.pdf.bak"),
                 })
        {
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                ContainerId = container.Id,
                FileName = name,
                Path = "/" + name,
                ResourceUri = uri,
                ContentHash = Guid.NewGuid().ToString("N"),
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            };
            db.Documents.Add(document);
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                OwnerId = container.Id,
                Content = $"the {Term} appears here",
                ChunkIndex = 0,
                Metadata = [],
            });
        }

        await db.SaveChangesAsync();

        var hits = await Build(db).SearchAsync(
            Term, For(container.Id),
            SearchScopes.Of([GrantScope.Parse("s3://acme/reports/q3.pdf", isObjectScope: true)]));

        hits.Should().ContainSingle("an object grant names one object");
        hits[0].Metadata.GetValueOrDefault("fileName").Should().Be("granted.md");
    }
```

- [ ] **Step 8: Run the integration tests**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~SearchScopeEnforcementTests"`
Expected: PASS. Requires Docker for Testcontainers.

- [ ] **Step 9: Commit**

```bash
git add src/Connapse.Core/Models/SearchScopes.cs src/Connapse.Storage/Vectors/PgVectorStore.cs src/Connapse.Search/Keyword/KeywordSearchService.cs tests/Connapse.Core.Tests/Search/SearchScopesTests.cs tests/Connapse.Integration.Tests/SearchScopeEnforcementTests.cs
git commit -m "fix(search): match an object grant by equality, not prefix"
```

---

### Task 3: Say why a search reached nothing

Three very different situations currently produce one indistinguishable empty result: the user genuinely has no grants, the caller could not be resolved to a person, and the resolver failed. A user told "no access" when the truth is "this deployment has no S3 Access Grants instance" will file a support ticket about permissions and look in the wrong place.

**Files:**
- Modify: `src/Connapse.Core/Models/SearchScopes.cs`
- Test: `tests/Connapse.Core.Tests/Search/SearchScopesTests.cs`

**Interfaces:**
- Consumes: `SearchScopes` from Task 2.
- Produces: `Connapse.Core.ScopeOutcome` enum with members `Unrestricted`, `Granted`, `NoGrants`, `NoPrincipal`, `ResolverFailed`; `SearchScopes.Outcome` property; `SearchScopes.NoPrincipal` and `SearchScopes.Failed` static instances. `SearchScopes.None` remains and now carries `ScopeOutcome.NoGrants`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Connapse.Core.Tests/Search/SearchScopesTests.cs`:

```csharp
    [Fact]
    public void Outcome_DistinguishesTheThreeWaysOfReachingNothing()
    {
        // All three deny, and they must not be one state. "You have no grants" is a configuration
        // message, "we could not tell who you are" is a token problem, and "the resolver failed" is
        // an outage. Collapsing them sends whoever debugs it to the wrong place every time.
        SearchScopes.None.Outcome.Should().Be(ScopeOutcome.NoGrants);
        SearchScopes.NoPrincipal.Outcome.Should().Be(ScopeOutcome.NoPrincipal);
        SearchScopes.Failed.Outcome.Should().Be(ScopeOutcome.ResolverFailed);

        SearchScopes.None.IsEmpty.Should().BeTrue();
        SearchScopes.NoPrincipal.IsEmpty.Should().BeTrue();
        SearchScopes.Failed.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Outcome_SeparatesNotFilteringFromReachingEverything()
    {
        SearchScopes.Unrestricted.Outcome.Should().Be(ScopeOutcome.Unrestricted);
        SearchScopes.Of(["s3://acme/team/"]).Outcome.Should().Be(ScopeOutcome.Granted);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~SearchScopesTests"`
Expected: FAIL — `ScopeOutcome`, `Outcome`, `NoPrincipal` and `Failed` do not exist.

- [ ] **Step 3: Implement**

In `src/Connapse.Core/Models/SearchScopes.cs`, add above the `SearchScopes` record:

```csharp
/// <summary>Why a search may reach what it may.</summary>
/// <remarks>
/// Three of these mean "nothing", and they are kept apart because they send whoever investigates to
/// three different places. <see cref="NoGrants"/> is almost always configuration — a deployment
/// with no S3 Access Grants instance returns an empty list for every user, which is
/// indistinguishable from denial unless something says so.
/// </remarks>
public enum ScopeOutcome
{
    /// <summary>This deployment does not filter.</summary>
    Unrestricted,

    /// <summary>The user has grants, and they are in <c>Matches</c>.</summary>
    Granted,

    /// <summary>The user was resolved and has no grants.</summary>
    NoGrants,

    /// <summary>The caller could not be resolved to a person.</summary>
    NoPrincipal,

    /// <summary>Permissions could not be determined. Denies, deliberately.</summary>
    ResolverFailed,
}
```

Then change the constructor and factories to carry an outcome:

```csharp
    private SearchScopes(bool unrestricted, IReadOnlyList<GrantMatch> matches, ScopeOutcome outcome)
    {
        IsUnrestricted = unrestricted;
        Matches = matches;
        Outcome = outcome;
    }

    public static readonly SearchScopes Unrestricted =
        new(true, [], ScopeOutcome.Unrestricted);

    /// <summary>Nothing is reachable. A resolved user with no grants.</summary>
    public static readonly SearchScopes None =
        new(false, [], ScopeOutcome.NoGrants);

    /// <summary>Nothing is reachable, because nobody could be named.</summary>
    public static readonly SearchScopes NoPrincipal =
        new(false, [], ScopeOutcome.NoPrincipal);

    /// <summary>Nothing is reachable, because permissions could not be determined.</summary>
    /// <remarks>
    /// Failing closed. XACML 3.0 §7.2.2: a deny-biased enforcement point denies without an explicit
    /// permit, and an indeterminate answer is not a permit. The caller is told this is an error
    /// rather than an empty result, because they are not the same thing.
    /// </remarks>
    public static readonly SearchScopes Failed =
        new(false, [], ScopeOutcome.ResolverFailed);

    /// <summary>Why this scope set is what it is.</summary>
    public ScopeOutcome Outcome { get; }
```

And update `Of(IReadOnlyList<GrantMatch>)` to pass an outcome:

```csharp
    public static SearchScopes Of(IReadOnlyList<GrantMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var usable = matches.Where(m => !string.IsNullOrWhiteSpace(m.Value)).ToList();
        return usable.Count == 0
            ? None
            : new SearchScopes(false, usable, ScopeOutcome.Granted);
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet build && dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~SearchScopesTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/SearchScopes.cs tests/Connapse.Core.Tests/Search/SearchScopesTests.cs
git commit -m "feat(search): distinguish the three ways a search reaches nothing"
```

---

### Task 4: Fail closed, and refuse to answer for nobody

The single call site that resolves scopes currently trusts the resolver completely — an exception propagates as a failed search, and a resolver that returns grants for a caller with no user id would be believed. Both are fixed here, in one place, so no future resolver has to remember.

**Files:**
- Modify: `src/Connapse.Search/Hybrid/HybridSearchService.cs:113-115`
- Test: `tests/Connapse.Core.Tests/Search/ScopeResolutionGuardTests.cs` *(new)*

**Interfaces:**
- Consumes: `SearchScopes.Failed`, `SearchScopes.NoPrincipal` from Task 3.
- Produces: `Connapse.Core.ScopeResolution.Guard(SearchScopes resolved, Guid? userId) -> SearchScopes` — a pure function holding the rule, so it can be tested without standing up the search pipeline.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Core.Tests/Search/ScopeResolutionGuardTests.cs`:

```csharp
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// The rule applied to whatever a resolver hands back, before anything acts on it.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeResolutionGuardTests
{
    [Fact]
    public void Guard_WhenNoUserAndResolverReturnedGrants_Refuses()
    {
        // A resolver that produces grants for a caller with no user has answered a question nobody
        // asked. Refused here rather than trusting every implementation to remember, because the
        // surfaces that legitimately have no user -- MCP, personal access tokens -- are exactly the
        // ones where believing it would be a hole rather than a bug.
        var resolved = SearchScopes.Of(["s3://acme/team/"]);

        ScopeResolution.Guard(resolved, userId: null)
            .Should().BeSameAs(SearchScopes.NoPrincipal);
    }

    [Fact]
    public void Guard_WhenNoUserAndDeploymentDoesNotFilter_LeavesItAlone()
    {
        // Not filtering is not a permission decision, so a caller with no user is no worse off
        // than any other. Forcing a denial here would break every existing installation.
        ScopeResolution.Guard(SearchScopes.Unrestricted, userId: null)
            .Should().BeSameAs(SearchScopes.Unrestricted);
    }

    [Fact]
    public void Guard_WhenUserIsPresent_PassesTheAnswerThrough()
    {
        var resolved = SearchScopes.Of(["s3://acme/team/"]);

        ScopeResolution.Guard(resolved, userId: Guid.NewGuid())
            .Should().BeSameAs(resolved);
    }

    [Fact]
    public void Guard_WhenNoUserAndResolverDenied_KeepsTheReason()
    {
        // Already a denial, and its reason is more specific than NoPrincipal would be.
        ScopeResolution.Guard(SearchScopes.None, userId: null)
            .Should().BeSameAs(SearchScopes.None);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~ScopeResolutionGuardTests"`
Expected: FAIL — `ScopeResolution` does not exist.

- [ ] **Step 3: Implement the guard**

Add to `src/Connapse.Core/Models/SearchScopes.cs`, at the end of the file:

```csharp
/// <summary>
/// The rule applied to a resolver's answer before anything acts on it.
/// </summary>
/// <remarks>
/// A pure function rather than a line inside the search pipeline, so the rule can be tested without
/// a database, an HTTP context, or a resolver.
/// </remarks>
public static class ScopeResolution
{
    /// <summary>
    /// Refuses grants handed back for a caller who is not a person.
    /// </summary>
    /// <remarks>
    /// Unrestricted passes through untouched: a deployment that does not filter has made no
    /// permission decision, and denying here would break every installation that has not opted in.
    /// An existing denial passes through too, because its reason is more specific than this one.
    /// </remarks>
    public static SearchScopes Guard(SearchScopes resolved, Guid? userId)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (userId is not null)
            return resolved;

        return resolved is { IsUnrestricted: false, IsEmpty: false }
            ? SearchScopes.NoPrincipal
            : resolved;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~ScopeResolutionGuardTests"`
Expected: PASS — 4 tests.

- [ ] **Step 5: Wire it into the search pipeline**

In `src/Connapse.Search/Hybrid/HybridSearchService.cs`, replace lines 113-115:

```csharp
        // One resolution per search, taken here because this is where the query fans out. Doing it
        // inside each leaf would mean two answers for one query, and hybrid would be the mode in
        // which they could disagree — half a result set from one set of permissions and half from
        // another, with no way to tell from the outside.
        SearchScopes scopes;
        try
        {
            scopes = await scope.ServiceProvider
                .GetRequiredService<ISearchScopeResolver>()
                .ResolveAsync(options.UserId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed. A resolver that cannot answer has not said "everything" — it has said
            // nothing, and a search that proceeds unfiltered on an outage is the failure this whole
            // feature exists to prevent.
            _logger.LogError(ex, "Could not resolve search scopes; denying this search");
            scopes = SearchScopes.Failed;
        }

        scopes = ScopeResolution.Guard(scopes, options.UserId);
```

The class already holds `private readonly ILogger<HybridSearchService> _logger;` at line 38. Use it; do not add a second logger.

- [ ] **Step 6: Build and run the search test suites**

Run: `dotnet build && dotnet test --filter "Category=Unit"`
Expected: build succeeds, all unit tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Connapse.Core/Models/SearchScopes.cs src/Connapse.Search/Hybrid/HybridSearchService.cs tests/Connapse.Core.Tests/Search/ScopeResolutionGuardTests.cs
git commit -m "fix(search): fail closed when scopes cannot be resolved"
```

---

### Task 5: Find documents with no recorded coordinate

Both predicates gate on `resource_uri IS NOT NULL`. Every document indexed before migration `20260827062208_AddDocumentResourceUri` has a null value, so switching filtering on would make the entire pre-existing corpus vanish — indistinguishably from a denial. Phase 2 declined a SQL backfill on purpose, because deriving the URI is silently wrong for a re-pointed source. The remedy is a re-sync, and the first thing needed is knowing which sources are affected.

**Files:**
- Create: `src/Connapse.Storage/Documents/DocumentCoordinateReport.cs`
- Test: `tests/Connapse.Integration.Tests/DocumentCoordinateReportTests.cs`

**Interfaces:**
- Consumes: `KnowledgeDbContext`, `IDbContextFactory<KnowledgeDbContext>`.
- Produces: `Connapse.Storage.Documents.UnlocatedSource` — `sealed record UnlocatedSource(Guid SourceId, string SourceName, int DocumentCount)`; and `DocumentCoordinateReport.UnlocatedBySourceAsync(CancellationToken ct) -> Task<IReadOnlyList<UnlocatedSource>>`.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/DocumentCoordinateReportTests.cs`:

```csharp
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Documents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Which sources still hold documents that no permission rule can be checked against.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SharedWebAppCollection.Name)]
public class DocumentCoordinateReportTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task UnlocatedBySourceAsync_CountsOnlyDocumentsWithNoResourceUri()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

        Guid sourceId = Guid.NewGuid();
        string sourceName = $"src-{Guid.NewGuid():N}";

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Sources.Add(new SourceEntity { Id = sourceId, Name = sourceName });

            // Two without a coordinate, one with. Only the two are the operator's problem.
            foreach (string? uri in new[] { null, null, "s3://acme/located.md" })
            {
                db.Documents.Add(new DocumentEntity
                {
                    Id = Guid.NewGuid(),
                    SourceId = sourceId,
                    FileName = "d.md",
                    Path = "/d.md",
                    ResourceUri = uri,
                    ContentHash = Guid.NewGuid().ToString("N"),
                    Status = "Ready",
                    CreatedAt = DateTime.UtcNow,
                    Metadata = [],
                });
            }

            await db.SaveChangesAsync();
        }

        var report = new DocumentCoordinateReport(factory);
        var rows = await report.UnlocatedBySourceAsync(CancellationToken.None);

        var row = rows.Should().ContainSingle(r => r.SourceId == sourceId).Subject;
        row.DocumentCount.Should().Be(2);
        row.SourceName.Should().Be(sourceName);
    }

    [Fact]
    public async Task UnlocatedBySourceAsync_OmitsSourcesWhereEveryDocumentIsLocated()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

        Guid sourceId = Guid.NewGuid();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Sources.Add(new SourceEntity { Id = sourceId, Name = $"src-{Guid.NewGuid():N}" });
            db.Documents.Add(new DocumentEntity
            {
                Id = Guid.NewGuid(),
                SourceId = sourceId,
                FileName = "d.md",
                Path = "/d.md",
                ResourceUri = "s3://acme/located.md",
                ContentHash = Guid.NewGuid().ToString("N"),
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            });
            await db.SaveChangesAsync();
        }

        var report = new DocumentCoordinateReport(factory);
        var rows = await report.UnlocatedBySourceAsync(CancellationToken.None);

        rows.Should().NotContain(r => r.SourceId == sourceId);
    }
}
```

If `SourceEntity` requires more non-nullable properties than `Id` and `Name`, populate them the way `tests/Connapse.Integration.Tests/SearchScopeEnforcementTests.cs` and its neighbours already do; do not invent new defaults.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~DocumentCoordinateReportTests"`
Expected: FAIL — `DocumentCoordinateReport` does not exist.

- [ ] **Step 3: Implement**

Create `src/Connapse.Storage/Documents/DocumentCoordinateReport.cs`:

```csharp
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Documents;

/// <summary>A source holding documents whose external address was never recorded.</summary>
public sealed record UnlocatedSource(Guid SourceId, string SourceName, int DocumentCount);

/// <summary>
/// Reports documents that no permission rule can be checked against.
/// </summary>
/// <remarks>
/// Both search predicates require <c>resource_uri IS NOT NULL</c>, so a document indexed before that
/// column existed becomes unreachable the moment per-user filtering is switched on — and
/// indistinguishably from a denial. This exists so an operator is told which sources to re-sync
/// beforehand, rather than discovering it as a support ticket afterwards.
/// <para>
/// A SQL backfill is deliberately not offered. Deriving the URI from a source's scope and a
/// document's stored path is silently wrong for a source that has been re-pointed since ingestion,
/// and a document attributed to the wrong key is worse than one attributed to none.
/// </para>
/// </remarks>
public sealed class DocumentCoordinateReport(IDbContextFactory<KnowledgeDbContext> factory)
{
    /// <summary>Sources with at least one document that has no recorded coordinate.</summary>
    public async Task<IReadOnlyList<UnlocatedSource>> UnlocatedBySourceAsync(
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Uploads have no external address and never will, so only source-backed documents count.
        return await db.Documents
            .Where(d => d.SourceId != null && d.ResourceUri == null)
            .GroupBy(d => d.SourceId!.Value)
            .Join(db.Sources,
                g => g.Key,
                s => s.Id,
                (g, s) => new UnlocatedSource(s.Id, s.Name, g.Count()))
            .OrderByDescending(r => r.DocumentCount)
            .ToListAsync(ct);
    }
}
```

- [ ] **Step 4: Register it**

In `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`, inside `AddConnapseStorage` (line 38), add alongside the existing `AddScoped` registrations around line 68:

```csharp
        services.AddScoped<DocumentCoordinateReport>();
```

Add `using Connapse.Storage.Documents;` to that file if it is not already present.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet build && dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~DocumentCoordinateReportTests"`
Expected: PASS — 2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Storage/Documents/DocumentCoordinateReport.cs src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs tests/Connapse.Integration.Tests/DocumentCoordinateReportTests.cs
git commit -m "feat(storage): report sources holding documents with no coordinate"
```

---

### Task 6: Tell the operator, on the page where they can act

The report is useless unless it appears where someone can do something about it. The Sources page already lists sources and already offers a sync; this adds a warning to the sources that need one, with the count and the reason.

**Files:**
- Modify: `src/Connapse.Web/Components/Pages/Sources.razor`
- Test: `tests/Connapse.Web.Tests/Components/UnlocatedSourceWarningTests.cs` *(new)*

**Interfaces:**
- Consumes: `DocumentCoordinateReport.UnlocatedBySourceAsync` and `UnlocatedSource` from Task 5.
- Produces: no new public API. The page renders a warning per affected source.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Web.Tests/Components/UnlocatedSourceWarningTests.cs`. This pins the wording, not the markup, because the wording is the part that has to stay honest. It uses the `PageTestPaths.RepositoryRoot()` helper the neighbouring `CloudIdentityClaimsTests` already uses — do not hand-roll a relative path:

```csharp
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// What the operator is told about documents with no recorded coordinate.
/// </summary>
/// <remarks>
/// The wording matters more than the markup. "Re-sync to record where these came from" tells an
/// operator what to do; "some documents are missing metadata" does not, and the consequence of not
/// acting -- an entire corpus vanishing when filtering is enabled -- is invisible from the second.
/// </remarks>
[Trait("Category", "Unit")]
public class UnlocatedSourceWarningTests
{
    private static readonly string Markup =
        File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "Sources.razor"));

    [Fact]
    public void SourcesPage_NamesTheActionThatFixesIt()
    {
        Markup.Should().Contain("Re-sync",
            "an operator needs to be told what to do, not only that something is wrong");
    }

    [Fact]
    public void SourcesPage_ExplainsWhyItMatters()
    {
        Markup.Should().Contain("per-user permissions",
            "the consequence is invisible unless the warning says what breaks");
    }

    [Fact]
    public void SourcesPage_ReadsTheReport()
    {
        Markup.Should().Contain("UnlocatedBySourceAsync",
            "the warning must come from the report rather than a guess");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Web.Tests --filter "FullyQualifiedName~UnlocatedSourceWarningTests"`
Expected: FAIL — the markup contains none of those strings.

- [ ] **Step 3: Inject the report into the page**

At the top of `src/Connapse.Web/Components/Pages/Sources.razor`, alongside the existing `@inject` directives:

```razor
@inject Connapse.Storage.Documents.DocumentCoordinateReport CoordinateReport
```

- [ ] **Step 4: Load the report where the page loads its sources**

In the `@code` block, add a field and populate it in the same lifecycle method that already loads sources (`OnInitializedAsync`, or the private load method it calls):

```csharp
    private IReadOnlyDictionary<Guid, int> _unlocated =
        new Dictionary<Guid, int>();

    private async Task LoadUnlocatedAsync()
    {
        var rows = await CoordinateReport.UnlocatedBySourceAsync();
        _unlocated = rows.ToDictionary(r => r.SourceId, r => r.DocumentCount);
    }
```

Call `await LoadUnlocatedAsync();` immediately after the existing source load, and again after any successful sync so the warning clears without a page refresh.

- [ ] **Step 5: Render the warning**

Inside the markup that renders each source row, where `source.Id` is in scope, add:

```razor
@if (_unlocated.TryGetValue(source.Id, out int unlocatedCount))
{
    <div class="alert alert-warning py-2 px-3 mt-2 mb-0 small" role="status">
        <strong>@unlocatedCount</strong> document@(unlocatedCount == 1 ? "" : "s")
        here have no recorded location.
        Re-sync this source to record where they came from — until then they cannot be
        reached once per-user permissions are switched on.
    </div>
}
```

The loop at `Sources.razor:113` is `@foreach (var source in sources)`, so `source.Id` is in scope there.

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet build && dotnet test tests/Connapse.Web.Tests --filter "FullyQualifiedName~UnlocatedSourceWarningTests"`
Expected: PASS — 3 tests.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS. `main` was last verified green at 386 passed, 0 failed; this plan adds roughly 20 tests. Integration tests need Docker.

- [ ] **Step 8: Commit**

```bash
git add src/Connapse.Web/Components/Pages/Sources.razor tests/Connapse.Web.Tests/Components/UnlocatedSourceWarningTests.cs
git commit -m "feat(web): warn when a source holds documents with no coordinate"
```

---

## Verification

After Task 6, all of the following must hold:

- `dotnet test` passes.
- A grant for `s3://acme*` does not reach `s3://acme-secrets/`, proven against real SQL.
- A grant for one object does not reach a suffixed sibling, proven against real SQL.
- `SearchScopes.None`, `NoPrincipal` and `Failed` all deny and are all distinguishable by `Outcome`.
- A resolver that throws produces `ScopeOutcome.ResolverFailed`, not an unfiltered search.
- A resolver that returns grants for a null user id is refused.
- The Sources page names every source holding documents with no `resource_uri`.

## Out of scope for 5a

Sub-phases 5b (Cognito sign-in and token store), 5c (provider setup and detection, opening with the exchange spike) and 5d (the resolver) each get their own plan. Nothing in 5a talks to AWS, and the default `UnrestrictedScopeResolver` stays registered, so behaviour is unchanged for every existing deployment until a real resolver is registered in 5d.
