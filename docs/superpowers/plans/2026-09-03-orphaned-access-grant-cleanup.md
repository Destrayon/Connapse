# Orphaned S3 Access Grant cleanup — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect S3 Access Grants that Connapse created but no connection still needs (or that belong to a superseded grant group) and delete them, safely and automatically.

**Architecture:** Provenance-tag every grant at creation so cleanup only ever deletes grants Connapse can prove are its own. A Hangfire recurring sweep (plus an admin button) builds the union of allowed-locations across all S3 connections, finds tagged grants whose scope that union no longer covers, confirms provenance via tags, and deletes them — with a hard fail-closed abort on any incomplete input and a circuit breaker against implausibly large deletions.

**Tech Stack:** .NET 10, C#, Blazor Server, AWS SDK for .NET (`AWSSDK.S3Control`), Hangfire (PostgreSQL), xUnit + FluentAssertions + NSubstitute.

**Spec:** [docs/superpowers/specs/2026-09-03-orphaned-access-grant-cleanup-design.md](../specs/2026-09-03-orphaned-access-grant-cleanup-design.md)

## Global Constraints

- .NET 10; file-scoped namespaces; nullable enabled. Records for DTOs; primary constructors for DI. No `var` for primitives; no `dynamic`. Async all the way.
- **Only ever delete a grant confirmed `connapse:managed=true` by tag**, that is a DIRECTORY_GROUP grant, and whose scope no connection covers. Never delete IAM or directory-user grants, and never an untagged / admin-authored grant.
- **Fail-closed:** any failure enumerating connections, or any unparseable connection config, aborts the whole reconcile tick — deletion requires a *complete* union. Mirror `AwsSearchScopeResolver`.
- **Circuit breaker:** a tick whose candidate set exceeds the configured plausibility threshold aborts and alerts instead of deleting.
- `DeleteAccessGrant` is one-per-call and **not idempotent** — a missing id throws `NotFoundException`; catch and fold into a NotFound bucket.
- One `AmazonS3ControlClient` **per region**; `AccountId` on every request.
- Log every deletion at audit level; sanitize user-controlled values with `LogSanitizer.Sanitize`.
- Test naming `MethodName_Scenario_ExpectedResult`; unit tests `[Trait("Category", "Unit")]`. Commit types `feat:`/`test:`/`refactor:`; end messages with the Co-Authored-By trailer.

---

# PR 1 — Tag grants on create (the enabler)

Ships first: small, safe, and the prerequisite for any safe deletion. Grants created after this are GC-able; grants before it stay untagged and are never auto-deleted (by design).

### Task 1.1: Stamp the provenance tag on every created grant

**Files:**
- Modify: `src/Connapse.Storage/CloudScope/S3AccessGrantsWriter.cs` (the `CreateAccessGrantAsync` call in `CreateAsync`)
- Create: `src/Connapse.Core/Utilities/GrantTags.cs` (the tag constants, so create and cleanup share one source)
- Test: `tests/Connapse.Core.Tests/Utilities/GrantTagsTests.cs`

**Interfaces:**
- Produces: `GrantTags.ManagedKey` = `"connapse:managed"`, `GrantTags.ManagedValue` = `"true"`.

- [ ] **Step 1: Write the failing test for the tag constants**

```csharp
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class GrantTagsTests
{
    [Fact]
    public void ManagedTag_IsTheConnapseProvenanceMarker()
    {
        // The one marker cleanup keys on. Kept out of the writer so create and the reconciler
        // cannot drift on the string that decides whether a grant is deletable.
        GrantTags.ManagedKey.Should().Be("connapse:managed");
        GrantTags.ManagedValue.Should().Be("true");
        GrantTags.ManagedKey.Should().NotStartWith("aws:"); // AWS rejects aws:-prefixed tag keys
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~GrantTagsTests"`
Expected: FAIL — `GrantTags` does not exist.

- [ ] **Step 3: Create the constants**

