# Auto-create S3 Access Grants Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Connapse's own AWS identity create the S3 Access Grants that make a connection's buckets searchable per-user, so an admin clicks a button on the connection instead of pasting a CloudShell script.

**Architecture:** Mirror the existing read path (`IAccessGrantsReader` → `S3AccessGrantsReader`) with a write twin. A pure `GrantPlanner` decides which grants to create vs. skip (unit-tested); a thin `S3AccessGrantsWriter` does the AWS I/O with Connapse's `ConnapseAwsCredentials` (verified manually/live, matching the reader). The role's `ConnapseRead` policy gains the grant-write actions. On the connection page, a "Grant access" button calls the writer and degrades to the existing CloudShell script when the identity is denied.

**Tech Stack:** .NET 10, C#, Blazor Server, AWS SDK for .NET (`AWSSDK.S3Control`), xUnit + FluentAssertions + NSubstitute.

**Spec:** [docs/superpowers/specs/2026-09-02-aws-auto-create-access-grants-design.md](../specs/2026-09-02-aws-auto-create-access-grants-design.md)

## Global Constraints

- .NET 10; file-scoped namespaces; nullable enabled; implicit usings.
- Records for DTOs/results; primary constructors for DI. Don't use `var` for primitive types; no `dynamic`.
- Async all the way — never `.Result`/`.Wait()`.
- **READ grants only** — never create WRITE or READWRITE (a write-only grant read back as readable is a disclosure).
- **Idempotent** — existing grants are read first and skipped; a second run creates nothing.
- **Fail-closed on partial error** — one location's failure never aborts the rest, and the result reports exactly which failed and why; never a silent partial success.
- **Connection's region only** — a grant is created against the Access Grants instance in the bucket's region; the writer is only ever called with the connection's own region.
- When logging user-controlled values (region, grantee id) wrap them with `LogSanitizer.Sanitize(...)` from `Connapse.Core.Utilities`.
- Test naming: `MethodName_Scenario_ExpectedResult`; tag unit tests `[Trait("Category", "Unit")]`.
- Commit message types: `feat:`, `test:`, `refactor:`. End commit messages with the Co-Authored-By trailer.

---

### Task 1: Pure grant planner

The one piece that guarantees the writer creates exactly the scope shape the reader and `GrantCoverageReporter` expect. Given the connection's ungranted locations and the grantee's existing grant scopes, decide which subprefixes to create and which are already covered. Reuses `AccessGrantScript.SanitiseLocation` so the location→subprefix shaping has a single source.

**Files:**
- Create: `src/Connapse.Core/Utilities/GrantPlanner.cs`
- Test: `tests/Connapse.Core.Tests/Utilities/GrantPlannerTests.cs`

**Interfaces:**
- Consumes: `AccessGrantScript.SanitiseLocation(string?)` (existing, public — trims, rejects unsafe, drops trailing slash).
- Produces:
  - `GrantPlanner.Plan(IEnumerable<string> requestedLocations, IEnumerable<string> existingScopes) → GrantPlan`
  - `record GrantPlan(IReadOnlyList<string> ToCreate, IReadOnlyList<string> AlreadyGranted)` — each string is a subprefix in the form `bucket/prefix/*` (no `s3://`), ready to pass as `S3SubPrefix`.

- [ ] **Step 1: Write the failing test**

