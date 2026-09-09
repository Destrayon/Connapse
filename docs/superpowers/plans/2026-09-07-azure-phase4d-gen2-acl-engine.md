# Azure Phase 4d — Gen2 POSIX ACL engine + ancestor-traverse resolver Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the pure, unit-testable Gen2 (ADLS HNS) permission engine — a POSIX first-match ACL evaluator and a lazily-cached ancestor-traverse resolver — that Phase 4e's live per-hit verifier will consume.

**Architecture:** Two pure pieces plus a thin SDK adapter. `PosixAclEvaluator` (Core, static) decides read/traverse for an identity set against one directory's ACL using the documented POSIX first-match rules. `AncestorTraverseResolver` (Storage) confirms the identity set holds traverse-`X` on **every** ancestor directory of a candidate file, resolving lazily and caching per directory behind an `IGen2DirectoryReader` seam. `DataLakeGen2DirectoryReader` (Storage) is the live seam implementation over `Azure.Storage.Files.DataLake`, with all mapping logic in `internal static` methods so the SDK-touching shell stays trivial. **Nothing here is wired into search** — that is Phase 4e.

**Tech Stack:** .NET 10, C# (file-scoped namespaces, records, primary constructors, nullable enabled), `Azure.Storage.Files.DataLake`, `Microsoft.Extensions.Caching.Memory`; xUnit + FluentAssertions + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-09-06-azure-phase4-permission-engine-design.md` (§D; governing principle and §E give the consumer's contract)

## Global Constraints

- **Live only.** No ingestion-time permission capture; no new persistence surface. Freshness = cache TTL.
- **Fail closed everywhere.** Any unreadable directory, fetch error, or uncertain answer → deny (no traverse), never a grant.
- **Reuse the provider-agnostic enforcement half.** 4d adds a library only; it does not touch `ISearchScopeResolver`, `SearchScopes`, `PgVectorStore`, `KeywordSearchService`, or `HybridSearchService`.
- **AWS behavior unchanged.** No file under `RolesAnywhere/`, `AwsSearchScopeResolver.cs`, `S3*`, or the SAML/JWT path is touched.
- **App identity is `Storage Blob Data Reader`** — read-only. Connapse reads ACL data to evaluate the *user's* access; it never writes a grant, role, or ACL.
- **Folder access is never trusted as file access.** ADLS ACLs are copy-at-create, not live-inherited. 4d resolves traverse-`X` on ancestor **directories** only; it never decides a file's own read (that is 4e's file-ACL check), never skip-verifies a file from a readable folder, and never excludes a file because of a folder.
- **Cache only confident answers.** A definitive ACL decision (grant/deny) is cacheable ~60 s; an unreadable/uncertain directory is never cached (fail closed and retried).
- **Cert-based auth via `ConnapseAzureCredentials`** (a `TokenCredential`); no client secrets. The DataLake client is built from the account URI + that credential.

---

## File Structure

**Create (Core — `src/Connapse.Core/`):**
- `Models/Gen2Acl.cs` — `Gen2Permission` (flags), `Gen2NamedAce`, `Gen2Acl`, `Gen2ModeBits` (+ `ToAcl()`), `Gen2Path`, and the `Gen2Permissions` symbolic-string parser. Pure data + parsing, no I/O.
- `CloudScope/PosixAclEvaluator.cs` — the static first-match evaluator.
- `Interfaces/IGen2DirectoryReader.cs` — the directory-ACL read seam.

**Create (Storage — `src/Connapse.Storage/`):**
- `CloudScope/AncestorTraverseResolver.cs` — the cached ancestor-traverse walk.
- `CloudScope/DataLakeGen2DirectoryReader.cs` — the live `IGen2DirectoryReader` over the DataLake SDK.