```csharp
namespace Connapse.Core.Utilities;

/// <summary>
/// The tags Connapse stamps on the S3 Access Grants it creates, so a cleanup pass can prove a
/// grant is its own before deleting it.
/// </summary>
/// <remarks>
/// One source shared by the writer (which applies them) and the reconciler (which requires them
/// before deleting). AWS forbids tag keys beginning <c>aws:</c>. Tags are write-only through the
/// grant read path — <c>ListAccessGrants</c> does not return them — so the reconciler reads them
/// back per-candidate via <c>ListTagsForResource</c>.
/// </remarks>
public static class GrantTags
{
    /// <summary>Present with <see cref="ManagedValue"/> exactly on grants Connapse created.</summary>
    public const string ManagedKey = "connapse:managed";

    /// <summary>The value <see cref="ManagedKey"/> carries.</summary>
    public const string ManagedValue = "true";
}
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~GrantTagsTests"`
Expected: PASS.

- [ ] **Step 5: Apply the tag in the writer**

In `S3AccessGrantsWriter.CreateAsync`, add `Tags` to the `CreateAccessGrantRequest` (the SDK type is `Amazon.S3Control.Model.Tag`):

```csharp
await client.CreateAccessGrantAsync(new CreateAccessGrantRequest
{
    AccountId = account,
    AccessGrantsLocationId = locationId,
    AccessGrantsLocationConfiguration =
        new AccessGrantsLocationConfiguration { S3SubPrefix = subPrefix },
    Permission = Permission.READ,
    Grantee = new Grantee
    {
        GranteeType = grantee.IsGroup ? GranteeType.DIRECTORY_GROUP : GranteeType.DIRECTORY_USER,
        GranteeIdentifier = grantee.Id,
    },
    // Provenance: cleanup deletes only grants carrying this tag.
    Tags = [new Tag { Key = GrantTags.ManagedKey, Value = GrantTags.ManagedValue }],
}, ct);
```

Add `using Connapse.Core.Utilities;` if not present. (The `connapse:instance` tag from spec §6 is added in PR 3, where instance-scoped deletion consumes it and the fingerprint source is resolved.)

- [ ] **Step 6: Build to verify the SDK `Tag` shape compiles**

Run: `dotnet build src/Connapse.Storage`
Expected: SUCCEEDS. (If `Tag`/`Tags` resolve differently in the installed `AWSSDK.S3Control`, correct against IntelliSense — the `Tags` request field and `Tag{Key,Value}` type exist in the S3 Control model.)

- [ ] **Step 7: Commit**

```bash
git add src/Connapse.Core/Utilities/GrantTags.cs tests/Connapse.Core.Tests/Utilities/GrantTagsTests.cs src/Connapse.Storage/CloudScope/S3AccessGrantsWriter.cs
git commit -m "feat: tag Connapse-created S3 access grants for later cleanup"
```

---

# PR 2 — Reader detail, delete path, and policy

The plumbing a reconciler needs: read grant ids + grantees back, delete by id, and let the role do it. No automatic behaviour yet — exercised via unit tests and (in PR 3) the admin button.

### Task 2.1: Surface full grant detail from the reader

**Files:**
- Modify: `src/Connapse.Core/Interfaces/IAccessGrantsReader.cs` (add `AccessGrantDetail` record + `ListAllAsync`)
- Modify: `src/Connapse.Storage/CloudScope/S3AccessGrantsReader.cs` (implement `ListAllAsync`)
- Test: covered indirectly (AWS-client class, no unit test at the client boundary — matching the existing reader); the projection shape is asserted in Task 3.x's candidate-selection unit tests via the pure type.

**Interfaces:**
- Produces:
  - `record AccessGrantDetail(string AccessGrantId, string AccessGrantArn, AccessGrantee Grantee, string GrantScope, string? Permission, string AccessGrantsLocationId)`
  - `IAccessGrantsReader.ListAllAsync(string region, CancellationToken ct = default) → Task<IReadOnlyList<AccessGrantDetail>>`

- [ ] **Step 1: Add the record and interface method**

In `IAccessGrantsReader.cs` (namespace `Connapse.Core`):

```csharp
/// <summary>One S3 access grant, with everything cleanup needs to identify and delete it.</summary>
/// <remarks>
/// Distinct from <see cref="AccessGrantRecord"/>, which the search/coverage paths use and which
/// deliberately drops the id and grantee. Deletion needs the <c>AccessGrantId</c>, the ARN (to read
/// tags back), and the grantee (to tell the configured group from a superseded one).
/// </remarks>
public record AccessGrantDetail(
    string AccessGrantId, string AccessGrantArn, AccessGrantee Grantee,
    string GrantScope, string? Permission, string AccessGrantsLocationId);
```

