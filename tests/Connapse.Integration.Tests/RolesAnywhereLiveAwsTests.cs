using System.Security.Cryptography.X509Certificates;
using Connapse.Core.Utilities;
using Connapse.Storage.CloudScope.RolesAnywhere;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests;

/// <summary>
/// The one thing unit tests cannot prove: that AWS actually accepts Connapse's CA→leaf certificate
/// chain and issues temporary credentials for a leaf-signed <c>CreateSession</c>.
/// </summary>
/// <remarks>
/// Tagged <c>Category=LiveAws</c> and never run by CI or the normal unit/integration filters. It
/// reaches real AWS, so it runs only when an operator has set the environment variables below,
/// pointing at a trust anchor they created by running the generated CloudShell script. Without them
/// each test no-ops (xUnit 2 has no runtime skip), so it is safe to leave in the suite. The full
/// procedure — generate a keypair, register the CA as a trust anchor, export the variables — is in
/// <c>docs/superpowers/runbooks/2026-09-02-roles-anywhere-live-aws-acceptance.md</c>.
/// </remarks>
[Trait("Category", "LiveAws")]
public class RolesAnywhereLiveAwsTests(ITestOutputHelper output)
{
    /// <summary>
    /// Signs a real <c>CreateSession</c> with the leaf key against a live trust anchor and asserts AWS
    /// returns temporary credentials — the epic's acceptance gate for the whole signing/setup chain.
    /// </summary>
    [Fact]
    public async Task CreateSession_AgainstARealTrustAnchor_ReturnsTemporaryCredentials()
    {
        // Two-level gate so acceptance evidence cannot be faked by a green no-op. Without the explicit
        // opt-in the test is a genuine no-op (it never reaches AWS). WITH the opt-in — the only way the
        // runbook claims the gate — a missing or invalid value is a FAILURE, not a pass, so a typo or
        // absent secret can never be recorded as a satisfied acceptance gate.
        bool optedIn = Environment.GetEnvironmentVariable("CONNAPSE_LIVE_AWS_RUN") == "1";
        LiveConfig? cfg = LiveConfig.FromEnvironment();

        if (cfg is null)
        {
            const string vars = "CONNAPSE_LIVE_AWS_RA_CERT_FILE, _KEY_FILE, _TRUST_ANCHOR_ARN, "
                + "_PROFILE_ARN, _ROLE_ARN and _REGION";
            if (optedIn)
            {
                Assert.Fail(
                    $"CONNAPSE_LIVE_AWS_RUN=1 asks to run the live-AWS gate, but the configuration is "
                    + $"incomplete or its files are missing. Set {vars} (see the runbook).");
            }

            output.WriteLine(
                $"Skipped: set CONNAPSE_LIVE_AWS_RUN=1 and {vars} (see the runbook) to run the live gate.");
            return;
        }

        if (!optedIn)
        {
            output.WriteLine(
                "Skipped: live config is present but CONNAPSE_LIVE_AWS_RUN=1 is not set, so this run "
                + "does not reach AWS. Set it to run the acceptance gate.");
            return;
        }

        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(cfg.CertificatePem, cfg.PrivateKeyPem);
        var client = new RolesAnywhereClient(new HttpClient());
        var parameters = new RolesAnywhereParameters(cfg.TrustAnchorArn, cfg.ProfileArn, cfg.RoleArn, cfg.Region);

        RolesAnywhereSession session = await client.CreateSessionAsync(certificate, parameters, DateTimeOffset.UtcNow);

        session.Credentials.AccessKey.Should().NotBeNullOrEmpty();
        session.Credentials.SecretKey.Should().NotBeNullOrEmpty();
        session.Credentials.Token.Should().NotBeNullOrEmpty();
        session.Expiration.Should().BeAfter(DateTimeOffset.UtcNow);

        output.WriteLine(
            $"AWS accepted the leaf-signed CreateSession against {cfg.TrustAnchorArn}; " +
            $"temporary credentials expire {session.Expiration:O}.");
    }