```csharp
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class GrantPlannerTests
{
    [Fact]
    public void Plan_LocationWithNoExistingGrant_IsToCreateAsSubPrefixStar()
    {
        var plan = GrantPlanner.Plan(["my-bucket/docs"], existingScopes: []);

        plan.ToCreate.Should().ContainSingle().Which.Should().Be("my-bucket/docs/*");
        plan.AlreadyGranted.Should().BeEmpty();
    }

    [Fact]
    public void Plan_LocationAlreadyGranted_IsSkipped()
    {
        var plan = GrantPlanner.Plan(
            ["my-bucket/docs"],
            existingScopes: ["s3://my-bucket/docs/*"]);

        plan.ToCreate.Should().BeEmpty();
        plan.AlreadyGranted.Should().ContainSingle().Which.Should().Be("my-bucket/docs/*");
    }

    [Fact]
    public void Plan_BucketRoot_BecomesBucketStar()
    {
        var plan = GrantPlanner.Plan(["my-bucket"], existingScopes: []);

        plan.ToCreate.Should().ContainSingle().Which.Should().Be("my-bucket/*");
    }

    [Fact]
    public void Plan_TrailingSlashAndDuplicates_AreNormalisedAndDeduped()
    {
        var plan = GrantPlanner.Plan(
            ["my-bucket/docs/", "my-bucket/docs"],
            existingScopes: []);

        plan.ToCreate.Should().ContainSingle().Which.Should().Be("my-bucket/docs/*");
    }

    [Fact]
    public void Plan_UnsafeLocation_IsDropped()
    {
        var plan = GrantPlanner.Plan(["bad bucket$name"], existingScopes: []);

        plan.ToCreate.Should().BeEmpty();
        plan.AlreadyGranted.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~GrantPlannerTests"`
Expected: FAIL — `GrantPlanner` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Connapse.Core.Utilities;

/// <summary>
/// Decides which S3 Access Grants to create for a grantee, given the grants they already hold.
/// </summary>
/// <remarks>
/// Pure so it can be tested without AWS, and so the subprefix shape — <c>bucket/prefix/*</c> — has
/// one source shared with <see cref="AccessGrantScript"/>. The writer creates exactly what this
/// returns; if the shape drifted from what the reader parses, a grant just created would still read
/// as ungranted.
/// </remarks>
public static class GrantPlanner
{
    /// <summary>Splits requested locations into those needing a grant and those already covered.</summary>
    /// <param name="requestedLocations">
    /// Buckets, each optionally followed by <c>/</c> and a prefix — the connection's ungranted
    /// locations.
    /// </param>
    /// <param name="existingScopes">
    /// The grantee's current grant scopes as AWS reports them, e.g. <c>s3://bucket/prefix/*</c>.
    /// </param>
    public static GrantPlan Plan(
        IEnumerable<string> requestedLocations, IEnumerable<string> existingScopes)
    {
        var existing = new HashSet<string>(
            (existingScopes ?? []).Select(s => s?.Trim() ?? string.Empty),
            StringComparer.Ordinal);

        var toCreate = new List<string>();
        var alreadyGranted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string location in requestedLocations ?? [])
        {
            string sanitised = AccessGrantScript.SanitiseLocation(location);
            if (sanitised.Length == 0)
                continue;

            // The shape a grant is created and read back as. AWS refuses a grant on the bare
            // s3:// location, so every one names a bucket and the trailing star makes it a subtree.
            string subPrefix = sanitised + "/*";

            if (!seen.Add(subPrefix))
                continue;

            if (existing.Contains("s3://" + subPrefix))
                alreadyGranted.Add(subPrefix);
            else
                toCreate.Add(subPrefix);
        }

        return new GrantPlan(toCreate, alreadyGranted);
    }
}

/// <summary>The outcome of planning grants: what to create, and what already exists.</summary>
public record GrantPlan(IReadOnlyList<string> ToCreate, IReadOnlyList<string> AlreadyGranted);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~GrantPlannerTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Utilities/GrantPlanner.cs tests/Connapse.Core.Tests/Utilities/GrantPlannerTests.cs
git commit -m "feat: grant planner deciding which S3 access grants to create"
```

---

### Task 2: Grant writer interface + S3 implementation + DI

Add the write twin of the reader. The interface and result types live in Core beside `IAccessGrantsReader`; the S3 implementation lives beside `S3AccessGrantsReader` and is constructed identically. The AWS-I/O class is not unit-tested at the client boundary — the reader isn't either — so its correctness rests on Task 1's planner tests plus the live-AWS check in Task 5's verification.

**Files:**
- Create: `src/Connapse.Core/Interfaces/IAccessGrantsWriter.cs` (namespace `Connapse.Core`, matching `IAccessGrantsReader.cs`)
- Create: `src/Connapse.Storage/CloudScope/S3AccessGrantsWriter.cs`
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs:219` (register beside the reader)

