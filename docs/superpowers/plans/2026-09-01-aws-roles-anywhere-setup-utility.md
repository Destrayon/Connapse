# AWS Roles Anywhere Setup Utility (PR 2b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide the two pure building blocks PR 3's UI will wire together — a local self-signed keypair/cert generator, and an `AwsRolesAnywhereSetup` utility that generates the CloudShell provisioning script (cert embedded) and parses the returned ARN block.

**Architecture:** Two additive, dependency-light pieces. `RolesAnywhereKeyGenerator` (in `Connapse.Storage.CloudScope.RolesAnywhere`, near the engine and the existing X.509 handling) produces a self-signed cert + private key as PEM strings. `AwsRolesAnywhereSetup` (in `Connapse.Core.Utilities`, mirroring `AwsIamUserSetup`) is a pure string utility: `GenerateScript` emits an idempotent bash script that reuses the shared role/`ConnapseRead` policy/profile (guarded by the `CreatedBy=Connapse` tag) and creates this instance's own trust anchor from the embedded cert, printing a `----- BEGIN CONNAPSE AWS ROLE -----` ARN block; `ParseResult` reads that block back. Neither is wired to any UI or DB — that is PR 3.

**Tech Stack:** .NET 10. `RolesAnywhereKeyGenerator` uses framework `System.Security.Cryptography` (no external package). `AwsRolesAnywhereSetup` is pure strings, reusing `S3SetupPolicy.ForManagedIdentity()` / `S3SetupPolicy.AccountPlaceholder`. Tests: xUnit, FluentAssertions, all `Category=Unit` (no Docker, no AWS).

**Spec:** `docs/superpowers/specs/2026-09-01-aws-role-based-credentials-design.md` (§3 easy-setup handshake, §4 manual values; delivery step 2, setup half).

## Global Constraints