Add to the `IAccessGrantsReader` interface:

```csharp
/// <summary>
/// Every grant in the instance in <paramref name="region"/>, with id, ARN and grantee — for
/// reconciliation. Grantee-blind: the reconciler needs all groups, including one that is no longer
/// configured.
/// </summary>
Task<IReadOnlyList<AccessGrantDetail>> ListAllAsync(string region, CancellationToken ct = default);
```

- [ ] **Step 2: Implement it in `S3AccessGrantsReader`**

Mirror `ListAllScopesAsync`'s paging, keeping the id/ARN/grantee this time:

```csharp
/// <inheritdoc />
public async Task<IReadOnlyList<AccessGrantDetail>> ListAllAsync(
    string region, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(region);

    if (!options.CurrentValue.IsConfigured)
        return [];

    var endpoint = RegionEndpoint.GetBySystemName(region);
    string account = await ResolveAccountIdAsync(endpoint, ct);

    using var client = new AmazonS3ControlClient(
        credentials, new AmazonS3ControlConfig { RegionEndpoint = endpoint });

    List<AccessGrantDetail> grants = [];
    string? nextToken = null;

    do
    {
        var response = await client.ListAccessGrantsAsync(
            new ListAccessGrantsRequest { AccountId = account, NextToken = nextToken }, ct);

        foreach (var g in response.AccessGrantsList ?? [])
        {
            if (string.IsNullOrWhiteSpace(g.AccessGrantId) || string.IsNullOrWhiteSpace(g.GrantScope))
                continue;

            var grantee = g.Grantee is { } who
                ? new AccessGrantee(
                    IsGroup: who.GranteeType == GranteeType.DIRECTORY_GROUP,
                    Id: who.GranteeIdentifier ?? string.Empty)
                : new AccessGrantee(IsGroup: false, Id: string.Empty);

            grants.Add(new AccessGrantDetail(
                g.AccessGrantId, g.AccessGrantArn ?? string.Empty, grantee,
                g.GrantScope, g.Permission?.Value, g.AccessGrantsLocationId ?? string.Empty));
        }

        nextToken = response.NextToken;
    }
    while (!string.IsNullOrEmpty(nextToken));

    return grants;
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Connapse.Storage`
Expected: SUCCEEDS.

- [ ] **Step 4: Commit**

```bash
git add src/Connapse.Core/Interfaces/IAccessGrantsReader.cs src/Connapse.Storage/CloudScope/S3AccessGrantsReader.cs
git commit -m "feat: read S3 access grant id, ARN and grantee for reconciliation"
```

### Task 2.2: Add the delete path to the writer + tag read-back

**Files:**
- Modify: `src/Connapse.Core/Interfaces/IAccessGrantsWriter.cs` (add `RevokeAsync` + `GrantRevokeResult`, and `AreManagedAsync` provenance read)
- Modify: `src/Connapse.Storage/CloudScope/S3AccessGrantsWriter.cs`

**Interfaces:**
- Produces:
  - `record GrantRevokeResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> NotFound, IReadOnlyList<GrantWriteFailure> Failed, bool AccessDenied)`
  - `IAccessGrantsWriter.RevokeAsync(string region, IReadOnlyList<string> grantIds, CancellationToken ct = default) → Task<GrantRevokeResult>`
  - `IAccessGrantsWriter.FilterManagedAsync(string region, IReadOnlyList<string> grantArns, CancellationToken ct = default) → Task<IReadOnlyList<string>>` — returns the subset of ARNs tagged `connapse:managed=true`.

- [ ] **Step 1: Add the result type and interface methods** to `IAccessGrantsWriter.cs`:

```csharp
/// <summary>The outcome of deleting grants by id in one region.</summary>
public record GrantRevokeResult(
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> NotFound,
    IReadOnlyList<GrantWriteFailure> Failed,
    bool AccessDenied);
```

Add to `IAccessGrantsWriter`:

```csharp
/// <summary>Deletes the named grants in <paramref name="region"/>. A missing id is NotFound, not a
/// failure — DeleteAccessGrant is not idempotent, so a concurrent delete must not fail the run.</summary>
Task<GrantRevokeResult> RevokeAsync(
    string region, IReadOnlyList<string> grantIds, CancellationToken ct = default);

/// <summary>The subset of <paramref name="grantArns"/> tagged as Connapse-managed. Tags are not
/// returned by ListAccessGrants, so this reads them back per grant via ListTagsForResource.</summary>
Task<IReadOnlyList<string>> FilterManagedAsync(
    string region, IReadOnlyList<string> grantArns, CancellationToken ct = default);
```

- [ ] **Step 2: Implement both in `S3AccessGrantsWriter`**

```csharp
/// <inheritdoc />
public async Task<GrantRevokeResult> RevokeAsync(
    string region, IReadOnlyList<string> grantIds, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(region);

    if (!options.CurrentValue.IsConfigured || grantIds.Count == 0)
        return new GrantRevokeResult([], [], [], AccessDenied: false);

    var endpoint = RegionEndpoint.GetBySystemName(region);
    string account = await ResolveAccountIdAsync(endpoint, ct);

    using var client = new AmazonS3ControlClient(
        credentials, new AmazonS3ControlConfig { RegionEndpoint = endpoint });

    var deleted = new List<string>();
    var notFound = new List<string>();
    var failed = new List<GrantWriteFailure>();
    bool accessDenied = false;

    foreach (string id in grantIds)
    {
        try
        {
            await client.DeleteAccessGrantAsync(
                new DeleteAccessGrantRequest { AccountId = account, AccessGrantId = id }, ct);
            deleted.Add(id);
        }
        catch (AmazonS3ControlException ex) when (IsNotFound(ex))
        {
            notFound.Add(id); // already gone -> success from our point of view
        }
        catch (AmazonS3ControlException ex)
        {
            if (IsAccessDenied(ex)) accessDenied = true;
            failed.Add(new GrantWriteFailure(id, ex.Message));
        }
    }

    return new GrantRevokeResult(deleted, notFound, failed, accessDenied);
}

/// <inheritdoc />
public async Task<IReadOnlyList<string>> FilterManagedAsync(
    string region, IReadOnlyList<string> grantArns, CancellationToken ct = default)
{
    if (!options.CurrentValue.IsConfigured || grantArns.Count == 0)
        return [];

    var endpoint = RegionEndpoint.GetBySystemName(region);
    using var client = new AmazonS3ControlClient(
        credentials, new AmazonS3ControlConfig { RegionEndpoint = endpoint });

    var managed = new List<string>();

    foreach (string arn in grantArns)
    {
        try
        {
            var tags = await client.ListTagsForResourceAsync(
                new ListTagsForResourceRequest { ResourceArn = arn }, ct);

            bool isManaged = (tags.Tags ?? []).Any(t =>
                t.Key == Connapse.Core.Utilities.GrantTags.ManagedKey
                && t.Value == Connapse.Core.Utilities.GrantTags.ManagedValue);

            if (isManaged)
                managed.Add(arn);
        }
        catch (AmazonS3ControlException)
        {
            // Provenance unconfirmed -> fail safe, treat as not ours (never deleted).
        }
    }

    return managed;
}

private static bool IsNotFound(AmazonS3ControlException ex) =>
    ex.StatusCode == System.Net.HttpStatusCode.NotFound
    || (ex.ErrorCode?.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ?? false);
```

(`ListTagsForResourceRequest.ResourceArn`, `DeleteAccessGrantRequest`, and `Tag` are in `Amazon.S3Control.Model`, already imported.)

- [ ] **Step 3: Build**

Run: `dotnet build src/Connapse.Storage`
Expected: SUCCEEDS.

- [ ] **Step 4: Commit**

```bash
git add src/Connapse.Core/Interfaces/IAccessGrantsWriter.cs src/Connapse.Storage/CloudScope/S3AccessGrantsWriter.cs
git commit -m "feat: delete S3 access grants and confirm provenance by tag"
```

### Task 2.3: Grant the delete permission in the role policy

**Files:**
- Modify: `src/Connapse.Core/Utilities/S3SetupPolicy.cs` (`ConnapseManageGrants` statement + summary)
- Test: `tests/Connapse.Core.Tests/Utilities/S3SetupPolicyTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void ForManagedIdentity_GrantsDeleteAndTagAccessGrant()
{
    var manage = Statement(S3SetupPolicy.ForManagedIdentity(), "ConnapseManageGrants");
    var actions = manage.GetProperty("Action").EnumerateArray().Select(a => a.GetString());

    // Cleanup needs delete; provenance needs tag-on-create and reading tags back.
    actions.Should().Contain(["s3:DeleteAccessGrant", "s3:TagResource", "s3:ListTagsForResource"]);
}
```

