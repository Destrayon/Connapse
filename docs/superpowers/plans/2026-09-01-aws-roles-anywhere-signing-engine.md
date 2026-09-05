# AWS Roles Anywhere Signing Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone C# engine that signs an IAM Roles Anywhere `CreateSession` request (SigV4-X509) and exchanges an X.509 certificate for temporary AWS credentials — with no dependency on the external `aws_signing_helper` binary.

**Architecture:** A pure static `RolesAnywhereSigner` produces the signed request (headers + JSON body) from a certificate, request parameters, and a signing time. A thin `RolesAnywhereClient` wraps an injected `HttpClient`, POSTs the signed request to the regional endpoint, and parses the temporary credentials out of the response. Everything is deterministic given the clock, so it is fully unit-testable — the signer's asymmetric signature is verified against the certificate's own public key (the same check AWS performs), with no live AWS calls.

**Tech Stack:** .NET 10, `System.Security.Cryptography` (RSA/ECDsa/X509Certificate2), `System.Text.Json`, `System.Net.Http`. Tests: xUnit, FluentAssertions, NSubstitute — all `Category=Unit` (no Docker).

**Spec:** `docs/superpowers/specs/2026-09-01-aws-role-based-credentials-design.md` (this plan implements delivery step 1, "Signing engine").

## Global Constraints

- **.NET 10**, file-scoped namespaces, `nullable` enabled, implicit usings.
- **Records** for DTOs; **primary constructors** for DI-style classes.
- **Async all the way** — never `.Result` / `.Wait()`.
- Don't use `var` for primitive types (`string`, `int`, `byte[]`, `bool`).
- New code lives in namespace `Connapse.Storage.CloudScope.RolesAnywhere`.
- Tests tagged `[Trait("Category", "Unit")]`; naming `MethodName_Scenario_ExpectedResult`.
- **Signing facts that are load-bearing and easy to get wrong** (from the AWS spec):
  - RSA signs with **PKCS#1 v1.5** over SHA-256 (never PSS); algorithm string `AWS4-X509-RSA-SHA256`.
  - ECDSA signs over SHA-256 **DER-encoded** (`DSASignatureFormat.Rfc3279DerSequence`, never raw r‖s); algorithm string `AWS4-X509-ECDSA-SHA256`.
  - The private key signs the **StringToSign directly** — there is **no** HMAC `kDate/kRegion/kService/kSigning` derivation chain.
  - The `Credential` field carries the certificate serial number in **decimal**, not hex, replacing the access-key id.
  - `X-Amz-X509` = **leaf certificate only**, Base64 of its DER (`cert.RawData`), and it MUST appear in the signed headers.
  - Service name in the credential scope is the literal `rolesanywhere`.
  - The signed `content-type` value is exactly `application/json` — the outgoing request must send that with **no `; charset=utf-8`**, or the signature will not match.

---

## File Structure

- `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereParameters.cs` — request-parameter record.
- `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereSigner.cs` — pure signing (static helpers + `Sign`).
- `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereClient.cs` — `CreateSessionAsync`, plus `RolesAnywhereSession` and `RolesAnywhereException`.
- `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignerTests.cs`
- `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereClientTests.cs`
- `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/CertificateTestFactory.cs` — shared self-signed-cert helpers.

The engine is not registered in DI in this PR; it is wired into `ConnapseAwsCredentials` in PR 2.

---

## Task 1: Parameters record + hashing/serial helpers

**Files:**
- Create: `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereParameters.cs`
- Create: `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereSigner.cs`
- Create: `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/CertificateTestFactory.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignerTests.cs`

**Interfaces:**
- Produces:
  - `record RolesAnywhereParameters(string TrustAnchorArn, string ProfileArn, string RoleArn, string Region, int? DurationSeconds = null, string? RoleSessionName = null)`
  - `static string RolesAnywhereSigner.Sha256Hex(ReadOnlySpan<byte> data)` — lowercase hex SHA-256.
  - `static string RolesAnywhereSigner.SerialDecimal(X509Certificate2 certificate)` — decimal string of the cert serial.
  - `static class CertificateTestFactory` with `X509Certificate2 CreateRsa()` and `X509Certificate2 CreateEc()`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/CertificateTestFactory.cs
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