- .NET 10, file-scoped namespaces, nullable enabled, records for DTOs, no `var` for primitive types.
- **Additive & pure.** No UI, no DB, no DI wiring, no AWS SDK calls. `AwsRolesAnywhereSetup` is a pure string generator/parser like `AwsIamUserSetup`. Do NOT touch `Providers.razor`, `ProviderSetupReader`, `AwsIamUserSetup`, or `ConnapseAwsCredentials`.
- **Shared vs per-instance (from the spec's decision 4):** the **role + `ConnapseRead` policy + profile are shared** and reused-if-present (tag-guarded, describe-then-create); the **trust anchor is per-instance** (unique name derived from the cert fingerprint). The shared role's trust policy must accept *any* Connapse trust anchor in the account via `ArnLike` on `arn:aws:rolesanywhere:*:<account>:trust-anchor/*` — never a single pinned ARN.
- Reuse `S3SetupPolicy.ForManagedIdentity()` for the `ConnapseRead` policy and `S3SetupPolicy.AccountPlaceholder` (`__CONNAPSE_ACCOUNT_ID__`) for account substitution — identical to how `AwsIamUserSetup` does it.
- Generated scripts force LF (`.Replace("\r\n", "\n")`) and must survive an interactive shell: no `set -e`, no bare `exit`, balanced single quotes, no trailing `\` line-continuations (mirroring the existing `AwsIamUserSetup`/`AccessGrantsSetup` "survives interactive shell" tests).
- Tests: `[Trait("Category","Unit")]`, namespace-appropriate, naming `MethodName_Scenario_ExpectedResult`. Script/policy assertions are substring-based; `ParseResult` tests build the block from the real marker constants; region/name sanitiser tests include injection inputs.

---

## File Structure

- `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereKeyGenerator.cs` — self-signed keypair/cert → PEMs.
- `src/Connapse.Core/Utilities/AwsRolesAnywhereSetup.cs` — constants, `AwsRolesAnywhereArns` record, `ParseResult`, `GenerateScript`.
- `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereKeyGeneratorTests.cs`
- `tests/Connapse.Core.Tests/Utilities/AwsRolesAnywhereSetupTests.cs`

---

## Task 1: RolesAnywhereKeyGenerator

**Files:**
- Create: `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereKeyGenerator.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereKeyGeneratorTests.cs`

**Interfaces:**
- Produces:
  - `sealed record RolesAnywhereKeyMaterial(string CertificatePem, string PrivateKeyPem)`
  - `static RolesAnywhereKeyMaterial RolesAnywhereKeyGenerator.Generate(string? subjectCommonName = null, TimeProvider? timeProvider = null)`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereKeyGeneratorTests.cs
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Connapse.Storage.CloudScope.RolesAnywhere;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

[Trait("Category", "Unit")]
public class RolesAnywhereKeyGeneratorTests
{
    [Fact]
    public void Generate_ProducesPemPairLoadableAsACertificateWithPrivateKey()
    {
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate();

        material.CertificatePem.Should().StartWith("-----BEGIN CERTIFICATE-----");
        material.PrivateKeyPem.Should().StartWith("-----BEGIN PRIVATE KEY-----");

        using X509Certificate2 cert = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);
        cert.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void Generate_ProducesACertificateTheSignerCanSignWith()
    {
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate();
        using X509Certificate2 cert = X509Certificate2.CreateFromPem(material.CertificatePem, material.PrivateKeyPem);

        byte[] signature = RolesAnywhereSigner.SignBytes(
            cert, RolesAnywhereSigner.RsaAlgorithm, Encoding.UTF8.GetBytes("string-to-sign"));

        using RSA pub = cert.GetRSAPublicKey()!;
        pub.VerifyData(Encoding.UTF8.GetBytes("string-to-sign"), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
           .Should().BeTrue();
    }

    [Fact]
    public void Generate_SelfSignedWithGivenCommonNameAndAboutAYearValidity()
    {
        var fixedNow = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate(
            "connapse-instance-a", new FakeTimeProvider(fixedNow));

        using X509Certificate2 cert = X509Certificate2.CreateFromPem(material.CertificatePem);
        cert.Subject.Should().Contain("CN=connapse-instance-a");
        cert.Subject.Should().Be(cert.Issuer); // self-signed
        cert.NotAfter.ToUniversalTime().Should().BeCloseTo(fixedNow.AddYears(1).UtcDateTime, TimeSpan.FromDays(1));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~RolesAnywhereKeyGeneratorTests"`
Expected: FAIL — `RolesAnywhereKeyGenerator` / `RolesAnywhereKeyMaterial` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereKeyGenerator.cs
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>A locally generated Roles Anywhere keypair: the public certificate and its private key, as PEM.</summary>
public sealed record RolesAnywhereKeyMaterial(string CertificatePem, string PrivateKeyPem);

/// <summary>
/// Generates the self-signed keypair Connapse registers as its own Roles Anywhere trust anchor and signs
/// CreateSession with. The private key never leaves the host; only the certificate is uploaded to AWS.
/// </summary>
public static class RolesAnywhereKeyGenerator
{
    /// <summary>Generates an RSA-2048 self-signed certificate + private key as PEM strings.</summary>
    public static RolesAnywhereKeyMaterial Generate(
        string? subjectCommonName = null, TimeProvider? timeProvider = null)
    {
        string commonName = string.IsNullOrWhiteSpace(subjectCommonName) ? "connapse-rolesanywhere" : subjectCommonName;
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Self-signed cert used as BOTH the trust-anchor CA and the end-entity signing cert:
        // mark it a CA and allow both cert-signing and digital-signature so AWS accepts it in either role.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, critical: true));

        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        return new RolesAnywhereKeyMaterial(certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~RolesAnywhereKeyGeneratorTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereKeyGenerator.cs tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereKeyGeneratorTests.cs
git commit -m "feat: generate the local Roles Anywhere keypair"
```

---

## Task 2: AwsRolesAnywhereSetup — constants, record, ParseResult

**Files:**
- Create: `src/Connapse.Core/Utilities/AwsRolesAnywhereSetup.cs`
- Test: `tests/Connapse.Core.Tests/Utilities/AwsRolesAnywhereSetupTests.cs`

**Interfaces:**
- Produces:
  - `sealed record AwsRolesAnywhereArns(string TrustAnchorArn, string ProfileArn, string RoleArn, string Region)`
  - `const string AwsRolesAnywhereSetup.BeginMarker = "----- BEGIN CONNAPSE AWS ROLE -----"`
  - `const string AwsRolesAnywhereSetup.EndMarker = "----- END CONNAPSE AWS ROLE -----"`
  - `const string AwsRolesAnywhereSetup.NamePrefix = "connapse"`
  - `static readonly IReadOnlyList<string> AwsRolesAnywhereSetup.RequiredPermissions`
  - `static AwsRolesAnywhereArns? AwsRolesAnywhereSetup.ParseResult(string? pasted)`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Core.Tests/Utilities/AwsRolesAnywhereSetupTests.cs
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class AwsRolesAnywhereSetupTests
{
    private static string Block(string ta, string profile, string role, string region) =>
        $"""
        {AwsRolesAnywhereSetup.BeginMarker}
        trustAnchorArn={ta}
        profileArn={profile}
        roleArn={role}
        region={region}
        {AwsRolesAnywhereSetup.EndMarker}
        """;

    [Fact]
    public void ParseResult_ReadsAllFourArnsFromTheBlock()
    {
        AwsRolesAnywhereArns? result = AwsRolesAnywhereSetup.ParseResult(Block(
            "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
            "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
            "arn:aws:iam::111:role/connapse-rolesanywhere",
            "us-east-1"));

        result.Should().Be(new AwsRolesAnywhereArns(
            "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
            "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
            "arn:aws:iam::111:role/connapse-rolesanywhere",
            "us-east-1"));
    }

    [Fact]
    public void ParseResult_AnchorsOnTheLastMarkerPair()
    {
        string echoedThenReal =
            Block("arn:ta:echoed", "arn:pf:echoed", "arn:role:echoed", "us-west-2")
            + "\n"
            + Block("arn:aws:rolesanywhere:us-east-1:111:trust-anchor/real",
                    "arn:aws:rolesanywhere:us-east-1:111:profile/real",
                    "arn:aws:iam::111:role/real", "us-east-1");

        AwsRolesAnywhereSetup.ParseResult(echoedThenReal)!.TrustAnchorArn
            .Should().Be("arn:aws:rolesanywhere:us-east-1:111:trust-anchor/real");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no markers here")]
    public void ParseResult_WithoutAUsableBlock_ReturnsNull(string? pasted)
    {
        AwsRolesAnywhereSetup.ParseResult(pasted).Should().BeNull();
    }

    [Fact]
    public void ParseResult_MissingARequiredArn_ReturnsNull()
    {
        string block =
            $"{AwsRolesAnywhereSetup.BeginMarker}\ntrustAnchorArn=arn:ta\nroleArn=arn:role\nregion=us-east-1\n{AwsRolesAnywhereSetup.EndMarker}";
        AwsRolesAnywhereSetup.ParseResult(block).Should().BeNull(); // profileArn absent
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AwsRolesAnywhereSetupTests.ParseResult"`
Expected: FAIL — `AwsRolesAnywhereSetup` / `AwsRolesAnywhereArns` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Connapse.Core/Utilities/AwsRolesAnywhereSetup.cs
namespace Connapse.Core.Utilities;

/// <summary>The non-secret identifiers a completed Roles Anywhere setup returns.</summary>
public sealed record AwsRolesAnywhereArns(string TrustAnchorArn, string ProfileArn, string RoleArn, string Region);

/// <summary>
/// Generates the CloudShell script that provisions Connapse's Roles Anywhere access — reusing the shared
/// role/policy/profile and creating this instance's own trust anchor — and parses the ARN block it prints
/// back. A pure string utility, mirroring <see cref="AwsIamUserSetup"/>.
/// </summary>
public static partial class AwsRolesAnywhereSetup
{
    public const string BeginMarker = "----- BEGIN CONNAPSE AWS ROLE -----";
    public const string EndMarker = "----- END CONNAPSE AWS ROLE -----";

    /// <summary>Shared-resource name prefix. A constant, deliberately not a parameter, so every instance shares them.</summary>
    public const string NamePrefix = "connapse";

    /// <summary>The CloudShell (admin) permissions the setup script needs — not the runtime identity's.</summary>
    public static readonly IReadOnlyList<string> RequiredPermissions =
    [
        "iam:GetRole", "iam:ListRoleTags", "iam:CreateRole", "iam:PutRolePolicy",
        "rolesanywhere:ListTrustAnchors", "rolesanywhere:CreateTrustAnchor",
        "rolesanywhere:ListProfiles", "rolesanywhere:CreateProfile",
        "sts:GetCallerIdentity"
    ];

    /// <summary>
    /// Parses the ARN block the script prints. Anchors on the LAST marker pair, so pasting the whole
    /// terminal (which echoes the script) still reads the printed output rather than the source.
    /// </summary>
    public static AwsRolesAnywhereArns? ParseResult(string? pasted)
    {
        if (string.IsNullOrEmpty(pasted)) return null;

        int end = pasted.LastIndexOf(EndMarker, StringComparison.Ordinal);
        if (end < 0) return null;
        int start = pasted.LastIndexOf(BeginMarker, end, StringComparison.Ordinal);
        if (start < 0) return null;

        string inner = pasted.Substring(start + BeginMarker.Length, end - start - BeginMarker.Length);

        string? trustAnchor = null, profile = null, role = null, region = null;
        foreach (string line in inner.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq];
            string value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "trustAnchorArn": trustAnchor = value; break;
                case "profileArn": profile = value; break;
                case "roleArn": role = value; break;
                case "region": region = value; break;
            }
        }

        if (string.IsNullOrEmpty(trustAnchor) || string.IsNullOrEmpty(profile)
            || string.IsNullOrEmpty(role) || string.IsNullOrEmpty(region))
            return null;

        return new AwsRolesAnywhereArns(trustAnchor, profile, role, region);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AwsRolesAnywhereSetupTests.ParseResult"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Utilities/AwsRolesAnywhereSetup.cs tests/Connapse.Core.Tests/Utilities/AwsRolesAnywhereSetupTests.cs
git commit -m "feat: parse the Roles Anywhere setup ARN block"
```

---

## Task 3: AwsRolesAnywhereSetup.GenerateScript

**Files:**
- Modify: `src/Connapse.Core/Utilities/AwsRolesAnywhereSetup.cs`
- Test: `tests/Connapse.Core.Tests/Utilities/AwsRolesAnywhereSetupTests.cs`

**Interfaces:**
- Consumes: `S3SetupPolicy.ForManagedIdentity()`, `S3SetupPolicy.AccountPlaceholder`; the constants from Task 2.
- Produces: `static string AwsRolesAnywhereSetup.GenerateScript(string certificatePem, string? region)`.

The script: resolves the account; reuses the shared role (`connapse-rolesanywhere`, tag-guarded `CreatedBy=Connapse`, else create with an `ArnLike` trust policy accepting any Connapse trust anchor in the account); always applies the `ConnapseRead` policy; reuses the shared profile by name (else create); derives a per-instance trust-anchor name from the cert's SHA-256 fingerprint and creates it from the embedded cert (via a `jq`-built `file://` source) if absent; prints the ARN block.

- [ ] **Step 1: Write the failing test**

```csharp
// append to AwsRolesAnywhereSetupTests.cs
private const string SampleCert =
    "-----BEGIN CERTIFICATE-----\nMIIBExampleExampleExample\n-----END CERTIFICATE-----";

private static string Script() => AwsRolesAnywhereSetup.GenerateScript(SampleCert, "us-east-1");

[Fact]
public void GenerateScript_ReusesSharedRoleGuardedByTheConnapseTag()
{
    string script = Script();
    script.Should().Contain("aws iam get-role --role-name");
    script.Should().Contain("aws iam list-role-tags");
    script.Should().Contain("CreatedBy"); // reuse guard
    script.Should().Contain("aws iam create-role");
    script.Should().Contain("Key=CreatedBy,Value=Connapse"); // tags a newly created role
}

[Fact]
public void GenerateScript_SharedRoleTrustPolicyAcceptsAnyConnapseTrustAnchorInTheAccount()
{
    string script = Script();
    script.Should().Contain("rolesanywhere.amazonaws.com");
    script.Should().Contain("ArnLike");
    script.Should().Contain("arn:aws:rolesanywhere:*:__CONNAPSE_ACCOUNT_ID__:trust-anchor/*"); // not a pinned ARN
    script.Should().Contain("sts:AssumeRole");
}

[Fact]
public void GenerateScript_AppliesTheSameConnapseReadPolicyAsTheUserPath()
{
    string script = Script();
    script.Should().Contain("aws iam put-role-policy");
    script.Should().Contain("--policy-name ConnapseRead");
    script.Should().Contain(S3SetupPolicy.ForManagedIdentity().Replace("\r\n", "\n"));
    script.Should().Contain($"//{S3SetupPolicy.AccountPlaceholder}/$ACCOUNT"); // account substitution
}

[Fact]
public void GenerateScript_ReusesTheSharedProfileThenCreatesAPerInstanceTrustAnchor()
{
    string script = Script();
    script.Should().Contain("aws rolesanywhere list-profiles");
    script.Should().Contain("aws rolesanywhere create-profile");
    script.Should().Contain("aws rolesanywhere list-trust-anchors");
    script.Should().Contain("aws rolesanywhere create-trust-anchor");
    script.Should().Contain("openssl x509 -noout -fingerprint -sha256"); // per-instance name from the cert
    script.Should().Contain("CERTIFICATE_BUNDLE");
    script.Should().Contain(SampleCert.Replace("\r\n", "\n")); // the cert is embedded
}

[Fact]
public void GenerateScript_PrintsTheArnBlockWithTheFourValues_AndPinsTheRegion()
{
    string script = Script();
    script.Should().Contain(AwsRolesAnywhereSetup.BeginMarker);
    script.Should().Contain(AwsRolesAnywhereSetup.EndMarker);
    script.Should().Contain("trustAnchorArn=");
    script.Should().Contain("profileArn=");
    script.Should().Contain("roleArn=");
    script.Should().Contain("region=us-east-1");
    script.Should().NotContain("aws configure get region"); // region pinned, no fallback
}

[Fact]
public void GenerateScript_SurvivesAnInteractiveShell()
{
    string[] lines = Script().Split('\n');
    string[] code = lines.Where(l => !l.TrimStart().StartsWith('#')).ToArray();

    code.Should().NotContain(l => l.Trim() == "set -e");
    code.Should().NotContain(l => l.Trim() == "exit" || l.Trim().StartsWith("exit "));
    code.Should().NotContain(l => l.TrimEnd().EndsWith(" \\")); // no line-continuations
    string.Join('\n', code).Count(c => c == '\'').Should().Match(n => n % 2 == 0); // balanced single quotes
}

[Theory]
[InlineData("us-east-1\"; rm -rf /", "")]
[InlineData("$(id)", "")]
[InlineData("us-west-2", "us-west-2")]
public void GenerateScript_SanitisesTheRegion(string input, string expectedInBlock)
{
    string script = AwsRolesAnywhereSetup.GenerateScript(SampleCert, input);
    script.Should().Contain($"region={expectedInBlock}".TrimEnd());
    if (expectedInBlock.Length == 0)
        script.Should().NotContain("rm -rf");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AwsRolesAnywhereSetupTests.GenerateScript"`
Expected: FAIL — `GenerateScript` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// add to AwsRolesAnywhereSetup

/// <summary>
/// The CloudShell script that provisions Roles Anywhere access for this instance. The shared role,
/// ConnapseRead policy, and profile are reused if a Connapse-tagged copy already exists (so a second
/// instance does not clobber them); this instance's own trust anchor is created from the embedded cert.
/// </summary>
public static string GenerateScript(string certificatePem, string? region)
{
    string cert = certificatePem.Replace("\r\n", "\n").Trim();
    string pinnedRegion = SanitiseRegion(region);
    string policy = S3SetupPolicy.ForManagedIdentity().Replace("\r\n", "\n");
    string account = S3SetupPolicy.AccountPlaceholder;
    string role = $"{NamePrefix}-rolesanywhere";
    string profile = $"{NamePrefix}-rolesanywhere";

    string script = $$"""
        # Provisions Connapse's IAM Roles Anywhere access. Safe to re-run and to run from a
        # second Connapse instance: the role, policy, and profile are shared and reused.
        FAILED=""
        REGION="{{pinnedRegion}}"
        ROLE="{{role}}"
        PROFILE="{{profile}}"

        ACCOUNT=$(aws sts get-caller-identity --query Account --output text) || FAILED="could not resolve the AWS account"

        # --- Shared role (reuse the Connapse-tagged one, else create) ---
        ROLE_ARN=""
        if aws iam get-role --role-name "$ROLE" >/dev/null 2>&1; then
          OWNER=$(aws iam list-role-tags --role-name "$ROLE" --query "Tags[?Key=='CreatedBy'].Value" --output text)
          if [ "$OWNER" != "Connapse" ]; then FAILED="a role named $ROLE already exists and Connapse did not create it"; fi
          ROLE_ARN=$(aws iam get-role --role-name "$ROLE" --query 'Role.Arn' --output text)
        else
          TRUST='{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"rolesanywhere.amazonaws.com"},"Action":["sts:AssumeRole","sts:SetSourceIdentity"],"Condition":{"ArnLike":{"aws:SourceArn":"arn:aws:rolesanywhere:*:{{account}}:trust-anchor/*"}}}]}'
          TRUST=${TRUST//{{account}}/$ACCOUNT}
          ROLE_ARN=$(aws iam create-role --role-name "$ROLE" --assume-role-policy-document "$TRUST" --tags Key=CreatedBy,Value=Connapse --query 'Role.Arn' --output text) || FAILED="could not create the role"
        fi

        # --- ConnapseRead policy (apply/update on the shared role) ---
        if [ -z "$FAILED" ]; then
          POLICY='{{policy}}'
          POLICY=${POLICY//{{account}}/$ACCOUNT}
          aws iam put-role-policy --role-name "$ROLE" --policy-name ConnapseRead --policy-document "$POLICY" || FAILED="could not apply the ConnapseRead policy"
        fi

        # --- Shared profile (reuse by name, else create) ---
        PROFILE_ARN=""
        if [ -z "$FAILED" ]; then
          PROFILE_ARN=$(aws rolesanywhere list-profiles --query "profiles[?name=='$PROFILE'].profileArn | [0]" --output text)
          if [ "$PROFILE_ARN" = "None" ] || [ -z "$PROFILE_ARN" ]; then
            PROFILE_ARN=$(aws rolesanywhere create-profile --name "$PROFILE" --role-arns "$ROLE_ARN" --enabled --query 'profile.profileArn' --output text) || FAILED="could not create the profile"
          fi
        fi

        # --- Per-instance trust anchor from this instance's certificate ---
        TA_ARN=""
        if [ -z "$FAILED" ]; then
          CERT='{{cert}}'
          FP=$(printf '%s' "$CERT" | openssl x509 -noout -fingerprint -sha256 | sed 's/.*=//; s/://g' | cut -c1-16 | tr 'A-Z' 'a-z')
          TA_NAME="{{NamePrefix}}-ra-$FP"
          TA_ARN=$(aws rolesanywhere list-trust-anchors --query "trustAnchors[?name=='$TA_NAME'].trustAnchorArn | [0]" --output text)
          if [ "$TA_ARN" = "None" ] || [ -z "$TA_ARN" ]; then
            jq -n --arg cert "$CERT" '{sourceData:{x509CertificateData:$cert},sourceType:"CERTIFICATE_BUNDLE"}' > "$HOME/connapse-ta-source.json"
            TA_ARN=$(aws rolesanywhere create-trust-anchor --name "$TA_NAME" --source "file://$HOME/connapse-ta-source.json" --enabled --query 'trustAnchor.trustAnchorArn' --output text) || FAILED="could not create the trust anchor"
            rm -f "$HOME/connapse-ta-source.json"
          fi
        fi

        # --- Report ---
        if [ -z "$FAILED" ]; then
          printf '%s\ntrustAnchorArn=%s\nprofileArn=%s\nroleArn=%s\nregion=%s\n%s\n' "{{BeginMarker}}" "$TA_ARN" "$PROFILE_ARN" "$ROLE_ARN" "$REGION" "{{EndMarker}}"
          echo "Paste the block above back into Connapse."
        else
          echo "Setup did not complete: $FAILED"
        fi
        """;

    return script.Replace("\r\n", "\n");
}

private static string SanitiseRegion(string? region)
{
    if (string.IsNullOrWhiteSpace(region)) return string.Empty;
    string trimmed = region.Trim();
    if (trimmed.Length is 0 or > 32) return string.Empty;
    foreach (char c in trimmed)
    {
        if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) return string.Empty;
    }
    return trimmed;
}
```

Note: the `$$"""..."""` raw-interpolated block uses `{{ }}` for C# substitutions and leaves single `{`/`}` (the JSON braces, `${...}` shell expansions, `Tags[?...]` filters) literal. Verify a full solution build after implementing — a mis-escaped brace is a compile error.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AwsRolesAnywhereSetupTests"`
Expected: PASS (all ParseResult + GenerateScript tests).

- [ ] **Step 5: Run the whole unit suite and commit**

Run: `dotnet test --filter "Category=Unit"`
Expected: PASS (new files only; no regressions).

```bash
git add src/Connapse.Core/Utilities/AwsRolesAnywhereSetup.cs tests/Connapse.Core.Tests/Utilities/AwsRolesAnywhereSetupTests.cs
git commit -m "feat: generate the Roles Anywhere CloudShell setup script"
```

---

## Self-Review

**Spec coverage (delivery step 2, setup half):**
- "`AwsRolesAnywhereSetup` script generator (idempotent reuse)" → Task 3 (describe-then-create reuse of the shared role/policy/profile; per-instance trust anchor).
- "local keypair generation" → Task 1.
- "ARN parsing" → Task 2.
- Spec §3 handshake pieces this provides: the embedded-cert script (step 2) and the ARN-block parse (step 3). The keypair-generation display (step 1) and wiring `ParseResult` → `SaveRolesAnywhereAsync` are PR 3 (UI).
- Spec decision 4 (shared role/profile/policy + per-instance trust anchor) → the `ArnLike` shared-role trust policy + fingerprint-named trust anchor.

**Placeholder scan:** none — every step has runnable code and an exact test command. The AWS CLI commands are concrete (`create-trust-anchor` uses a `jq`-built `file://` source per the verified CLI syntax).

**Type consistency:** `RolesAnywhereKeyMaterial`, `AwsRolesAnywhereArns`, the marker/prefix constants, and `ParseResult`/`GenerateScript` signatures are referenced identically across tasks. `GenerateScript` consumes exactly `S3SetupPolicy.ForManagedIdentity()`/`.AccountPlaceholder` as `AwsIamUserSetup` does.

**Deferred to PR 3 (out of scope):** displaying the generated cert PEM, running `ParseResult` and calling `SaveRolesAnywhereAsync`, the adaptive Access-card UI, and the live-AWS smoke test that confirms AWS accepts the generated cert/trust-anchor (which validates the self-signed-cert-as-trust-anchor assumption and the CA/KeyUsage extensions chosen in Task 1).