- [ ] **Step 2: Run it — Expected: FAIL** (`dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~S3SetupPolicyTests"`).

- [ ] **Step 3: Extend the `ConnapseManageGrants` action list** in `S3SetupPolicy.StorageStatements`:

```csharp
["Action"] = new[]
{
    "s3:ListAccessGrants",
    "s3:ListAccessGrantsLocations",
    "s3:CreateAccessGrant",
    "s3:DeleteAccessGrant",
    "s3:TagResource",
    "s3:ListTagsForResource"
},
```

Update the statement's comment to note it now also deletes and tags grants, and update `ManagedIdentitySummary` to say the identity can create **and remove** access grants.

- [ ] **Step 4: Run tests — Expected: PASS** (also re-run `AwsRolesAnywhereSetupTests`, which embeds this policy).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Utilities/S3SetupPolicy.cs tests/Connapse.Core.Tests/Utilities/S3SetupPolicyTests.cs
git commit -m "feat: allow the Connapse role to delete and tag S3 access grants"
```

---

# PR 3 — The reconciler (sweep + button + safety rails)

Ties it together. The pure decision logic is unit-tested exhaustively; the AWS I/O and Hangfire wiring follow existing patterns and are verified manually/live.

### Task 3.1: The pure orphan/candidate selector

The heart of the feature, and where all the safety logic that *can* be pure lives. Given the union of allowed-locations, all grant details, and the configured group, decide which grant ids to delete — reusing `GrantCoverage.Overlaps` for the scope test.

**Files:**
- Create: `src/Connapse.Core/Utilities/GrantReconciler.cs`
- Modify: `src/Connapse.Core/Utilities/GrantCoverage.cs` — make `Normalise`/`Overlaps` reusable (e.g. `internal` → visible to this class, or add a public `Overlaps(scope, location)` façade). Follow the existing visibility; do not duplicate the matcher.
- Test: `tests/Connapse.Core.Tests/Utilities/GrantReconcilerTests.cs`

**Interfaces:**
- Consumes: `AccessGrantDetail` (PR 2), `GrantCoverage.Overlaps`.
- Produces:
  - `GrantReconciler.SelectOrphans(IReadOnlyList<AccessGrantDetail> grants, IReadOnlyList<string> unionLocations, string configuredGroupId) → OrphanSelection`
  - `record OrphanSelection(IReadOnlyList<AccessGrantDetail> Candidates)` — grants that are DIRECTORY_GROUP, orphaned by scope, and (current or previous group). Provenance-by-tag is applied *after* this, on the narrowed set, because it needs an AWS call.

- [ ] **Step 1: Write the failing tests**

```csharp
using Connapse.Core;
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class GrantReconcilerTests
{
    private static AccessGrantDetail Grant(string scope, bool group = true, string id = "g", string grp = "grp-1") =>
        new(AccessGrantId: id, AccessGrantArn: "arn:" + id,
            Grantee: new AccessGrantee(IsGroup: group, Id: grp),
            GrantScope: scope, Permission: "READ", AccessGrantsLocationId: "default");

    [Fact]
    public void SelectOrphans_ScopeCoveredByAConnection_IsNotACandidate()
    {
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://my-bucket/docs/*")],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void SelectOrphans_ScopeCoveredByNoConnection_IsACandidate()
    {
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://old-bucket/*")],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().ContainSingle().Which.AccessGrantId.Should().Be("g");
    }

    [Fact]
    public void SelectOrphans_PreviousGroupOrphan_IsACandidate()
    {
        // A group that is no longer configured, whose scope nothing covers — a previous-group orphan.
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://old-bucket/*", grp: "grp-OLD")],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().ContainSingle();
    }

    [Fact]
    public void SelectOrphans_PreviousGroupButStillCovered_IsLeftAlone()
    {
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://my-bucket/docs/*", grp: "grp-OLD")],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void SelectOrphans_NonGroupGrant_IsNeverACandidate()
    {
        var sel = GrantReconciler.SelectOrphans(
            [Grant("s3://old-bucket/*", group: false)],
            unionLocations: ["my-bucket/docs"],
            configuredGroupId: "grp-1");

        sel.Candidates.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run — Expected: FAIL** (`GrantReconciler` missing).

- [ ] **Step 3: Implement**

```csharp
namespace Connapse.Core.Utilities;

/// <summary>
/// Decides which access grants are orphaned — held by a directory group, covered by no connection.
/// Pure: the AWS-touching provenance check and deletion happen around it.
/// </summary>
/// <remarks>
/// The scope test is <see cref="GrantCoverage"/>'s boundary-aware overlap, the same matcher the
/// coverage reporter uses in the create direction, so "granted" and "orphaned" cannot disagree.
/// This selector is deliberately group-only and does not read tags: provenance requires an AWS call
/// and is applied to the (small) candidate set afterwards.
/// </remarks>
public static class GrantReconciler
{
    public static OrphanSelection SelectOrphans(
        IReadOnlyList<AccessGrantDetail> grants,
        IReadOnlyList<string> unionLocations,
        string configuredGroupId)
    {
        var candidates = new List<AccessGrantDetail>();

        foreach (var grant in grants)
        {
            // Never touch anything but a directory-group grant.
            if (!grant.Grantee.IsGroup || string.IsNullOrWhiteSpace(grant.Grantee.Id))
                continue;

            // Orphaned = its scope overlaps no allowed location across every connection.
            bool covered = unionLocations.Any(loc => GrantCoverage.Overlaps(grant.GrantScope, loc));
            if (covered)
                continue;

            // Current-group orphan or previous-group orphan — both delete; the difference is only
            // which group id it belongs to, and both are handled the same once orphaned.
            candidates.Add(grant);
        }

        return new OrphanSelection(candidates);
    }
}

/// <summary>Grants selected for deletion, before the AWS provenance-tag confirmation.</summary>
public record OrphanSelection(IReadOnlyList<AccessGrantDetail> Candidates);
```

If `GrantCoverage.Overlaps` is not currently accessible from here, add a thin public façade on `GrantCoverage` that calls the existing internal matcher (do not copy the logic). Confirm the exact current signature of the internal `Overlaps`/`Normalise` before wiring.

- [ ] **Step 4: Run — Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Utilities/GrantReconciler.cs src/Connapse.Core/Utilities/GrantCoverage.cs tests/Connapse.Core.Tests/Utilities/GrantReconcilerTests.cs
git commit -m "feat: pure selector for orphaned group access grants"
```

### Task 3.2: The reconcile service (union + fail-closed + circuit breaker + provenance + delete)

**Files:**
- Create: `src/Connapse.Web/Services/GrantReconciliationService.cs` (Web, so it can reach `IConnectionStore`, `IAwsGrantRegions`, `IAccessGrantsReader/Writer`, settings — same layer as `AwsSearchScopeResolver` consumers)
- Modify: DI registration in the appropriate `ServiceCollectionExtensions`
- Test: `tests/Connapse.Web.Tests/Services/GrantReconciliationServiceTests.cs` (NSubstitute the reader/writer/connection store)

**Interfaces:**
- Produces: `IGrantReconciliationService.ReconcileAsync(bool enforce, CancellationToken ct) → Task<ReconcileReport>` where `ReconcileReport(int Scanned, int Orphaned, int Deleted, IReadOnlyList<string> Aborted)` and `Aborted` carries the fail-closed/circuit-breaker reasons. `enforce=false` computes and reports without deleting (used by the button's preview; the sweep calls `enforce=true`).

- [ ] **Step 1: Write failing tests for the safety rails** (the parts that are testable with mocks):

```csharp
// 1. Connection enumeration failure -> aborts, deletes nothing.
[Fact] public async Task Reconcile_ConnectionListThrows_AbortsWithoutDeleting() { /* substitute IConnectionStore.ListAsync to throw; assert writer.RevokeAsync never called and report.Aborted non-empty */ }

// 2. Circuit breaker: candidates exceed threshold -> abort.
[Fact] public async Task Reconcile_ImplausiblyManyCandidates_TripsCircuitBreaker() { /* reader returns many managed orphans, empty union; assert no delete, Aborted names the breaker */ }

// 3. Happy path: one orphan, tagged -> deleted.
[Fact] public async Task Reconcile_TaggedOrphan_IsDeleted() { /* reader ListAllAsync returns 1 group orphan; writer.FilterManagedAsync returns its ARN; assert RevokeAsync called with its id */ }

// 4. Untagged orphan -> never deleted.
[Fact] public async Task Reconcile_UntaggedOrphan_IsSkipped() { /* FilterManagedAsync returns empty; assert RevokeAsync not called */ }
```

Write these fully against the concrete interfaces (substitute `IConnectionStore`, `IAccessGrantsReader`, `IAccessGrantsWriter`, `IAwsGrantRegions`, `IOptionsMonitor<SamlSignInSettings>`).

- [ ] **Step 2: Run — Expected: FAIL.**

- [ ] **Step 3: Implement `GrantReconciliationService`**, in this order per tick:
  1. Read `SamlSignInSettings`; capture `GrantGroupId`/`HasGrantGroup`.
  2. **Union:** `connections.ListAsync(0, int.MaxValue, ct)` filtered to `Provider == S3`; parse each with `StorageLocationPolicy.ReadAllowedLocations`. On **any** exception, or **any** connection whose config won't parse, return a `ReconcileReport` with an `Aborted` reason and **do not delete** (fail-closed).
  3. For each region in `regions.ListAsync(ct)`: `reader.ListAllAsync(region)`; `GrantReconciler.SelectOrphans(...)`.
  4. **Circuit breaker:** if candidate count in a region exceeds the configured threshold (a `GrantReconciliationSettings.MaxDeletePerTick`, default e.g. 50, and/or "= the entire managed set"), abort that region with a logged alert; delete nothing there.
  5. **Provenance:** `writer.FilterManagedAsync(region, candidateArns)`; keep only confirmed-managed.
  6. If `enforce`, `writer.RevokeAsync(region, managedOrphanIds)`; log each deletion at audit level (`LogSanitizer`).
  7. Aggregate into `ReconcileReport`.

- [ ] **Step 4: Run — Expected: PASS.** Then `dotnet build`.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Web/Services/GrantReconciliationService.cs tests/Connapse.Web.Tests/Services/GrantReconciliationServiceTests.cs [DI file]
git commit -m "feat: fail-closed reconcile service for orphaned access grants"
```

### Task 3.3: Hangfire recurring sweep

**Files:**
- Create: `src/Connapse.Background/Jobs/GrantReconciliationJobs.cs` (`IGrantReconciliationJobs`)
- Modify: `src/Connapse.Background/HangfireServiceCollectionExtensions.cs` (register `AddScoped`)
- Modify: `src/Connapse.Web/Program.cs` (recurring-job registration, next to `summary-sweep-stale-containers` ~line 351)

- [ ] **Step 1: Implement the job**, mirroring `SummaryJobs.SweepStaleContainersAsync`: a scoped class injecting `IGrantReconciliationService` + `ILogger`, one method `ReconcileAsync` decorated `[Queue(JobQueues.Default)]` and `[AutomaticRetry(Attempts = 0)]` and `[DisableConcurrentExecution(600)]`, calling `service.ReconcileAsync(enforce: true, ct)` and logging the report.

- [ ] **Step 2: Register** the job `AddScoped<IGrantReconciliationJobs, GrantReconciliationJobs>()` in `AddConnapseHangfire`, and in `Program.cs` add:

```csharp
recurringJobs.AddOrUpdate<IGrantReconciliationJobs>(
    "grant-reconcile-orphaned", j => j.ReconcileAsync(default), "*/30 * * * *");
```

(30-minute cadence; adjust to taste. Idempotent AddOrUpdate, safe every boot — same as the summary sweep.)

- [ ] **Step 3: Build the solution** — Expected: SUCCEEDS.

- [ ] **Step 4: Commit**

```bash
git add src/Connapse.Background/Jobs/GrantReconciliationJobs.cs src/Connapse.Background/HangfireServiceCollectionExtensions.cs src/Connapse.Web/Program.cs
git commit -m "feat: recurring sweep to reconcile orphaned access grants"
```

### Task 3.4: Admin "Clean up orphaned grants" button

**Files:**
- Modify: the AWS provider admin page (`src/Connapse.Web/Components/Pages/Providers.razor`, near the per-user-permissions section) — a button calling `IGrantReconciliationService.ReconcileAsync(enforce: true, ct)` and showing the returned `ReconcileReport` (scanned / orphaned / deleted / any aborted reason). Follow the existing button/result-line pattern used by the grant/reset actions.

- [ ] **Step 1: Add the button + result line**, gated on per-user permissions being configured. On click, call the service, render the report (e.g. "Removed N orphaned grants" or the abort reason).

- [ ] **Step 2: Build** — Expected: SUCCEEDS.

- [ ] **Step 3: Manual verification** (needs the live AWS test instance + at least one Connapse-created grant whose bucket has been removed from every connection): click the button; confirm the orphaned grant is deleted and an admin grant over the same bucket is left intact; confirm removing all connections then running does NOT wipe everything (circuit breaker / fail-closed).

- [ ] **Step 4: Commit**

```bash
git add src/Connapse.Web/Components/Pages/Providers.razor
git commit -m "feat: admin button to clean up orphaned access grants"
```

### Task 3.5: Instance tag (multi-instance scoping, spec §6) — DEFERRED

**Status: deferred.** Not built in the initial epic. Reason: the instance fingerprint has no clean
universal source in `S3AccessGrantsWriter` (the Roles Anywhere trust-anchor ARN is not wired into
the writer, and ambient on-AWS deployments hold no cert at all), and the common single-instance case
is correct without it. Resolving the fingerprint source is a design decision for the owner. The
single-instance assumption is documented in `GrantReconciliationService`'s remarks. When taken up:

**Files:**
- Modify: `S3AccessGrantsWriter.CreateAsync` (add `connapse:instance` tag) and `GrantTags` (add `InstanceKey`).
- Modify: `GrantReconciliationService` — when a `GrantReconciliationSettings.MultiInstanceScoping` flag is on, `FilterManagedAsync` (or a variant) also requires the instance tag to match this instance's fingerprint; default off (single-instance deletes any managed orphan).
- Resolve the fingerprint source: reuse the stable per-instance identifier the Roles Anywhere credential already establishes (e.g. a hash of the stored `TrustAnchorArn`), or a persisted install-id GUID when ambient/BYO. Pick one concretely during this task and document it.

- [ ] Steps: add the constant + tag on create; extend the provenance filter to check the instance tag under the flag; unit-test that under scoping-on a grant tagged for another instance is not selected. Commit `feat: scope grant cleanup to the creating Connapse instance`.

---

## Self-Review

**Spec coverage:** §Provenance/tagging → Task 1.1 (+ instance in 3.5). §1 → PR1. §2 reader → 2.1. §3 delete+policy → 2.2, 2.3. §4 reconciler (union, fail-closed, candidate, circuit breaker, provenance, delete) → 3.1 (pure selection) + 3.2 (service safety rails) + 3.3 (sweep) + 3.4 (button). §5 previous-group → covered in 3.1 (grantee ≠ configured group still selected when orphaned) and its test. §6 multi-instance → 3.5. §7 safety invariants → enforced across 3.1/3.2 and asserted in 3.2's tests. §8 testing → per task. §9 delivery/build order → the three PR sections. ✅

**Placeholder scan:** Task 3.2 Step 1 gives test *names + intent* rather than full bodies, and 3.5 is a compact task — both are the more-distant, larger tasks where the exact mock wiring depends on the interfaces built in 3.1/2.x; flagged here so the implementer writes them fully at that point rather than treating the outline as done. No TBD/TODO in the load-bearing near-term tasks (PR1, PR2, 3.1).

**Type consistency:** `AccessGrantDetail` (2.1) is consumed by `GrantReconciler.SelectOrphans` (3.1) and the service (3.2). `GrantRevokeResult`/`RevokeAsync` and `FilterManagedAsync` (2.2) are consumed by the service (3.2). `GrantTags.ManagedKey/Value` (1.1) is used by create (1.1) and the provenance filter (2.2). `GrantCoverage.Overlaps` reused by 3.1 rather than re-derived.

**Deviation from spec:** the `connapse:instance` tag is applied in PR3/Task 3.5 rather than PR1 (spec §1 lists both at create), because its fingerprint source and its only consumer (instance-scoped deletion) both live in the multi-instance work. Recorded here; the `connapse:managed` provenance marker — the one deletion actually gates on — still ships in PR1.