**Modify:**
- `src/Connapse.Storage/Connapse.Storage.csproj` — add the `Azure.Storage.Files.DataLake` package.
- `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — register `IGen2DirectoryReader` and `AncestorTraverseResolver` (consumed by 4e; not wired to search).

**Create (tests):**
- `tests/Connapse.Core.Tests/Models/Gen2AclTests.cs`
- `tests/Connapse.Core.Tests/CloudScope/PosixAclEvaluatorTests.cs`
- `tests/Connapse.Storage.Tests/CloudScope/AncestorTraverseResolverTests.cs`
- `tests/Connapse.Storage.Tests/CloudScope/DataLakeGen2DirectoryReaderMappingTests.cs`

`Connapse.Core.Tests` and `Connapse.Storage.Tests` already have `InternalsVisibleTo`, so `internal static` mappers are directly testable.

---

### Task 1: Gen2 ACL types + directory-reader seam (Core)

Pure type declarations, a symbolic-permission parser, and the read seam. No logic beyond parsing.

**Files:**
- Create: `src/Connapse.Core/Models/Gen2Acl.cs`
- Create: `src/Connapse.Core/Interfaces/IGen2DirectoryReader.cs`
- Test: `tests/Connapse.Core.Tests/Models/Gen2AclTests.cs`

**Interfaces:**
- Produces:
  - `enum Gen2Permission { None=0, Execute=1, Write=2, Read=4 }` (`[Flags]`).
  - `record Gen2NamedAce(string Oid, Gen2Permission Permissions)`.
  - `record Gen2Acl(string? OwnerOid, string? OwningGroupOid, Gen2Permission OwnerPermissions, Gen2Permission OwningGroupPermissions, Gen2Permission OtherPermissions, Gen2Permission? Mask, IReadOnlyList<Gen2NamedAce> NamedUsers, IReadOnlyList<Gen2NamedAce> NamedGroups)`.
  - `record Gen2ModeBits(string? OwnerOid, string? OwningGroupOid, Gen2Permission Owner, Gen2Permission OwningGroup, Gen2Permission Other, bool HasExtendedAcl)` with `Gen2Acl ToAcl()`.
  - `record Gen2Path(string Account, string FileSystem, string Path)` — `Path` is the blob path relative to the filesystem, no leading slash; the filesystem root is `Path == ""`.
  - `static class Gen2Permissions { static Gen2Permission FromRwx(char r, char w, char x); static (Gen2Permission Owner, Gen2Permission Group, Gen2Permission Other, bool Extended) ParseSymbolic(string symbolic); }`.
  - `interface IGen2DirectoryReader` (see step 6).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Core.Tests/Models/Gen2AclTests.cs
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

[Trait("Category", "Unit")]
public class Gen2AclTests
{
    [Theory]
    [InlineData('r', '-', 'x', Gen2Permission.Read | Gen2Permission.Execute)]
    [InlineData('r', 'w', 'x', Gen2Permission.Read | Gen2Permission.Write | Gen2Permission.Execute)]
    [InlineData('-', '-', '-', Gen2Permission.None)]
    [InlineData('-', '-', 'x', Gen2Permission.Execute)]
    public void FromRwx_MapsEachBit(char r, char w, char x, Gen2Permission expected) =>
        Gen2Permissions.FromRwx(r, w, x).Should().Be(expected);

    [Fact]
    public void ParseSymbolic_NineChars_SplitsOwnerGroupOther()
    {
        var (owner, group, other, extended) = Gen2Permissions.ParseSymbolic("rwxr-x---");
        owner.Should().Be(Gen2Permission.Read | Gen2Permission.Write | Gen2Permission.Execute);
        group.Should().Be(Gen2Permission.Read | Gen2Permission.Execute);
        other.Should().Be(Gen2Permission.None);
        extended.Should().BeFalse();
    }

    [Fact]
    public void ParseSymbolic_TrailingPlus_MarksExtendedAcl()
    {
        var (_, _, _, extended) = Gen2Permissions.ParseSymbolic("rwxr-x---+");
        extended.Should().BeTrue();
    }

    [Fact]
    public void ParseSymbolic_TenCharLeadingType_IgnoresTypeChar()
    {
        // Some listings prefix a file-type char (e.g. 'd' for directory): "drwxr-x---".
        var (owner, _, _, _) = Gen2Permissions.ParseSymbolic("drwxr-x---");
        owner.Should().Be(Gen2Permission.Read | Gen2Permission.Write | Gen2Permission.Execute);
    }

    [Fact]
    public void ModeBits_ToAcl_CarriesOwnerGroupOther_NoNamedNoMask()
    {
        var bits = new Gen2ModeBits("owner-oid", "group-oid",
            Gen2Permission.Read | Gen2Permission.Execute, Gen2Permission.Execute, Gen2Permission.None,
            HasExtendedAcl: false);

        Gen2Acl acl = bits.ToAcl();

        acl.OwnerOid.Should().Be("owner-oid");
        acl.OwningGroupOid.Should().Be("group-oid");
        acl.OwnerPermissions.Should().Be(Gen2Permission.Read | Gen2Permission.Execute);
        acl.OwningGroupPermissions.Should().Be(Gen2Permission.Execute);
        acl.OtherPermissions.Should().Be(Gen2Permission.None);
        acl.Mask.Should().BeNull();
        acl.NamedUsers.Should().BeEmpty();
        acl.NamedGroups.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~Gen2AclTests"`
Expected: FAIL — `Gen2Permission`, `Gen2Permissions`, `Gen2ModeBits` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Connapse.Core/Models/Gen2Acl.cs
namespace Connapse.Core;

/// <summary>POSIX permission bits, as flags so a permission set is one value.</summary>
[Flags]
public enum Gen2Permission
{
    None = 0,
    Execute = 1,
    Write = 2,
    Read = 4,
}

/// <summary>A named ADLS ACL entry: a user or group principal object id and its permissions.</summary>
public record Gen2NamedAce(string Oid, Gen2Permission Permissions);

/// <summary>
/// One directory's <b>access</b> ACL (never the default/inheritance ACL), as the POSIX evaluator
/// needs it: the owner and owning-group principals and their permissions, the mask (present only
/// when the ACL is extended), the named user/group entries, and the "other" permissions.
/// </summary>
public record Gen2Acl(
    string? OwnerOid,
    string? OwningGroupOid,
    Gen2Permission OwnerPermissions,
    Gen2Permission OwningGroupPermissions,
    Gen2Permission OtherPermissions,
    Gen2Permission? Mask,
    IReadOnlyList<Gen2NamedAce> NamedUsers,
    IReadOnlyList<Gen2NamedAce> NamedGroups);

/// <summary>
/// The cheap "mode bits" view of a directory — owner/group principals plus the owner/group/other
/// rwx triples — from a listing (<c>GetPaths</c>) or properties read. <see cref="HasExtendedAcl"/>
/// is the POSIX '+' marker: when false there are no named entries and no mask, so these bits alone
/// decide access; when true a named entry could be decisive and the full ACL must be read.
/// </summary>
public record Gen2ModeBits(
    string? OwnerOid,
    string? OwningGroupOid,
    Gen2Permission Owner,
    Gen2Permission OwningGroup,
    Gen2Permission Other,
    bool HasExtendedAcl)
{
    /// <summary>The equivalent <see cref="Gen2Acl"/> with no named entries and no mask. Correct to
    /// evaluate against only when the requester owns the directory or when there is no extended ACL;
    /// the resolver enforces that precondition.</summary>
    public Gen2Acl ToAcl() =>
        new(OwnerOid, OwningGroupOid, Owner, OwningGroup, Other, Mask: null, NamedUsers: [], NamedGroups: []);
}