internal static class CertificateTestFactory
{
    public static X509Certificate2 CreateRsa()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=connapse-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    public static X509Certificate2 CreateEc()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=connapse-test", ecdsa, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
```

```csharp
// tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignerTests.cs
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Connapse.Storage.CloudScope.RolesAnywhere;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

[Trait("Category", "Unit")]
public class RolesAnywhereSignerTests
{
    [Fact]
    public void Sha256Hex_EmptyInput_MatchesAwsKnownConstant()
    {
        string hash = RolesAnywhereSigner.Sha256Hex(Array.Empty<byte>());

        hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public void SerialDecimal_IsDecimalRepresentationOfCertSerial_NotHex()
    {
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();

        string decimalSerial = RolesAnywhereSigner.SerialDecimal(cert);

        var expected = System.Numerics.BigInteger.Parse(
            "00" + cert.SerialNumber, System.Globalization.NumberStyles.HexNumber);
        decimalSerial.Should().Be(expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        decimalSerial.Should().MatchRegex("^[0-9]+$");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereSignerTests"`
Expected: FAIL — `RolesAnywhereParameters` / `RolesAnywhereSigner` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereParameters.cs
namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>
/// The identifiers a Roles Anywhere <c>CreateSession</c> call needs: which trust anchor vouches for
/// the certificate, which profile and role to assume, and where.
/// </summary>
public sealed record RolesAnywhereParameters(
    string TrustAnchorArn,
    string ProfileArn,
    string RoleArn,
    string Region,
    int? DurationSeconds = null,
    string? RoleSessionName = null);
```

```csharp
// src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereSigner.cs
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>
/// Signs an IAM Roles Anywhere <c>CreateSession</c> request with SigV4-X509: the same canonical
/// request and string-to-sign as ordinary SigV4, but the final signature is an asymmetric X.509
/// signature made with the certificate's private key, and the credential carries the certificate
/// serial instead of an access-key id.
/// </summary>
public static partial class RolesAnywhereSigner
{
    /// <summary>Lowercase hex of the SHA-256 of <paramref name="data"/>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// The certificate serial number as a decimal string. AWS puts this in the Credential field, and
    /// expects decimal — emitting the hex form (which <see cref="X509Certificate2.SerialNumber"/>
    /// returns) is a silent rejection.
    /// </summary>
    public static string SerialDecimal(X509Certificate2 certificate)
    {
        // Prepend "00" so the high bit never reads as a negative BigInteger.
        var serial = BigInteger.Parse("00" + certificate.SerialNumber, NumberStyles.HexNumber);
        return serial.ToString(CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereSignerTests"`
Expected: PASS (both tests).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/RolesAnywhere/ tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/
git commit -m "feat: Roles Anywhere signer parameters and hashing helpers"
```

---

## Task 2: Algorithm selection + asymmetric signing

**Files:**
- Modify: `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereSigner.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignerTests.cs`

**Interfaces:**
- Consumes: `RolesAnywhereSigner` (Task 1).
- Produces:
  - `static string RolesAnywhereSigner.SelectAlgorithm(X509Certificate2 certificate)` → `"AWS4-X509-RSA-SHA256"` or `"AWS4-X509-ECDSA-SHA256"`.
  - `static byte[] RolesAnywhereSigner.SignBytes(X509Certificate2 certificate, string algorithm, byte[] data)`.

- [ ] **Step 1: Write the failing test**

```csharp
// append to RolesAnywhereSignerTests.cs
[Fact]
public void SelectAlgorithm_RsaCertificate_ReturnsRsaAlgorithm()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
    RolesAnywhereSigner.SelectAlgorithm(cert).Should().Be("AWS4-X509-RSA-SHA256");
}

[Fact]
public void SelectAlgorithm_EcCertificate_ReturnsEcdsaAlgorithm()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateEc();
    RolesAnywhereSigner.SelectAlgorithm(cert).Should().Be("AWS4-X509-ECDSA-SHA256");
}

[Fact]
public void SignBytes_RsaSignature_VerifiesAgainstCertificatePublicKey()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
    byte[] data = Encoding.UTF8.GetBytes("string-to-sign");

    byte[] signature = RolesAnywhereSigner.SignBytes(cert, "AWS4-X509-RSA-SHA256", data);

    using RSA pub = cert.GetRSAPublicKey()!;
    pub.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
       .Should().BeTrue();
}

[Fact]
public void SignBytes_RsaSignature_IsDeterministic()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
    byte[] data = Encoding.UTF8.GetBytes("string-to-sign");

    byte[] first = RolesAnywhereSigner.SignBytes(cert, "AWS4-X509-RSA-SHA256", data);
    byte[] second = RolesAnywhereSigner.SignBytes(cert, "AWS4-X509-RSA-SHA256", data);

    first.Should().Equal(second); // PKCS#1 v1.5 is deterministic
}

[Fact]
public void SignBytes_EcdsaSignature_VerifiesAsDerSequence()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateEc();
    byte[] data = Encoding.UTF8.GetBytes("string-to-sign");

    byte[] signature = RolesAnywhereSigner.SignBytes(cert, "AWS4-X509-ECDSA-SHA256", data);

    using ECDsa pub = cert.GetECDsaPublicKey()!;
    pub.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
       .Should().BeTrue();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereSignerTests"`
Expected: FAIL — `SelectAlgorithm` / `SignBytes` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// add to RolesAnywhereSigner (same class)
public const string RsaAlgorithm = "AWS4-X509-RSA-SHA256";
public const string EcdsaAlgorithm = "AWS4-X509-ECDSA-SHA256";

/// <summary>
/// The SigV4-X509 algorithm string for this certificate's key type. AWS rejects a request whose
/// declared algorithm does not match the certificate's public key, so it is derived from the key,
/// never guessed.
/// </summary>
public static string SelectAlgorithm(X509Certificate2 certificate)
{
    using (RSA? rsa = certificate.GetRSAPrivateKey())
    {
        if (rsa is not null) return RsaAlgorithm;
    }
    using (ECDsa? ecdsa = certificate.GetECDsaPrivateKey())
    {
        if (ecdsa is not null) return EcdsaAlgorithm;
    }
    throw new InvalidOperationException("Certificate has neither an RSA nor an ECDSA private key.");
}

/// <summary>
/// Signs <paramref name="data"/> (the string-to-sign bytes) with the certificate's private key.
/// RSA is PKCS#1 v1.5 over SHA-256; ECDSA is SHA-256 with a DER-encoded signature — the two forms
/// AWS accepts. The raw IEEE-P1363 ECDSA form the default overload produces is rejected.
/// </summary>
public static byte[] SignBytes(X509Certificate2 certificate, string algorithm, byte[] data)
{
    if (algorithm == RsaAlgorithm)
    {
        using RSA rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("RSA algorithm selected but certificate has no RSA private key.");
        return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    if (algorithm == EcdsaAlgorithm)
    {
        using ECDsa ecdsa = certificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("ECDSA algorithm selected but certificate has no ECDSA private key.");
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported Roles Anywhere algorithm.");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereSignerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereSigner.cs tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignerTests.cs
git commit -m "feat: Roles Anywhere asymmetric signing (RSA PKCS1, ECDSA DER)"
```

---

## Task 3: Canonical request + string-to-sign builders

**Files:**
- Modify: `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereSigner.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignerTests.cs`

**Interfaces:**
- Consumes: `RolesAnywhereSigner.Sha256Hex` (Task 1).
- Produces:
  - `static string RolesAnywhereSigner.BuildCanonicalRequest(string httpMethod, string canonicalUri, string canonicalQueryString, IReadOnlyList<KeyValuePair<string, string>> sortedHeaders, string signedHeaders, string payloadHashHex)`
  - `static string RolesAnywhereSigner.BuildStringToSign(string algorithm, string amzDate, string credentialScope, string canonicalRequestHashHex)`

The canonical form is pinned by structure rather than a full golden string: AWS publishes no example with a real signature, and its example uses a placeholder certificate. The tests assert every byte-level rule that actually breaks signatures (ordering, blank lines, trailing hash, trimmed values).

- [ ] **Step 1: Write the failing test**

```csharp
// append to RolesAnywhereSignerTests.cs
[Fact]
public void BuildCanonicalRequest_MatchesAwsExampleLayout_ForEmptyPayload()
{
    var headers = new List<KeyValuePair<string, string>>
    {
        new("content-type", "application/json"),
        new("host", "rolesanywhere.us-east-1.amazonaws.com"),
        new("x-amz-date", "20211103T120000Z"),
        new("x-amz-x509", "BASE64DER"),
    };
    string emptyPayloadHash = RolesAnywhereSigner.Sha256Hex(Array.Empty<byte>());

    string canonical = RolesAnywhereSigner.BuildCanonicalRequest(
        "POST", "/sessions", "", headers,
        "content-type;host;x-amz-date;x-amz-x509", emptyPayloadHash);

    string expected =
        "POST\n" +
        "/sessions\n" +
        "\n" +
        "content-type:application/json\n" +
        "host:rolesanywhere.us-east-1.amazonaws.com\n" +
        "x-amz-date:20211103T120000Z\n" +
        "x-amz-x509:BASE64DER\n" +
        "\n" +
        "content-type;host;x-amz-date;x-amz-x509\n" +
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    canonical.Should().Be(expected);
}

[Fact]
public void BuildCanonicalRequest_TrimsHeaderValues()
{
    var headers = new List<KeyValuePair<string, string>>
    {
        new("host", "  example  "),
    };

    string canonical = RolesAnywhereSigner.BuildCanonicalRequest(
        "POST", "/sessions", "", headers, "host", "HASH");

    canonical.Should().Contain("host:example\n");
}

[Fact]
public void BuildStringToSign_HasFourLinesInFixedOrder()
{
    string sts = RolesAnywhereSigner.BuildStringToSign(
        "AWS4-X509-RSA-SHA256",
        "20211101T121030Z",
        "20211101/us-east-1/rolesanywhere/aws4_request",
        "abc123");

    sts.Should().Be(
        "AWS4-X509-RSA-SHA256\n" +
        "20211101T121030Z\n" +
        "20211101/us-east-1/rolesanywhere/aws4_request\n" +
        "abc123");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereSignerTests"`
Expected: FAIL — `BuildCanonicalRequest` / `BuildStringToSign` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// add to RolesAnywhereSigner
using System.Text; // ensure present at top of file

/// <summary>
/// The SigV4 canonical request. <paramref name="sortedHeaders"/> must already be lowercase-named and
/// ordinal-sorted by name; values are whitespace-trimmed here.
/// </summary>
public static string BuildCanonicalRequest(
    string httpMethod,
    string canonicalUri,
    string canonicalQueryString,
    IReadOnlyList<KeyValuePair<string, string>> sortedHeaders,
    string signedHeaders,
    string payloadHashHex)
{
    var builder = new StringBuilder();
    builder.Append(httpMethod).Append('\n');
    builder.Append(canonicalUri).Append('\n');
    builder.Append(canonicalQueryString).Append('\n');
    foreach (KeyValuePair<string, string> header in sortedHeaders)
    {
        builder.Append(header.Key).Append(':').Append(header.Value.Trim()).Append('\n');
    }
    builder.Append('\n');
    builder.Append(signedHeaders).Append('\n');
    builder.Append(payloadHashHex);
    return builder.ToString();
}

/// <summary>The SigV4 string-to-sign: algorithm, timestamp, credential scope, hashed canonical request.</summary>
public static string BuildStringToSign(
    string algorithm, string amzDate, string credentialScope, string canonicalRequestHashHex)
    => $"{algorithm}\n{amzDate}\n{credentialScope}\n{canonicalRequestHashHex}";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereSignerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereSigner.cs tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignerTests.cs
git commit -m "feat: Roles Anywhere canonical request and string-to-sign"
```

---

## Task 4: Assemble the signed request

**Files:**
- Modify: `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereSigner.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignerTests.cs`

**Interfaces:**
- Consumes: all Task 1-3 helpers.
- Produces:
  - `sealed record SignedSessionRequest(string Url, string JsonBody, IReadOnlyList<KeyValuePair<string, string>> Headers)`
  - `static SignedSessionRequest RolesAnywhereSigner.Sign(X509Certificate2 certificate, RolesAnywhereParameters parameters, DateTimeOffset signingTime)`
  - The `Headers` list contains, at least: `x-amz-date`, `x-amz-x509`, `content-type` (value exactly `application/json`), and `authorization`.

- [ ] **Step 1: Write the failing test**

```csharp
// append to RolesAnywhereSignerTests.cs
[Fact]
public void Sign_ProducesRegionalUrlAndJsonBodyWithArns()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
    var parameters = new RolesAnywhereParameters(
        "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
        "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
        "arn:aws:iam::111:role/connapse",
        "us-east-1");

    RolesAnywhereSigner.SignedSessionRequest signed =
        RolesAnywhereSigner.Sign(cert, parameters, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));

    signed.Url.Should().Be("https://rolesanywhere.us-east-1.amazonaws.com/sessions");
    signed.JsonBody.Should().Contain("\"profileArn\":\"arn:aws:rolesanywhere:us-east-1:111:profile/pf\"");
    signed.JsonBody.Should().Contain("\"roleArn\":\"arn:aws:iam::111:role/connapse\"");
    signed.JsonBody.Should().Contain("\"trustAnchorArn\":\"arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta\"");
}

[Fact]
public void Sign_ContentTypeHeaderHasNoCharset()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
    RolesAnywhereSigner.SignedSessionRequest signed = SignSample(cert);

    string contentType = signed.Headers.Single(h => h.Key == "content-type").Value;
    contentType.Should().Be("application/json"); // a "; charset=utf-8" here would break the signature
}

[Fact]
public void Sign_AuthorizationHeader_UsesDecimalSerialAndRsaAlgorithm()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
    RolesAnywhereSigner.SignedSessionRequest signed = SignSample(cert);

    string auth = signed.Headers.Single(h => h.Key == "authorization").Value;
    string serial = RolesAnywhereSigner.SerialDecimal(cert);
    auth.Should().StartWith("AWS4-X509-RSA-SHA256 Credential=" + serial + "/20260901/us-east-1/rolesanywhere/aws4_request");
    auth.Should().Contain("SignedHeaders=content-type;host;x-amz-date;x-amz-x509");
    auth.Should().Contain("Signature=");
}

[Fact]
public void Sign_XAmzX509Header_IsBase64OfCertificateDer()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
    RolesAnywhereSigner.SignedSessionRequest signed = SignSample(cert);

    string x509 = signed.Headers.Single(h => h.Key == "x-amz-x509").Value;
    x509.Should().Be(Convert.ToBase64String(cert.RawData));
}

[Fact]
public void Sign_SignatureInAuthorization_VerifiesAgainstCertificatePublicKey()
{
    using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
    RolesAnywhereSigner.SignedSessionRequest signed = SignSample(cert);

    // Recompute the string-to-sign from the emitted request and confirm the signature is valid.
    string auth = signed.Headers.Single(h => h.Key == "authorization").Value;
    string signatureHex = auth.Split("Signature=")[1];
    byte[] signature = Convert.FromHexString(signatureHex);

    string amzDate = signed.Headers.Single(h => h.Key == "x-amz-date").Value;
    string x509 = signed.Headers.Single(h => h.Key == "x-amz-x509").Value;
    var headers = new List<KeyValuePair<string, string>>
    {
        new("content-type", "application/json"),
        new("host", "rolesanywhere.us-east-1.amazonaws.com"),
        new("x-amz-date", amzDate),
        new("x-amz-x509", x509),
    };
    string payloadHash = RolesAnywhereSigner.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(signed.JsonBody));
    string canonical = RolesAnywhereSigner.BuildCanonicalRequest(
        "POST", "/sessions", "", headers, "content-type;host;x-amz-date;x-amz-x509", payloadHash);
    string sts = RolesAnywhereSigner.BuildStringToSign(
        "AWS4-X509-RSA-SHA256", amzDate, "20260901/us-east-1/rolesanywhere/aws4_request",
        RolesAnywhereSigner.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(canonical)));