**Interfaces:**
- Consumes: `AccessGrantee` (existing, `Connapse.Core`), `GrantPlanner.Plan(...)` / `GrantPlan` (Task 1), `ConnapseAwsCredentials`, `IOptionsMonitor<IdentityCenterSettings>` (both already injected into `S3AccessGrantsReader`).
- Produces:
  - `IAccessGrantsWriter.GrantReadAsync(AccessGrantee grantee, string region, IReadOnlyList<string> locations, CancellationToken ct = default) → Task<GrantWriteResult>`
  - `record GrantWriteResult(IReadOnlyList<string> Created, IReadOnlyList<string> AlreadyGranted, IReadOnlyList<GrantWriteFailure> Failed, bool AccessDenied)` with `bool Succeeded => Failed.Count == 0;`
  - `record GrantWriteFailure(string Location, string Reason)`

- [ ] **Step 1: Create the interface and result types**

```csharp
namespace Connapse.Core;

/// <summary>One location that could not be granted, and why.</summary>
public record GrantWriteFailure(string Location, string Reason);

/// <summary>The outcome of creating grants for one grantee against one region.</summary>
/// <param name="Created">Subprefixes a grant was created for.</param>
/// <param name="AlreadyGranted">Subprefixes a grant already covered (nothing created).</param>
/// <param name="Failed">Locations that could not be granted, with the AWS reason.</param>
/// <param name="AccessDenied">
/// True when a failure was AWS <c>AccessDenied</c> — Connapse's identity is not (yet) allowed to
/// create grants, so the UI should fall back to the admin-run CloudShell script rather than present
/// the failure as broken.
/// </param>
public record GrantWriteResult(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> AlreadyGranted,
    IReadOnlyList<GrantWriteFailure> Failed,
    bool AccessDenied)
{
    /// <summary>Nothing failed.</summary>
    public bool Succeeded => Failed.Count == 0;

    /// <summary>An empty result — nothing requested, or the feature is not configured.</summary>
    public static readonly GrantWriteResult Nothing = new([], [], [], AccessDenied: false);
}

/// <summary>
/// Creates S3 Access Grants using Connapse's own AWS identity.
/// </summary>
/// <remarks>
/// The write twin of <see cref="IAccessGrantsReader"/>. Connapse now creates grants as well as
/// reading them: its runtime identity is deliberately no longer read-only, so a compromise of that
/// identity can create grants. The blast radius is stated to the administrator on the setup page
/// (see <c>S3SetupPolicy.ManagedIdentitySummary</c>).
/// </remarks>
public interface IAccessGrantsWriter
{
    /// <summary>
    /// Creates one READ grant per location for <paramref name="grantee"/>, against the Access
    /// Grants instance in <paramref name="region"/>. Idempotent: existing grants are read first and
    /// skipped. Never creates WRITE or READWRITE.
    /// </summary>
    Task<GrantWriteResult> GrantReadAsync(
        AccessGrantee grantee, string region,
        IReadOnlyList<string> locations, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the S3 implementation**

Mirror `S3AccessGrantsReader` (`src/Connapse.Storage/CloudScope/S3AccessGrantsReader.cs`) — same constructor, same account-id caching, same `IsConfigured` guard, same "SDK leaves collections null" handling.

```csharp
using System.Net;
using Amazon;
using Amazon.S3Control;
using Amazon.S3Control.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Connapse.Core;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Creates S3 Access Grants with Connapse's own AWS identity.
/// </summary>
/// <remarks>
/// The write twin of <see cref="S3AccessGrantsReader"/>, built the same way and against the same
/// account. It reads the grantee's existing grants once and creates only what
/// <see cref="GrantPlanner"/> says is missing, so a rerun converges rather than duplicates.
/// </remarks>
public sealed class S3AccessGrantsWriter(
    ConnapseAwsCredentials credentials,
    IOptionsMonitor<IdentityCenterSettings> options) : IAccessGrantsWriter
{
    private string? accountId;

    /// <inheritdoc />
    public async Task<GrantWriteResult> GrantReadAsync(
        AccessGrantee grantee, string region,
        IReadOnlyList<string> locations, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantee.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        if (!options.CurrentValue.IsConfigured || locations.Count == 0)
            return GrantWriteResult.Nothing;

        var endpoint = RegionEndpoint.GetBySystemName(region);
        string account = await ResolveAccountIdAsync(endpoint, ct);

        using var client = new AmazonS3ControlClient(
            credentials, new AmazonS3ControlConfig { RegionEndpoint = endpoint });

        GrantPlan plan;
        try
        {
            string? locationId = await FindRootLocationAsync(client, account, ct);
            if (locationId is null)
                return AllFailed(locations,
                    "No s3:// location is registered. Run the Access Grants setup step in Connapse first.",
                    accessDenied: false);

            IReadOnlyList<string> existing = await ListGranteeScopesAsync(client, account, grantee, ct);
            plan = GrantPlanner.Plan(locations, existing);

            return await CreateAsync(client, account, grantee, plan, locationId, ct);
        }
        catch (AmazonS3ControlException ex) when (IsAccessDenied(ex))
        {
            // The identity cannot even look up the location or existing grants. Report every
            // requested location as denied so the UI shows the CloudShell fallback rather than a
            // partial, confusing result.
            return AllFailed(locations, ex.Message, accessDenied: true);
        }
    }

    private async Task<GrantWriteResult> CreateAsync(
        AmazonS3ControlClient client, string account, AccessGrantee grantee,
        GrantPlan plan, string locationId, CancellationToken ct)
    {
        var created = new List<string>();
        var alreadyGranted = new List<string>(plan.AlreadyGranted);
        var failed = new List<GrantWriteFailure>();
        bool accessDenied = false;

        foreach (string subPrefix in plan.ToCreate)
        {
            try
            {
                await client.CreateAccessGrantAsync(new CreateAccessGrantRequest
                {
                    AccountId = account,
                    AccessGrantsLocationId = locationId,
                    AccessGrantsLocationConfiguration =
                        new AccessGrantsLocationConfiguration { S3SubPrefix = subPrefix },
                    Permission = Permission.READ,
                    Grantee = new Grantee
                    {
                        GranteeType = grantee.IsGroup
                            ? GranteeType.DIRECTORY_GROUP
                            : GranteeType.DIRECTORY_USER,
                        GranteeIdentifier = grantee.Id,
                    },
                }, ct);

                created.Add(subPrefix);
            }
            catch (AmazonS3ControlException ex) when (IsConflict(ex))
            {
                // The read-then-create race backstop: already there is success, not failure.
                alreadyGranted.Add(subPrefix);
            }
            catch (AmazonS3ControlException ex)
            {
                if (IsAccessDenied(ex))
                    accessDenied = true;

                // Keep going: one bad bucket must not hide the rest.
                failed.Add(new GrantWriteFailure(subPrefix, ex.Message));
            }
        }

        return new GrantWriteResult(created, alreadyGranted, failed, accessDenied);
    }

    private static async Task<string?> FindRootLocationAsync(
        AmazonS3ControlClient client, string account, CancellationToken ct)
    {
        var response = await client.ListAccessGrantsLocationsAsync(
            new ListAccessGrantsLocationsRequest { AccountId = account }, ct);

        return (response.AccessGrantsLocationsList ?? [])
            .FirstOrDefault(l => l.LocationScope == "s3://")
            ?.AccessGrantsLocationId;
    }

    private static async Task<IReadOnlyList<string>> ListGranteeScopesAsync(
        AmazonS3ControlClient client, string account, AccessGrantee grantee, CancellationToken ct)
    {
        List<string> scopes = [];
        string? nextToken = null;

        do
        {
            var response = await client.ListAccessGrantsAsync(
                new ListAccessGrantsRequest
                {
                    AccountId = account,
                    GranteeType = grantee.IsGroup
                        ? GranteeType.DIRECTORY_GROUP
                        : GranteeType.DIRECTORY_USER,
                    GranteeIdentifier = grantee.Id,
                    NextToken = nextToken,
                }, ct);

            // Null, not empty, when the account holds no grants — the AWS SDK leaves response
            // collections unset rather than initialising them.
            scopes.AddRange((response.AccessGrantsList ?? [])
                .Select(g => g.GrantScope)
                .Where(s => !string.IsNullOrWhiteSpace(s))!);

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return scopes;
    }

    private async Task<string> ResolveAccountIdAsync(RegionEndpoint region, CancellationToken ct)
    {
        if (accountId is { Length: > 0 })
            return accountId;

        using var sts = new AmazonSecurityTokenServiceClient(
            credentials, new AmazonSecurityTokenServiceConfig { RegionEndpoint = region });

        var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
        accountId = identity.Account;
        return accountId;
    }

    private static GrantWriteResult AllFailed(
        IReadOnlyList<string> locations, string reason, bool accessDenied) =>
        new([], [], [.. locations.Select(l => new GrantWriteFailure(l, reason))], accessDenied);

    private static bool IsAccessDenied(AmazonS3ControlException ex) =>
        ex.StatusCode == HttpStatusCode.Forbidden
        || (ex.ErrorCode?.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsConflict(AmazonS3ControlException ex) =>
        ex.StatusCode == HttpStatusCode.Conflict
        || (ex.ErrorCode?.Contains("Conflict", StringComparison.OrdinalIgnoreCase) ?? false)
        || (ex.Message?.Contains("already", StringComparison.OrdinalIgnoreCase) ?? false);
}
```

- [ ] **Step 3: Register in DI**

In `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`, immediately after the reader registration at line 219:

```csharp
services.AddSingleton<IAccessGrantsReader, CloudScope.S3AccessGrantsReader>();
services.AddSingleton<IAccessGrantsWriter, CloudScope.S3AccessGrantsWriter>();
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/Connapse.Storage`
Expected: SUCCEEDS. (If `Grantee`, `Permission`, `AccessGrantsLocationConfiguration`, or `ListAccessGrantsLocationsRequest` resolve to a different name in the installed `AWSSDK.S3Control` version, correct the member names against IntelliSense — the shapes exist in the SDK the reader already uses.)

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Interfaces/IAccessGrantsWriter.cs src/Connapse.Storage/CloudScope/S3AccessGrantsWriter.cs src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat: create S3 access grants with Connapse's own identity"
```

---

### Task 3: Grant-write permissions in the role policy

The role can only create grants if its `ConnapseRead` policy allows it, and the policy's self-description must stop claiming read-only. `S3SetupPolicy.ForManagedIdentity()` is embedded verbatim into the Roles Anywhere setup script (`AwsRolesAnywhereSetup.GenerateScript`, which attaches it as the `ConnapseRead` policy), so editing it here flows straight into what the role is granted.

**Files:**
- Modify: `src/Connapse.Core/Utilities/S3SetupPolicy.cs` — extend `StorageStatements` (the `ConnapseReadGrants` statement) and rewrite `ManagedIdentitySummary`.
- Test: `tests/Connapse.Core.Tests/Utilities/S3SetupPolicyTests.cs` (extend)

**Interfaces:**
- Consumes: nothing new.
- Produces: `S3SetupPolicy.ForManagedIdentity()` now includes `s3:CreateAccessGrant` and `s3:ListAccessGrantsLocations`; `S3SetupPolicy.ManagedIdentitySummary` no longer claims read-only.

- [ ] **Step 1: Write the failing tests**

Add to `S3SetupPolicyTests`:

```csharp
[Fact]
public void ForManagedIdentity_GrantsCreateAccessGrant()
{
    string policy = S3SetupPolicy.ForManagedIdentity();

    policy.Should().Contain("s3:CreateAccessGrant");
    policy.Should().Contain("s3:ListAccessGrantsLocations");
}

[Fact]
public void ManagedIdentitySummary_DoesNotClaimReadOnly()
{
    // The identity now creates access grants; a summary that still says it cannot change
    // anything would be a false statement on the setup page.
    S3SetupPolicy.ManagedIdentitySummary.Should().NotContain("cannot write");
    S3SetupPolicy.ManagedIdentitySummary.Should().Contain("access grant", Exactly.Once());
}
```

(If `Exactly` is not in scope, assert `.ToLowerInvariant().Should().Contain("access grant")` instead.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~S3SetupPolicyTests"`
Expected: FAIL — the two new grant actions are absent and the summary still says "cannot write, delete, or change anything".

- [ ] **Step 3: Extend the policy statement**

In `S3SetupPolicy.cs`, replace the `ConnapseReadGrants` statement inside `StorageStatements` so it carries the write actions alongside the read, and update its comment to say so. The old statement:

```csharp
new Dictionary<string, object>
{
    ["Sid"] = "ConnapseReadGrants",
    ["Effect"] = "Allow",
    ["Action"] = new[] { "s3:ListAccessGrants" },
    ["Resource"] = $"arn:aws:s3:*:{AccountPlaceholder}:access-grants/*"
},
```

becomes:

```csharp
// Connapse both reads and now creates access grants. Creating a grant is an access-control
// decision the runtime identity is deliberately allowed to make (see ManagedIdentitySummary);
// ListAccessGrantsLocations finds the s3:// location a grant attaches to. Same access-grants
// resource as the read, and the account is substituted by the script from sts:GetCallerIdentity.
new Dictionary<string, object>
{
    ["Sid"] = "ConnapseManageGrants",
    ["Effect"] = "Allow",
    ["Action"] = new[]
    {
        "s3:ListAccessGrants",
        "s3:ListAccessGrantsLocations",
        "s3:CreateAccessGrant"
    },
    ["Resource"] = $"arn:aws:s3:*:{AccountPlaceholder}:access-grants/*"
},
```

- [ ] **Step 4: Rewrite the summary**

Replace `ManagedIdentitySummary`:

```csharp
public const string ManagedIdentitySummary =
    "reading every S3 bucket in the account. It cannot write, delete, or change anything.";
```

with:

```csharp
public const string ManagedIdentitySummary =
    "reading every S3 bucket in the account, and creating S3 access grants (granting directory "
    + "groups read access to buckets). It cannot read, write, delete, or change the objects "
    + "themselves beyond reading them.";
```

Also soften the one-line "read-only" claims in the `ForManagedIdentity` doc-comment where it says the identity is "read-only across every AWS storage service" and "the identity is read-only, belongs to Connapse alone" — reword to note it can also create access grants. (Comment-only; no behaviour.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~S3SetupPolicyTests"`
Expected: PASS.

- [ ] **Step 6: Check the Roles Anywhere setup test still holds**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~AwsRolesAnywhereSetupTests"`
Expected: PASS. If a test asserts the embedded policy's exact content or the old `ConnapseReadGrants` Sid, update that assertion to match the new `ConnapseManageGrants` statement — the policy is embedded by that script generator.

- [ ] **Step 7: Commit**

```bash
git add src/Connapse.Core/Utilities/S3SetupPolicy.cs tests/Connapse.Core.Tests/Utilities/S3SetupPolicyTests.cs
git commit -m "feat: allow the Connapse role to create S3 access grants"
```

---

### Task 4: "Grant access" button on the connection

Replace the copy-the-command affordance with a button that creates the grants directly, refreshes coverage in place, and falls back to the CloudShell script only when the identity is denied. The script (`AccessGrantScript` / `GrantCommand`) is kept as that fallback.

**Files:**
- Modify: `src/Connapse.Web/Components/Pages/Connections.razor` — the markup block at lines 800–824 and the `@code` region around `GrantCommand`/`CopyGrantCommand` (~1260–1341).

**Interfaces:**
- Consumes: `IAccessGrantsWriter.GrantReadAsync(...)` (Task 2); existing `SamlOptions.CurrentValue` (`HasGrantGroup`, `GrantGroupId`, `GrantGroupName`), `coverage` (`HasWarning`, `Ungranted`), `form.Region`, `RefreshUngrantedAsync()`, `RegionMismatch`, `GrantGroupLabel`, `GrantCommand`.

- [ ] **Step 1: Inject the writer**

Near the other `@inject` lines at the top of `Connections.razor`, add:

```razor
@inject IAccessGrantsWriter GrantsWriter
```

- [ ] **Step 2: Add the grant action to the `@code` block**

Add beside `CopyGrantCommand` (the `Connapse.Core` namespace holding `AccessGrantee` is already available in the page's usings via `GrantCommand`; if not, add `@using Connapse.Core`):

```csharp
private bool granting;
private string? grantResultMessage;
private bool grantResultOk;
private bool grantFellBack;   // an AccessDenied pushed us back to the CloudShell script

/// <summary>Whether Connapse can offer to create the grants itself.</summary>
/// <remarks>
/// The same conditions the command was shown under: a group is configured, this connection has
/// ungranted buckets, and the regions are not mismatched. When true the button is offered; the
/// CloudShell script appears only after <see cref="grantFellBack"/> is set by an AccessDenied.
/// </remarks>
private bool CanGrant =>
    SamlOptions.CurrentValue.HasGrantGroup
    && coverage is { HasWarning: true }
    && RegionMismatch is null;

private async Task GrantAccess()
{
    grantResultMessage = null;
    grantFellBack = false;
    granting = true;

    try
    {
        var signIn = SamlOptions.CurrentValue;
        var grantee = new AccessGrantee(IsGroup: true, Id: signIn.GrantGroupId);

        var result = await GrantsWriter.GrantReadAsync(
            grantee, form.Region, coverage!.Ungranted, CancellationToken.None);

        if (result.Succeeded)
        {
            // Re-check so the warning clears in place, no page reload.
            await RefreshCoverageAsync();
            grantResultOk = true;
            grantResultMessage = result.Created.Count > 0
                ? $"Granted read access to {GrantGroupLabel} on {result.Created.Count} location(s). "
                  + "Searches start returning these documents within a minute."
                : $"{GrantGroupLabel} was already granted everything here.";
        }
        else if (result.AccessDenied)
        {
            // Connapse's identity is not allowed to create grants (a bring-your-own narrow role,
            // or the policy not applied yet). Fall back to the admin-run CloudShell script.
            grantFellBack = true;
            grantResultOk = false;
            grantResultMessage =
                "Connapse's AWS identity is not allowed to create access grants. "
                + "Use the CloudShell command below instead, or add s3:CreateAccessGrant to its policy.";
        }
        else
        {
            grantResultOk = false;
            grantResultMessage =
                "Could not grant on: "
                + string.Join("; ", result.Failed.Select(f => $"{f.Location} ({f.Reason})"));
        }
    }
    catch (Exception ex)
    {
        grantResultOk = false;
        grantResultMessage = $"Could not create the grants: {ex.Message}";
    }
    finally
    {
        granting = false;
    }
}
```

- [ ] **Step 3: Add a coverage refresh for this one connection**

`RefreshUngrantedAsync` walks the whole page; the button needs the currently-edited connection's `coverage` recomputed. Add:

```csharp
/// <summary>Recomputes this connection's grant coverage after a grant is created.</summary>
private async Task RefreshCoverageAsync()
{
    coverage = await GrantCoverage.CheckAsync(AllowedList());
    await RefreshUngrantedAsync();
}
```

(Confirm the field/method names against the file: `coverage` is the current connection's report, `GrantCoverage` is the injected reporter, and `AllowedList()` yields this form's locations. If the page already exposes a single-connection refresh, call that instead of duplicating.)

- [ ] **Step 4: Replace the markup block**

Replace the `else if (GrantCommand is { Length: > 0 } grantCommand)` branch (lines 800–824) with the button-first version. The CloudShell block moves behind `grantFellBack`:

```razor
                                else if (CanGrant)
                                {
                                    <div class="mt-2">
                                        <div class="small mb-1">
                                            Grant read access to <strong>@(GrantGroupLabel)</strong>
                                            for the buckets above, using Connapse's AWS identity.
                                        </div>
                                        <div class="d-flex flex-wrap gap-2 align-items-center">
                                            <button type="button" class="btn btn-sm btn-primary"
                                                    @onclick="GrantAccess" disabled="@granting">
                                                @if (granting)
                                                {
                                                    <span class="spinner-border spinner-border-sm me-1"></span>
                                                }
                                                <span class="bi-key me-1"></span> Grant access
                                            </button>
                                            @if (grantResultMessage is { Length: > 0 })
                                            {
                                                <span class="small @(grantResultOk ? "text-success" : "text-danger")">
                                                    @grantResultMessage
                                                </span>
                                            }
                                        </div>

                                        @if (grantFellBack && GrantCommand is { Length: > 0 } grantCommand)
                                        {
                                            <div class="small mt-2 mb-1">
                                                Run this in AWS CloudShell with your own credentials instead:
                                            </div>
                                            <pre class="bg-body-tertiary border rounded p-2 small mb-1" style="max-height:14rem;overflow:auto"><code>@grantCommand</code></pre>
                                            <div class="d-flex flex-wrap gap-2">
                                                <button type="button" class="btn btn-sm btn-outline-secondary"
                                                        @onclick="CopyGrantCommand">
                                                    <span class="bi-clipboard me-1"></span> Copy command
                                                </button>
                                                <a class="btn btn-sm btn-outline-primary" target="_blank" rel="noopener"
                                                   href="https://console.aws.amazon.com/cloudshell/home">
                                                    <span class="bi-terminal me-1"></span> Open CloudShell
                                                    <span class="bi-box-arrow-up-right ms-1 small"></span>
                                                </a>
                                                <span class="align-self-center small @(grantCopySucceeded ? "text-success" : "text-danger")">
                                                    @grantCopyMessage
                                                </span>
                                            </div>
                                        }
                                    </div>
                                }
```

The `RegionMismatch` branch above it and the `else` branch below it (choose a group) are unchanged.

- [ ] **Step 5: Build and run the app to verify manually**

Run: `dotnet build src/Connapse.Web`
Expected: SUCCEEDS.

Manual check (needs a configured Identity Center + grant group + an S3 connection with an ungranted bucket, per CLAUDE.md's run instructions):
1. Open the connection; confirm the ungranted-bucket warning shows a **Grant access** button (not a CloudShell command).
2. Click it. With the policy from Task 3 applied to Connapse's role, the warning clears and the success line appears.
3. Re-open the connection; the button is gone (coverage satisfied) — proving idempotency end to end.
4. To see the fallback, point Connapse at a role lacking `s3:CreateAccessGrant`: the button yields the AccessDenied message and reveals the CloudShell script.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Web/Components/Pages/Connections.razor
git commit -m "feat: grant S3 access from the connection instead of a CloudShell script"
```

---

## Self-Review

**Spec coverage:**
- §1 grant writer (interface + impl + DI) → Tasks 1 (planner) + 2 (writer/DI). ✅
- §2 policy + summary → Task 3. The spec's preflight required-actions extension is **not** implemented: that green/yellow/red action-probe does not exist on this branch (the current `ProviderSetupReader.Access()` is a binary can-read-S3 check; the probe belongs to the unmerged Roles Anywhere UI PR). Forward-note: when that PR lands, add `s3:CreateAccessGrant` to its declared required-actions set so a role missing it shows yellow. Deliberately out of scope here. ✅ (scope narrowed vs. spec — noted)
- §3 UI button + graceful degradation, script kept → Task 4. ✅
- §4 safety invariants (READ only, idempotent, fail-closed, region) → enforced in Tasks 1–2 and asserted in Task 1 tests; carried in Global Constraints. ✅
- §5 testing → planner unit tests (Task 1), policy unit tests (Task 3); writer AWS-I/O and UI verified manually/live per codebase precedent (no bUnit harness, reader has no client-boundary unit test). ✅

**Placeholder scan:** No TBD/TODO; all code blocks concrete. The two "confirm the member/method name against the file/SDK" notes (Task 2 Step 4, Task 4 Step 3) are guardrails against version/name drift, not deferred work.

**Type consistency:** `GrantPlanner.Plan → GrantPlan{ToCreate, AlreadyGranted}` (Task 1) consumed in Task 2's `CreateAsync`. `GrantWriteResult{Created, AlreadyGranted, Failed, AccessDenied}` + `Succeeded` (Task 2) consumed in Task 4's `GrantAccess`. `AccessGrantee(IsGroup, Id)` matches the existing record. Subprefix shape `bucket/prefix/*` is produced once (planner) and passed unchanged as `S3SubPrefix`.

**Deviation from spec:** the preflight extension (spec §2, second bullet) is dropped as not-yet-applicable and recorded as a forward-note above.