/// <summary>An ADLS path: the storage account, filesystem (container), and the path within it
/// (no leading slash; the filesystem root is the empty string).</summary>
public record Gen2Path(string Account, string FileSystem, string Path);

/// <summary>Parses ADLS symbolic permission strings into <see cref="Gen2Permission"/> triples.</summary>
public static class Gen2Permissions
{
    /// <summary>Maps one rwx triple (e.g. 'r','-','x') to a permission set.</summary>
    public static Gen2Permission FromRwx(char r, char w, char x)
    {
        Gen2Permission p = Gen2Permission.None;
        if (r == 'r') p |= Gen2Permission.Read;
        if (w == 'w') p |= Gen2Permission.Write;
        if (x is 'x' or 's' or 't') p |= Gen2Permission.Execute; // setuid/sticky imply the x slot
        return p;
    }

    /// <summary>
    /// Parses a symbolic permission string into owner/group/other triples plus the extended-ACL
    /// flag. Accepts a 9-char body ("rwxr-x---"), an optional trailing '+' (extended ACL), and an
    /// optional leading file-type char ("drwx...").
    /// </summary>
    public static (Gen2Permission Owner, Gen2Permission Group, Gen2Permission Other, bool Extended) ParseSymbolic(string symbolic)
    {
        ArgumentNullException.ThrowIfNull(symbolic);
        bool extended = symbolic.EndsWith('+');
        string s = extended ? symbolic[..^1] : symbolic;
        if (s.Length == 10) s = s[1..];           // drop a leading file-type char
        if (s.Length != 9)
            throw new FormatException($"Unexpected ADLS permission string '{symbolic}'.");
        return (
            FromRwx(s[0], s[1], s[2]),
            FromRwx(s[3], s[4], s[5]),
            FromRwx(s[6], s[7], s[8]),
            extended);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~Gen2AclTests"`
Expected: PASS (all cases).

- [ ] **Step 5: Add the reader seam**

```csharp
// src/Connapse.Core/Interfaces/IGen2DirectoryReader.cs
namespace Connapse.Core.Interfaces;

/// <summary>
/// Reads a single ADLS Gen2 directory's <b>access</b> ACL, with Connapse's own (Data Reader)
/// identity, to evaluate a searcher's traverse rights. Two calls so the resolver can short-circuit:
/// the cheap mode-bits read decides on its own whenever the requester owns the directory or the ACL
/// is not extended; only otherwise is the full ACL fetched. Both return <c>null</c> when the
/// directory cannot be read — an uncertain answer the caller treats as fail-closed.
/// </summary>
public interface IGen2DirectoryReader
{
    Task<Gen2ModeBits?> ReadModeBitsAsync(Gen2Path directory, CancellationToken ct = default);
    Task<Gen2Acl?> ReadAccessAclAsync(Gen2Path directory, CancellationToken ct = default);
}
```

(`Gen2ModeBits`, `Gen2Acl`, `Gen2Path` live in namespace `Connapse.Core`; add the `using Connapse.Core;` the interface needs.)

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Core/Models/Gen2Acl.cs src/Connapse.Core/Interfaces/IGen2DirectoryReader.cs tests/Connapse.Core.Tests/Models/Gen2AclTests.cs
git commit -m "feat(azure): Gen2 ACL model + directory-reader seam (#489)"
```

---

### Task 2: POSIX first-match evaluator (Core)

The pure decision. This is the correctness heart of 4d — the matrix test is exhaustive.

**Files:**
- Create: `src/Connapse.Core/CloudScope/PosixAclEvaluator.cs`
- Test: `tests/Connapse.Core.Tests/CloudScope/PosixAclEvaluatorTests.cs`

**Interfaces:**
- Consumes: `Gen2Acl`, `Gen2NamedAce`, `Gen2Permission` (Task 1).
- Produces: `static bool PosixAclEvaluator.Grants(Gen2Acl acl, IReadOnlySet<string> principals, Gen2Permission requested)`.

**Algorithm (documented POSIX first-match; class precedence owner > named-user > group-union > other):**
1. **Owner** — if any principal is the owner, the owner permissions decide; the mask does **not** apply. Terminal.
2. **Named user** — else if any principal matches a named-user entry, that entry (capped by the mask) decides. Terminal even if it denies.
3. **Group class** — else gather the owning group (if a principal is in it) and every named-group entry a principal matches; if **any** of them, masked, grants the permission, allow; if there is at least one group match but none grants, deny (do not fall through).
4. **Other** — else the "other" permissions decide.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Core.Tests/CloudScope/PosixAclEvaluatorTests.cs
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class PosixAclEvaluatorTests
{
    private const Gen2Permission RX = Gen2Permission.Read | Gen2Permission.Execute;

    private static Gen2Acl Acl(
        string? owner = "owner", string? group = "group",
        Gen2Permission ownerPerms = RX, Gen2Permission groupPerms = RX, Gen2Permission other = Gen2Permission.None,
        Gen2Permission? mask = null,
        IReadOnlyList<Gen2NamedAce>? namedUsers = null, IReadOnlyList<Gen2NamedAce>? namedGroups = null) =>
        new(owner, group, ownerPerms, groupPerms, other, mask, namedUsers ?? [], namedGroups ?? []);

    private static IReadOnlySet<string> P(params string[] oids) => new HashSet<string>(oids);

    [Fact]
    public void Owner_Decides_AndIgnoresMask()
    {
        // Owner has X; a restrictive mask must NOT cut the owner down.
        var acl = Acl(ownerPerms: RX, mask: Gen2Permission.None);
        PosixAclEvaluator.Grants(acl, P("owner"), Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void Owner_WithoutTheBit_IsDenied_NotFallingThroughToGroup()
    {
        // Requester owns the dir (owner has no X) AND is in the owning group (group has X). Owner is
        // terminal: no X for the owner means denied, group never consulted.
        var acl = Acl(ownerPerms: Gen2Permission.Read, groupPerms: RX);
        PosixAclEvaluator.Grants(acl, P("owner", "group"), Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void NamedUser_Matched_IsCappedByMask()
    {
        var acl = Acl(other: RX,
            mask: Gen2Permission.Read, // mask strips X from named entries
            namedUsers: [new Gen2NamedAce("alice", RX)]);
        // Named-user match is terminal and masked → no X, even though "other" would have granted it.
        PosixAclEvaluator.Grants(acl, P("alice"), Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void NamedUser_Matched_Grants_WhenMaskAllows()
    {
        var acl = Acl(mask: RX, namedUsers: [new Gen2NamedAce("alice", RX)]);
        PosixAclEvaluator.Grants(acl, P("alice"), Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void OwningGroup_Grants_WhenMaskAllows()
    {
        var acl = Acl(owner: "someone-else", groupPerms: RX, mask: RX);
        PosixAclEvaluator.Grants(acl, P("group"), Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void AnyNamedGroup_Granting_Suffices()
    {
        // Two group matches: one denies X, one grants it. Any one granting → allow.
        var acl = Acl(owner: "someone-else", group: "not-mine",
            mask: RX,
            namedGroups: [new Gen2NamedAce("g1", Gen2Permission.Read), new Gen2NamedAce("g2", RX)]);
        PosixAclEvaluator.Grants(acl, P("g1", "g2"), Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void GroupMatch_ButNoneGrant_IsDenied_NotFallingThroughToOther()
    {
        var acl = Acl(owner: "someone-else", group: "not-mine", other: RX,
            mask: RX, namedGroups: [new Gen2NamedAce("g1", Gen2Permission.Read)]);
        PosixAclEvaluator.Grants(acl, P("g1"), Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void GroupClass_CappedByMask()
    {
        var acl = Acl(owner: "someone-else", group: "g", groupPerms: RX, mask: Gen2Permission.Read);
        PosixAclEvaluator.Grants(acl, P("g"), Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void Other_Decides_WhenNoOwnerNamedOrGroupMatch()
    {
        var acl = Acl(owner: "someone-else", group: "not-mine", other: RX);
        PosixAclEvaluator.Grants(acl, P("stranger"), Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void Other_WithoutTheBit_IsDenied()
    {
        var acl = Acl(owner: "someone-else", group: "not-mine", other: Gen2Permission.Read);
        PosixAclEvaluator.Grants(acl, P("stranger"), Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void Owner_TakesPrecedence_OverANamedUserEntryForTheSamePrincipal()
    {
        // Same principal is both owner and a named user; owner class wins (terminal, unmasked).
        var acl = Acl(ownerPerms: RX, mask: Gen2Permission.None,
            namedUsers: [new Gen2NamedAce("owner", Gen2Permission.None)]);
        PosixAclEvaluator.Grants(acl, P("owner"), Gen2Permission.Execute).Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~PosixAclEvaluatorTests"`
Expected: FAIL — `PosixAclEvaluator` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Connapse.Core/CloudScope/PosixAclEvaluator.cs
namespace Connapse.Core;

/// <summary>
/// The documented POSIX ACL first-match check, as ADLS Gen2 applies it: class precedence
/// owner &gt; named-user &gt; group-union &gt; other. The mask caps named users, the owning group,
/// and named groups — never the owner or "other". A matched owner or named-user class is terminal
/// (even when it denies); in the group class, any one matching entry that (masked) grants suffices.
/// Pure and side-effect-free so it is exhaustively unit-testable.
/// </summary>
public static class PosixAclEvaluator
{
    public static bool Grants(Gen2Acl acl, IReadOnlySet<string> principals, Gen2Permission requested)
    {
        ArgumentNullException.ThrowIfNull(acl);
        ArgumentNullException.ThrowIfNull(principals);

        // 1. Owner class — terminal, mask does not apply.
        if (acl.OwnerOid is not null && principals.Contains(acl.OwnerOid))
            return Has(acl.OwnerPermissions, requested);

        // 2. Named-user class — terminal if matched, capped by the mask.
        foreach (Gen2NamedAce entry in acl.NamedUsers)
        {
            if (principals.Contains(entry.Oid))
                return Has(Capped(entry.Permissions, acl.Mask), requested);
        }

        // 3. Group class — owning group ∪ named groups. Any one that (masked) grants suffices; a
        //    match that grants nothing denies rather than falling through to "other".
        bool anyGroupMatch = false;
        if (acl.OwningGroupOid is not null && principals.Contains(acl.OwningGroupOid))
        {
            anyGroupMatch = true;
            if (Has(Capped(acl.OwningGroupPermissions, acl.Mask), requested))
                return true;
        }
        foreach (Gen2NamedAce entry in acl.NamedGroups)
        {
            if (!principals.Contains(entry.Oid)) continue;
            anyGroupMatch = true;
            if (Has(Capped(entry.Permissions, acl.Mask), requested))
                return true;
        }
        if (anyGroupMatch)
            return false;

        // 4. Other class.
        return Has(acl.OtherPermissions, requested);
    }

    private static Gen2Permission Capped(Gen2Permission perms, Gen2Permission? mask) =>
        mask is null ? perms : perms & mask.Value;

    private static bool Has(Gen2Permission perms, Gen2Permission requested) =>
        (perms & requested) == requested;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~PosixAclEvaluatorTests"`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/CloudScope/PosixAclEvaluator.cs tests/Connapse.Core.Tests/CloudScope/PosixAclEvaluatorTests.cs
git commit -m "feat(azure): POSIX first-match ACL evaluator (#489)"
```

---

### Task 3: Ancestor-traverse resolver (Storage)

Walks a candidate file's ancestor directories, confirms traverse-`X` on **every** one, resolves lazily with the mode-bits short-circuit, and caches confident per-directory decisions. Fails closed.

**Files:**
- Create: `src/Connapse.Storage/CloudScope/AncestorTraverseResolver.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/AncestorTraverseResolverTests.cs`

**Interfaces:**
- Consumes: `IGen2DirectoryReader` (Task 1), `PosixAclEvaluator.Grants` (Task 2), `Gen2Path`, `Gen2ModeBits`, `Gen2Acl`, `Gen2Permission` (Task 1), `Microsoft.Extensions.Caching.Memory.IMemoryCache`.
- Produces: `Task<bool> AncestorTraverseResolver.HoldsTraverseOnAllAncestorsAsync(Gen2Path file, IReadOnlySet<string> principals, CancellationToken ct = default)`.

**Ancestor set:** the filesystem root plus every directory prefix of the file, excluding the file itself. For `dir1/dir2/file.txt` → `""` (root), `"dir1"`, `"dir1/dir2"`. For a root-level `file.txt` → just `""`.

**Short-circuit + fail-closed per directory:** read mode bits; if the requester owns the directory **or** the ACL is not extended, decide from the mode bits alone; otherwise read the full ACL. Either read returning `null` is uncertain → the whole traverse is denied (and nothing is cached for that directory). Any ancestor lacking `X` denies immediately.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/AncestorTraverseResolverTests.cs
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AncestorTraverseResolverTests
{
    private const Gen2Permission RX = Gen2Permission.Read | Gen2Permission.Execute;
    private static readonly IReadOnlySet<string> Me = new HashSet<string> { "me" };

    private static Gen2Path File(string path) => new("acct", "fs", path);

    // Mode bits where "other" carries X (so anyone traverses), no extended ACL.
    private static Gen2ModeBits OpenDir() =>
        new("owner", "group", RX, RX, RX, HasExtendedAcl: false);

    // Mode bits where nobody but owner/group traverses (other has no X), no extended ACL.
    private static Gen2ModeBits ClosedDir() =>
        new("owner", "group", RX, RX, Gen2Permission.None, HasExtendedAcl: false);

    private static AncestorTraverseResolver Build(IGen2DirectoryReader reader) =>
        new(reader, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task AllAncestorsGrantX_IsReadable()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns(OpenDir());

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("a/b/file.txt"), Me);

        ok.Should().BeTrue();
        // root, "a", "a/b" — three ancestor directories, file excluded.
        await reader.Received(3).ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LosingXOnOneAncestor_IsNotReadable()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Is<Gen2Path>(p => p.Path == "a"), Arg.Any<CancellationToken>()).Returns(ClosedDir());
        reader.ReadModeBitsAsync(Arg.Is<Gen2Path>(p => p.Path != "a"), Arg.Any<CancellationToken>()).Returns(OpenDir());

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("a/b/file.txt"), Me);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ExtendedAcl_AndNotOwner_ReadsFullAcl_ForTheTieBreak()
    {
        // Mode bits say "other" has no X and the ACL is extended → a named entry could grant X, so
        // the full ACL is consulted; a named group grants X.
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, Gen2Permission.None, HasExtendedAcl: true));
        reader.ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2Acl("owner", "group", RX, Gen2Permission.None, Gen2Permission.None,
                Mask: RX, NamedUsers: [], NamedGroups: [new Gen2NamedAce("me", RX)]));

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me);

        ok.Should().BeTrue();
        await reader.Received(1).ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Owner_ShortCircuits_WithoutReadingFullAcl_EvenWhenExtended()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("me", "group", RX, Gen2Permission.None, Gen2Permission.None, HasExtendedAcl: true));

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me);

        ok.Should().BeTrue();
        await reader.DidNotReceive().ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnreadableAncestor_FailsClosed_AndIsNotCached()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns((Gen2ModeBits?)null);

        var resolver = Build(reader);
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me)).Should().BeFalse();
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me)).Should().BeFalse();

        // Not cached → read attempted again on the second call (root only, one dir).
        await reader.Received(2).ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfidentDecision_IsCached_SecondCallDoesNotReRead()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns(OpenDir());

        var resolver = Build(reader);
        await resolver.HoldsTraverseOnAllAncestorsAsync(File("a/file.txt"), Me); // root + "a" = 2 reads
        await resolver.HoldsTraverseOnAllAncestorsAsync(File("a/file.txt"), Me); // served from cache

        await reader.Received(2).ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentPrincipalSet_IsNotServedFromAnotherSetsCache()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        // "other" has no X and not extended → decision depends purely on group membership.
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, Gen2Permission.None, HasExtendedAcl: false));

        var resolver = Build(reader);
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), new HashSet<string> { "group" })).Should().BeTrue();
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), new HashSet<string> { "stranger" })).Should().BeFalse();
    }

    [Fact]
    public async Task CallerCancellation_IsRethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns<Gen2ModeBits?>(_ => throw new OperationCanceledException());

        Func<Task> act = () => Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AncestorTraverseResolverTests"`
Expected: FAIL — `AncestorTraverseResolver` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Connapse.Storage/CloudScope/AncestorTraverseResolver.cs
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Decides whether an identity set holds traverse-<c>X</c> on <b>every</b> ancestor directory of a
/// candidate Gen2 file — the necessary condition (with the file's own read, checked by Phase 4e's
/// verifier) for the file to be readable. Resolves lazily per directory with the mode-bits
/// short-circuit and caches confident per-directory decisions, so candidates that share ancestors
/// share the work. Fails closed: any directory that cannot be read denies the whole traverse and is
/// never cached.
/// </summary>
public sealed class AncestorTraverseResolver(IGen2DirectoryReader reader, IMemoryCache cache)
{
    /// <summary>Cache window for a confident per-directory traverse decision; also its revocation
    /// delay. Kept short (the spec's ~30–120 s) so a changed ACL takes effect quickly.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    private const string KeyPrefix = "gen2-traverse:";

    public async Task<bool> HoldsTraverseOnAllAncestorsAsync(
        Gen2Path file, IReadOnlySet<string> principals, CancellationToken ct = default)
    {
        string principalKey = string.Join(",", principals.OrderBy(p => p, StringComparer.Ordinal));

        foreach (Gen2Path dir in Ancestors(file))
        {
            string key = $"{KeyPrefix}{dir.Account}:{dir.FileSystem}:{dir.Path}:{principalKey}";
            if (cache.TryGetValue(key, out bool cached))
            {
                if (!cached) return false; // a cached definitive deny short-circuits the whole walk
                continue;
            }

            bool? decision = await ResolveDirectoryExecuteAsync(dir, principals, ct);
            if (decision is null)
                return false; // uncertain → fail closed, uncached (retried next time)

            cache.Set(key, decision.Value, CacheLifetime);
            if (!decision.Value)
                return false;
        }
        return true;
    }

    /// <summary>Whether <paramref name="principals"/> hold traverse-<c>X</c> on one directory.
    /// <c>null</c> means the directory could not be read (uncertain → the caller denies).</summary>
    private async Task<bool?> ResolveDirectoryExecuteAsync(
        Gen2Path dir, IReadOnlySet<string> principals, CancellationToken ct)
    {
        Gen2ModeBits? bits = await reader.ReadModeBitsAsync(dir, ct);
        if (bits is null)
            return null;

        // Mode bits alone are authoritative when the requester owns the directory (owner is terminal)
        // or the ACL is not extended (no named entries, no mask). Otherwise a named entry could be
        // decisive, so the full access ACL is read.
        bool ownsDir = bits.OwnerOid is not null && principals.Contains(bits.OwnerOid);
        if (ownsDir || !bits.HasExtendedAcl)
            return PosixAclEvaluator.Grants(bits.ToAcl(), principals, Gen2Permission.Execute);

        Gen2Acl? full = await reader.ReadAccessAclAsync(dir, ct);
        if (full is null)
            return null;
        return PosixAclEvaluator.Grants(full, principals, Gen2Permission.Execute);
    }

    /// <summary>The filesystem root and every directory prefix of the file, file itself excluded.</summary>
    private static IEnumerable<Gen2Path> Ancestors(Gen2Path file)
    {
        yield return file with { Path = "" }; // filesystem root

        string[] segments = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < segments.Length; i++) // exclude the last segment (the file)
            yield return file with { Path = string.Join('/', segments[..i]) };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AncestorTraverseResolverTests"`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/AncestorTraverseResolver.cs tests/Connapse.Storage.Tests/CloudScope/AncestorTraverseResolverTests.cs
git commit -m "feat(azure): ancestor-traverse resolver with mode-bits short-circuit (#489)"
```

---

### Task 4: DataLake directory reader + package + DI (Storage)

The live `IGen2DirectoryReader` over the ADLS SDK. All translation lives in `internal static` mappers that are unit-tested with plain inputs; the client-calling shell stays trivial.

**Files:**
- Modify: `src/Connapse.Storage/Connapse.Storage.csproj` (add package)
- Create: `src/Connapse.Storage/CloudScope/DataLakeGen2DirectoryReader.cs`
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` (register reader + resolver)
- Test: `tests/Connapse.Storage.Tests/CloudScope/DataLakeGen2DirectoryReaderMappingTests.cs`

**Interfaces:**
- Consumes: `IGen2DirectoryReader`, `Gen2Acl`, `Gen2ModeBits`, `Gen2Path`, `Gen2Permission`, `Gen2Permissions` (Tasks 1–3); `Azure.Core.TokenCredential` (already registered — `ConnapseAzureCredentials` maps to a bare `TokenCredential` in DI, added in 4a); `Azure.Storage.Files.DataLake` SDK types (`DataLakeServiceClient`, `DataLakeDirectoryClient`, `PathAccessControl`, `PathAccessControlItem`, `AccessControlType`).
- Produces: `DataLakeGen2DirectoryReader : IGen2DirectoryReader`; DI registration of `IGen2DirectoryReader` and `AncestorTraverseResolver`.

- [ ] **Step 1: Add the package**

Add to `src/Connapse.Storage/Connapse.Storage.csproj` (in the `<ItemGroup>` with the other `Azure.Storage.*` references):

```xml
<PackageReference Include="Azure.Storage.Files.DataLake" Version="12.22.0" />
```

Run: `dotnet restore src/Connapse.Storage/Connapse.Storage.csproj`
Expected: restores cleanly. If the version conflicts with `Azure.Storage.Blobs` 12.24.0's transitive `Azure.Storage.Common`, bump to the newest `12.x` that restores without a downgrade warning (DataLake ships in lockstep with Blobs), and note the chosen version in the commit.

- [ ] **Step 2: Write the failing mapping test**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/DataLakeGen2DirectoryReaderMappingTests.cs
using Azure.Storage.Files.DataLake.Models;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class DataLakeGen2DirectoryReaderMappingTests
{
    private const Gen2Permission RX = Gen2Permission.Read | Gen2Permission.Execute;

    [Fact]
    public void MapModeBits_ParsesOwnerGroupOther_AndExtendedFlag()
    {
        Gen2ModeBits bits = DataLakeGen2DirectoryReader.MapModeBits("owner-oid", "group-oid", "rwxr-x---+");

        bits.OwnerOid.Should().Be("owner-oid");
        bits.OwningGroupOid.Should().Be("group-oid");
        bits.Owner.Should().Be(Gen2Permission.Read | Gen2Permission.Write | Gen2Permission.Execute);
        bits.OwningGroup.Should().Be(RX);
        bits.Other.Should().Be(Gen2Permission.None);
        bits.HasExtendedAcl.Should().BeTrue();
    }

    [Fact]
    public void MapAccessControl_SplitsEntriesByType_AccessScopeOnly()
    {
        var items = new List<PathAccessControlItem>
        {
            new(AccessControlType.User,  RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.User,  RolePermissions.Read, defaultScope: false, entityId: "alice"),
            new(AccessControlType.Group, RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.Group, RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: "devs"),
            new(AccessControlType.Mask,  RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.Other, RolePermissions.None, defaultScope: false, entityId: null),
            // A default-scope entry must be ignored (inheritance ACL, not the access ACL).
            new(AccessControlType.User,  RolePermissions.Read | RolePermissions.Write | RolePermissions.Execute, defaultScope: true, entityId: "should-be-ignored"),
        };

        Gen2Acl acl = DataLakeGen2DirectoryReader.MapAccessControl("owner-oid", "group-oid", items);

        acl.OwnerOid.Should().Be("owner-oid");
        acl.OwningGroupOid.Should().Be("group-oid");
        acl.OwnerPermissions.Should().Be(RX);
        acl.OwningGroupPermissions.Should().Be(Gen2Permission.Execute);
        acl.OtherPermissions.Should().Be(Gen2Permission.None);
        acl.Mask.Should().Be(RX);
        acl.NamedUsers.Should().ContainSingle().Which.Should().BeEquivalentTo(new Gen2NamedAce("alice", Gen2Permission.Read));
        acl.NamedGroups.Should().ContainSingle().Which.Should().BeEquivalentTo(new Gen2NamedAce("devs", RX));
    }

    [Fact]
    public void MapAccessControl_NoMaskEntry_LeavesMaskNull()
    {
        var items = new List<PathAccessControlItem>
        {
            new(AccessControlType.User,  RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.Group, RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.Other, RolePermissions.None, defaultScope: false, entityId: null),
        };

        DataLakeGen2DirectoryReader.MapAccessControl("o", "g", items).Mask.Should().BeNull();
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~DataLakeGen2DirectoryReaderMappingTests"`
Expected: FAIL — `DataLakeGen2DirectoryReader` does not exist.

- [ ] **Step 4: Write minimal implementation**

```csharp
// src/Connapse.Storage/CloudScope/DataLakeGen2DirectoryReader.cs
using Azure;
using Azure.Core;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Reads a Gen2 directory's access ACL live over the ADLS SDK, with Connapse's own
/// <see cref="TokenCredential"/> (Storage Blob Data Reader). A thin shell over the SDK: every
/// translation lives in the <c>internal static</c> mappers, which are unit-tested directly. Any SDK
/// failure returns <c>null</c> (the resolver treats that as fail-closed); genuine caller
/// cancellation propagates.
/// </summary>
public sealed class DataLakeGen2DirectoryReader(TokenCredential credential) : IGen2DirectoryReader
{
    public async Task<Gen2ModeBits?> ReadModeBitsAsync(Gen2Path directory, CancellationToken ct = default)
    {
        try
        {
            DataLakeDirectoryClient dir = DirectoryClient(directory);
            Response<PathAccessControl> ac = await dir.GetAccessControlAsync(cancellationToken: ct);
            // GetAccessControl returns the full ACL; derive the cheap mode-bits view from it. (The
            // GetPaths-listing optimization is a Phase-4e pre-warm concern; correctness does not
            // depend on it.) The extended flag is "any named entry or mask present".
            Gen2Acl acl = MapAccessControl(ac.Value.Owner, ac.Value.Group, ac.Value.AccessControlList);
            bool extended = acl.NamedUsers.Count > 0 || acl.NamedGroups.Count > 0 || acl.Mask is not null;
            return new Gen2ModeBits(acl.OwnerOid, acl.OwningGroupOid,
                acl.OwnerPermissions, acl.OwningGroupPermissions, acl.OtherPermissions, extended);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<Gen2Acl?> ReadAccessAclAsync(Gen2Path directory, CancellationToken ct = default)
    {
        try
        {
            DataLakeDirectoryClient dir = DirectoryClient(directory);
            Response<PathAccessControl> ac = await dir.GetAccessControlAsync(cancellationToken: ct);
            return MapAccessControl(ac.Value.Owner, ac.Value.Group, ac.Value.AccessControlList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private DataLakeDirectoryClient DirectoryClient(Gen2Path directory)
    {
        var service = new DataLakeServiceClient(
            new Uri($"https://{directory.Account}.dfs.core.windows.net"), credential);
        DataLakeFileSystemClient fs = service.GetFileSystemClient(directory.FileSystem);
        return directory.Path.Length == 0
            ? fs.GetDirectoryClient("/")
            : fs.GetDirectoryClient(directory.Path);
    }

    /// <summary>Parses an SDK owner/group/symbolic-permissions triple into mode bits.</summary>
    internal static Gen2ModeBits MapModeBits(string? owner, string? group, string symbolicPermissions)
    {
        var (o, g, other, extended) = Gen2Permissions.ParseSymbolic(symbolicPermissions);
        return new Gen2ModeBits(owner, group, o, g, other, extended);
    }

    /// <summary>Builds a <see cref="Gen2Acl"/> from the SDK access-control list, ignoring default
    /// (inheritance) entries and keeping only the access-scope ones.</summary>
    internal static Gen2Acl MapAccessControl(string? owner, string? group, IEnumerable<PathAccessControlItem> items)
    {
        Gen2Permission ownerPerms = Gen2Permission.None, groupPerms = Gen2Permission.None, other = Gen2Permission.None;
        Gen2Permission? mask = null;
        var namedUsers = new List<Gen2NamedAce>();
        var namedGroups = new List<Gen2NamedAce>();

        foreach (PathAccessControlItem item in items)
        {
            if (item.DefaultScope) continue; // inheritance ACL, not evaluated
            Gen2Permission perms = FromRolePermissions(item.Permissions);
            bool named = !string.IsNullOrEmpty(item.EntityId);
            switch (item.AccessControlType)
            {
                case AccessControlType.User when named: namedUsers.Add(new Gen2NamedAce(item.EntityId!, perms)); break;
                case AccessControlType.User: ownerPerms = perms; break;
                case AccessControlType.Group when named: namedGroups.Add(new Gen2NamedAce(item.EntityId!, perms)); break;
                case AccessControlType.Group: groupPerms = perms; break;
                case AccessControlType.Mask: mask = perms; break;
                case AccessControlType.Other: other = perms; break;
            }
        }

        return new Gen2Acl(owner, group, ownerPerms, groupPerms, other, mask, namedUsers, namedGroups);
    }

    private static Gen2Permission FromRolePermissions(RolePermissions p)
    {
        Gen2Permission result = Gen2Permission.None;
        if (p.HasFlag(RolePermissions.Read)) result |= Gen2Permission.Read;
        if (p.HasFlag(RolePermissions.Write)) result |= Gen2Permission.Write;
        if (p.HasFlag(RolePermissions.Execute)) result |= Gen2Permission.Execute;
        return result;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~DataLakeGen2DirectoryReaderMappingTests"`
Expected: PASS.

- [ ] **Step 6: Register in DI (consumed by 4e; not wired to search)**

In `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`, alongside the other CloudScope registrations (`AzureSearchScopeResolver`, `ArmRbacReader`, etc.), add:

```csharp
// Gen2 permission engine (Phase 4d). A pure library the Phase 4e verifier consumes; nothing here
// is wired into the search pipeline yet.
services.AddSingleton<IGen2DirectoryReader, DataLakeGen2DirectoryReader>();
services.AddSingleton<AncestorTraverseResolver>();
```

Confirm the file already has `using Connapse.Core.Interfaces;` and `using Connapse.Storage.CloudScope;` (add whichever is missing). `DataLakeGen2DirectoryReader` resolves its `TokenCredential` from the existing 4a registration; `AncestorTraverseResolver` gets `IGen2DirectoryReader` + the already-registered `IMemoryCache`.

- [ ] **Step 7: Build and run the full Storage + Core unit suites**

Run:
```bash
dotnet build -clp:ErrorsOnly
dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --no-build --filter "Category=Unit"
dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --no-build --filter "Category=Unit"
```
Expected: build clean (0 warnings); both suites green.

- [ ] **Step 8: Commit**

```bash
git add src/Connapse.Storage/Connapse.Storage.csproj src/Connapse.Storage/CloudScope/DataLakeGen2DirectoryReader.cs src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs tests/Connapse.Storage.Tests/CloudScope/DataLakeGen2DirectoryReaderMappingTests.cs
git commit -m "feat(azure): live DataLake Gen2 directory reader + DI (#489)"
```

---

## Self-Review

**1. Spec coverage (§D):**
- "POSIX first-match evaluator: owner bypasses mask; named user/group capped by mask; any one group granting suffices; owning-group; other" → Task 2 (`PosixAclEvaluator`), matrix test covers each clause including owner-ignores-mask and any-named-group-suffices.
- "Read (R) and traverse (X) decisions" → `Grants(..., Gen2Permission requested)` takes either bit; 4d exercises `Execute`, 4e will pass `Read` for the file's own ACL.
- "Ancestor-traverse resolver … resolve whether P holds X on every ancestor directory; resolve lazily and cache per directory" → Task 3.
- "mode bits via GetPaths/properties; GetAccessControl only for named-entry tie-breaks" → the `HasExtendedAcl` / owner short-circuit in Task 3, backed by mode-bits vs full-ACL reads in Task 4.
- "folder access never proves file access; never excludes and never skip-verifies from folders" → 4d only decides ancestor **traverse**; it exposes no file-read decision and no exclusion API. Documented in the resolver/interface XML docs and the Global Constraints.
- "Optional bulk pre-warm (one recursive directories-only GetPaths) — perf only, not required" → explicitly a non-goal for 4d (a 4e performance option); the reader's correctness path is per-directory. Noted here so a reviewer does not flag its absence.

**2. Placeholder scan:** No TBD/TODO; every code step has full code; every test asserts concrete values. The one version caveat (DataLake package) gives an exact value to try plus a deterministic fallback rule, not a placeholder.

**3. Type consistency:** `Gen2Permission`, `Gen2Acl`, `Gen2NamedAce`, `Gen2ModeBits` (+ `ToAcl`), `Gen2Path`, `Gen2Permissions.ParseSymbolic/FromRwx`, `IGen2DirectoryReader.ReadModeBitsAsync/ReadAccessAclAsync`, `PosixAclEvaluator.Grants`, `AncestorTraverseResolver.HoldsTraverseOnAllAncestorsAsync`, `DataLakeGen2DirectoryReader.MapModeBits/MapAccessControl` — names and signatures are identical across the task that defines each and every task that consumes it. `RolePermissions` / `PathAccessControlItem` / `AccessControlType` are the real `Azure.Storage.Files.DataLake.Models` types (verify the `PathAccessControlItem` constructor arg order on the pinned package version during Task 4; adjust the test's object construction if the SDK exposes a factory instead).

**Note for the implementer:** the `PathAccessControlItem` constructor signature can vary slightly by SDK version. If the pinned version does not expose the `(AccessControlType, RolePermissions, bool defaultScope, string entityId)` constructor used in the Task 4 test, construct the items via whatever the package provides (constructor or `DataLakeModelFactory`) — the mapper under test is unaffected.