    using RSA pub = cert.GetRSAPublicKey()!;
    pub.VerifyData(System.Text.Encoding.UTF8.GetBytes(sts), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
       .Should().BeTrue();
}

private static RolesAnywhereSigner.SignedSessionRequest SignSample(X509Certificate2 cert)
{
    var parameters = new RolesAnywhereParameters(
        "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
        "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
        "arn:aws:iam::111:role/connapse",
        "us-east-1");
    return RolesAnywhereSigner.Sign(cert, parameters, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereSignerTests"`
Expected: FAIL — `SignedSessionRequest` / `Sign` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// add to RolesAnywhereSigner
using System.Text.Json; // ensure present at top of file

/// <summary>A signed CreateSession request: where to send it, the exact body bytes, and the headers.</summary>
public sealed record SignedSessionRequest(
    string Url, string JsonBody, IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// Builds and signs the CreateSession request. Deterministic given <paramref name="signingTime"/>,
/// which is what makes the whole engine unit-testable without live AWS.
/// </summary>
public static SignedSessionRequest Sign(
    X509Certificate2 certificate, RolesAnywhereParameters parameters, DateTimeOffset signingTime)
{
    DateTime utc = signingTime.UtcDateTime;
    string amzDate = utc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
    string dateStamp = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    string host = $"rolesanywhere.{parameters.Region}.amazonaws.com";

    string body = BuildBody(parameters);
    string payloadHash = Sha256Hex(Encoding.UTF8.GetBytes(body));
    string x509 = Convert.ToBase64String(certificate.RawData);

    var headers = new List<KeyValuePair<string, string>>
    {
        new("content-type", "application/json"),
        new("host", host),
        new("x-amz-date", amzDate),
        new("x-amz-x509", x509),
    };
    headers.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
    const string signedHeaders = "content-type;host;x-amz-date;x-amz-x509";

    string canonicalRequest = BuildCanonicalRequest("POST", "/sessions", "", headers, signedHeaders, payloadHash);
    string credentialScope = $"{dateStamp}/{parameters.Region}/rolesanywhere/aws4_request";
    string algorithm = SelectAlgorithm(certificate);
    string stringToSign = BuildStringToSign(
        algorithm, amzDate, credentialScope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

    string signatureHex = Convert.ToHexStringLower(SignBytes(certificate, algorithm, Encoding.UTF8.GetBytes(stringToSign)));
    string credential = $"{SerialDecimal(certificate)}/{credentialScope}";
    string authorization =
        $"{algorithm} Credential={credential}, SignedHeaders={signedHeaders}, Signature={signatureHex}";

    var outgoing = new List<KeyValuePair<string, string>>
    {
        new("content-type", "application/json"),
        new("host", host),
        new("x-amz-date", amzDate),
        new("x-amz-x509", x509),
        new("authorization", authorization),
    };

    return new SignedSessionRequest($"https://{host}/sessions", body, outgoing);
}

private static string BuildBody(RolesAnywhereParameters parameters)
{
    var payload = new Dictionary<string, object>
    {
        ["profileArn"] = parameters.ProfileArn,
        ["roleArn"] = parameters.RoleArn,
        ["trustAnchorArn"] = parameters.TrustAnchorArn,
    };
    if (parameters.DurationSeconds is int seconds)
    {
        payload["durationSeconds"] = seconds;
    }
    if (!string.IsNullOrWhiteSpace(parameters.RoleSessionName))
    {
        payload["roleSessionName"] = parameters.RoleSessionName;
    }
    return JsonSerializer.Serialize(payload);
}
```

Note: the body is hashed from the exact string returned by `BuildBody`, and the same string is what `RolesAnywhereClient` sends — so field ordering is irrelevant to correctness as long as sent bytes equal hashed bytes.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereSignerTests"`
Expected: PASS (all signer tests).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereSigner.cs tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignerTests.cs
git commit -m "feat: assemble signed Roles Anywhere CreateSession request"
```

---

## Task 5: CreateSession HTTP client

**Files:**
- Create: `src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereClient.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereClientTests.cs`

**Interfaces:**
- Consumes: `RolesAnywhereSigner.Sign` (Task 4), `RolesAnywhereParameters` (Task 1).
- Produces:
  - `sealed record RolesAnywhereSession(ImmutableCredentials Credentials, DateTimeOffset Expiration)` (`ImmutableCredentials` from `Amazon.Runtime`).
  - `sealed class RolesAnywhereException(int statusCode, string body) : Exception` with an `int StatusCode` property.
  - `sealed class RolesAnywhereClient(HttpClient httpClient)` with
    `Task<RolesAnywhereSession> CreateSessionAsync(X509Certificate2 certificate, RolesAnywhereParameters parameters, DateTimeOffset now, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereClientTests.cs
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime;
using Connapse.Storage.CloudScope.RolesAnywhere;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

[Trait("Category", "Unit")]
public class RolesAnywhereClientTests
{
    private static readonly RolesAnywhereParameters Params = new(
        "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
        "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
        "arn:aws:iam::111:role/connapse",
        "us-east-1");

    [Fact]
    public async Task CreateSessionAsync_On201_ReturnsTemporaryCredentials()
    {
        const string json = """
        {
          "credentialSet": [
            {
              "credentials": {
                "accessKeyId": "ASIA_TEMP",
                "secretAccessKey": "temp-secret",
                "sessionToken": "temp-token",
                "expiration": "2026-09-01T13:00:00Z"
              }
            }
          ]
        }
        """;
        var handler = new StubHandler(HttpStatusCode.Created, json);
        var client = new RolesAnywhereClient(new HttpClient(handler));
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();

        RolesAnywhereSession session = await client.CreateSessionAsync(
            cert, Params, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));

        ImmutableCredentials creds = session.Credentials;
        creds.AccessKey.Should().Be("ASIA_TEMP");
        creds.SecretKey.Should().Be("temp-secret");
        creds.Token.Should().Be("temp-token");
        session.Expiration.Should().Be(DateTimeOffset.Parse("2026-09-01T13:00:00Z"));
    }

    [Fact]
    public async Task CreateSessionAsync_SendsSignedHeadersAndNoCharsetContentType()
    {
        var handler = new StubHandler(HttpStatusCode.Created, MinimalBody);
        var client = new RolesAnywhereClient(new HttpClient(handler));
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();

        await client.CreateSessionAsync(cert, Params, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://rolesanywhere.us-east-1.amazonaws.com/sessions");
        handler.LastRequest.Headers.Contains("X-Amz-X509").Should().BeTrue();
        handler.LastRequest.Headers.Authorization.Should().BeNull(); // sent raw, not parsed as scheme
        handler.LastRequest.Headers.TryGetValues("Authorization", out _).Should().BeTrue();
        handler.LastRequest.Content!.Headers.ContentType!.ToString().Should().Be("application/json");
    }

    [Fact]
    public async Task CreateSessionAsync_OnNon201_ThrowsWithStatusAndBody()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "{\"message\":\"denied\"}");
        var client = new RolesAnywhereClient(new HttpClient(handler));
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();

        Func<Task> act = () => client.CreateSessionAsync(cert, Params, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));

        (await act.Should().ThrowAsync<RolesAnywhereException>())
            .Which.StatusCode.Should().Be(403);
    }

    private const string MinimalBody = """
    {"credentialSet":[{"credentials":{"accessKeyId":"a","secretAccessKey":"s","sessionToken":"t","expiration":"2026-09-01T13:00:00Z"}}]}
    """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereClientTests"`
Expected: FAIL — `RolesAnywhereClient` / `RolesAnywhereSession` / `RolesAnywhereException` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereClient.cs
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;

namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>Temporary credentials from a CreateSession call, and when they expire.</summary>
public sealed record RolesAnywhereSession(ImmutableCredentials Credentials, DateTimeOffset Expiration);

/// <summary>A CreateSession response other than 201, surfaced with its status and body for diagnosis.</summary>
public sealed class RolesAnywhereException(int statusCode, string body)
    : Exception($"IAM Roles Anywhere CreateSession failed with HTTP {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Exchanges an X.509 certificate for temporary AWS credentials via IAM Roles Anywhere, signing the
/// request itself (no aws_signing_helper binary). The <see cref="HttpClient"/> is injected so the
/// caller owns its lifetime and so tests can stub the transport.
/// </summary>
public sealed class RolesAnywhereClient(HttpClient httpClient)
{
    public async Task<RolesAnywhereSession> CreateSessionAsync(
        X509Certificate2 certificate,
        RolesAnywhereParameters parameters,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        RolesAnywhereSigner.SignedSessionRequest signed = RolesAnywhereSigner.Sign(certificate, parameters, now);

        using var request = new HttpRequestMessage(HttpMethod.Post, signed.Url);

        // Content-Type must be exactly "application/json" — StringContent's (body, encoding, media)
        // overload appends "; charset=utf-8", which would not match the signed header value.
        var content = new StringContent(signed.JsonBody, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        foreach (KeyValuePair<string, string> header in signed.Headers)
        {
            if (header.Key is "content-type" or "host")
            {
                continue; // content-type is on the content; host is set by HttpClient from the URL.
            }
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
        string responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new RolesAnywhereException((int)response.StatusCode, responseBody);
        }

        return Parse(responseBody);
    }

    private static RolesAnywhereSession Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement credentials = document.RootElement
            .GetProperty("credentialSet")[0]
            .GetProperty("credentials");

        string accessKeyId = credentials.GetProperty("accessKeyId").GetString()!;
        string secretAccessKey = credentials.GetProperty("secretAccessKey").GetString()!;
        string sessionToken = credentials.GetProperty("sessionToken").GetString()!;
        DateTimeOffset expiration = credentials.GetProperty("expiration").GetDateTimeOffset();

        return new RolesAnywhereSession(
            new ImmutableCredentials(accessKeyId, secretAccessKey, sessionToken), expiration);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RolesAnywhereClientTests"`
Expected: PASS.

- [ ] **Step 5: Run the whole unit suite and commit**

Run: `dotnet test --filter "Category=Unit"`
Expected: PASS (no regressions — this PR adds only new files).

```bash
git add src/Connapse.Storage/CloudScope/RolesAnywhere/RolesAnywhereClient.cs tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereClientTests.cs
git commit -m "feat: Roles Anywhere CreateSession client"
```

---

## Self-Review

**Spec coverage (delivery step 1 only):**
- "native SigV4-X509 signer" → Tasks 1-4.
- "CreateSession HTTP client returning temporary credentials" → Task 5.
- "no aws_signing_helper binary" → all signing is in-process `System.Security.Cryptography`.
- Out of scope for this PR (deferred, per the re-slice): storage reshape, `ConnapseAwsCredentials` wiring, DI registration, keypair generation, setup script, UI. These are PR 2 / PR 3.

**Type consistency:** `RolesAnywhereParameters`, `RolesAnywhereSigner.SignedSessionRequest`, `RolesAnywhereSession`, `RolesAnywhereException`, and the helper signatures are referenced identically across tasks. `SelectAlgorithm` returns the same constant strings `SignBytes` switches on.

**Placeholder scan:** none — every step has runnable code and an exact test command.

**Deferred correctness note for PR 2:** `ConnapseAwsCredentials.GenerateNewCredentials` is synchronous and runs under a Blazor synchronization context, so wiring `CreateSessionAsync` in must reuse the existing `Task.Run(...).GetAwaiter().GetResult()` pattern already documented in that file — not a naive `.Result`. Called out here so it is not lost between PRs.