    /// <summary>
    /// Generates a fresh CA + leaf keypair and writes the three PEMs to a directory, so an operator can
    /// register the CA as a trust anchor and point the verify test at the leaf. Gated by its own
    /// variable so it never writes files during a normal run.
    /// </summary>
    [Fact]
    public void GenerateKeypair_WritesCaLeafAndKey_ForManualTrustAnchorSetup()
    {
        string? outDir = Environment.GetEnvironmentVariable("CONNAPSE_LIVE_AWS_RA_KEYGEN_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
        {
            output.WriteLine(
                "Skipped: set CONNAPSE_LIVE_AWS_RA_KEYGEN_DIR to a directory to generate a CA+leaf " +
                "keypair for the live-AWS setup (see the runbook).");
            return;
        }

        string region = Environment.GetEnvironmentVariable("CONNAPSE_LIVE_AWS_RA_REGION") ?? "us-east-1";

        RolesAnywhereKeyMaterial material = RolesAnywhereKeyGenerator.Generate();

        // Owner-only directory: the leaf key authorizes AWS while its trust anchor and role are live,
        // so it must never be left group/other-readable in a shared or CI host. On Unix the directory
        // is 0700 and the key file 0600; on Windows the inherited ACL applies (the flow is documented
        // for Linux/CloudShell).
        Directory.CreateDirectory(outDir);
        string keyPath = Path.Combine(outDir, "leaf-key.pem");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(outDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(Path.Combine(outDir, "ca.pem"), material.CaCertificatePem);
        File.WriteAllText(Path.Combine(outDir, "leaf-cert.pem"), material.LeafCertificatePem);
        File.WriteAllText(keyPath, material.LeafPrivateKeyPem);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // The exact CloudShell script the product generates for this CA — run it to create the trust
        // anchor/profile/role, so the live test exercises the real setup path, not a bespoke one.
        File.WriteAllText(
            Path.Combine(outDir, "setup.sh"),
            AwsRolesAnywhereSetup.GenerateScript(material.CaCertificatePem, region));

        output.WriteLine(
            $"Wrote ca.pem, leaf-cert.pem, leaf-key.pem (owner-only) and setup.sh (region {region}) to " +
            $"{outDir}. Run setup.sh in AWS CloudShell, then point the verify test at " +
            "leaf-cert.pem/leaf-key.pem and the printed ARNs. Delete the directory when done — it holds a private key.");
    }

    /// <summary>The live setup, read from the environment. Null when it is not fully configured.</summary>
    private sealed record LiveConfig(
        string CertificatePem, string PrivateKeyPem,
        string TrustAnchorArn, string ProfileArn, string RoleArn, string Region)
    {
        public static LiveConfig? FromEnvironment()
        {
            string? certFile = Environment.GetEnvironmentVariable("CONNAPSE_LIVE_AWS_RA_CERT_FILE");
            string? keyFile = Environment.GetEnvironmentVariable("CONNAPSE_LIVE_AWS_RA_KEY_FILE");
            string? trustAnchor = Environment.GetEnvironmentVariable("CONNAPSE_LIVE_AWS_RA_TRUST_ANCHOR_ARN");
            string? profile = Environment.GetEnvironmentVariable("CONNAPSE_LIVE_AWS_RA_PROFILE_ARN");
            string? role = Environment.GetEnvironmentVariable("CONNAPSE_LIVE_AWS_RA_ROLE_ARN");
            string? region = Environment.GetEnvironmentVariable("CONNAPSE_LIVE_AWS_RA_REGION");

            if (string.IsNullOrWhiteSpace(certFile) || string.IsNullOrWhiteSpace(keyFile)
                || string.IsNullOrWhiteSpace(trustAnchor) || string.IsNullOrWhiteSpace(profile)
                || string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(region)
                || !File.Exists(certFile) || !File.Exists(keyFile))
            {
                return null;
            }

            return new LiveConfig(
                File.ReadAllText(certFile), File.ReadAllText(keyFile),
                trustAnchor, profile, role, region);
        }
    }
}
