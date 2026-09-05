using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Web.Components.Settings;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Settings;

/// <summary>
/// The connection editor's mapping between form fields and stored config JSON.
/// <para>
/// This is the part of the Connections tab that carries risk: a field dropped in the round trip
/// produces a connection that resolves somewhere other than the operator intended, and nothing
/// downstream can tell the difference. Extracted from the Razor component precisely so it can be
/// tested — this repository has no component test harness.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ConnectionFormTests
{
    private static Connection Stored(ConnectionProvider provider, string configJson, bool hasSecret = false) => new(
        Id: Guid.NewGuid(),
        Name: "conn",
        Provider: provider,
        ConfigJson: configJson,
        CreatedByUserId: null,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        HasSecret: hasSecret);

    // ── Round trip ────────────────────────────────────────────────────

    [Fact]
    public void S3Connection_RoundTripsEveryField()
    {
        var stored = Stored(ConnectionProvider.S3,
            """{"region":"eu-west-1","roleArn":"arn:aws:iam::1:role/r","allowedLocations":["a","b/docs"]}""");

        var form = ConnectionForm.FromConnection(stored);

        form.Region.Should().Be("eu-west-1");
        form.RoleArn.Should().Be("arn:aws:iam::1:role/r");
        form.AllowedLocations.Should().Be("a\nb/docs");

        var reserialized = JsonNode.Parse(form.ToConfigJson())!.AsObject();
        reserialized["region"]!.GetValue<string>().Should().Be("eu-west-1");
        reserialized["roleArn"]!.GetValue<string>().Should().Be("arn:aws:iam::1:role/r");
        reserialized["allowedLocations"]!.AsArray().Select(x => x!.GetValue<string>())
            .Should().BeEquivalentTo(["a", "b/docs"]);
    }

    [Fact]
    public void FilesystemConnection_RoundTripsItsRoot()
    {
        var stored = Stored(ConnectionProvider.Filesystem, """{"allowedRoot":"/data/docs"}""");

        var form = ConnectionForm.FromConnection(stored);
        form.AllowedRoot.Should().Be("/data/docs");

        JsonNode.Parse(form.ToConfigJson())!["allowedRoot"]!.GetValue<string>().Should().Be("/data/docs");
    }

    // ── Serialization rules ───────────────────────────────────────────

    [Fact]
    public void ToConfigJson_OmitsFieldsBelongingToOtherProviders()
    {
        // A form that has been switched between providers must not leave a stale key behind —
        // the connector factory reads whatever is present and would honour it.
        var form = new ConnectionForm
        {
            Name = "c",
            Provider = ConnectionProvider.S3,
            Region = "us-east-1",
            AllowedRoot = "/left-over-from-filesystem",
        };

        var node = JsonNode.Parse(form.ToConfigJson())!.AsObject();

        node.ContainsKey("region").Should().BeTrue();
        node.ContainsKey("allowedRoot").Should().BeFalse();
    }

    [Fact]
    public void ToConfigJson_FilesystemOmitsAllowedLocations()
    {
        // Filesystem confinement is the root plus the subpath check; allowedLocations is the
        // cloud equivalent and does not apply.
        var form = new ConnectionForm
        {
            Name = "c",
            Provider = ConnectionProvider.Filesystem,
            AllowedRoot = "/data",
            AllowedLocations = "some-bucket",
        };

        JsonNode.Parse(form.ToConfigJson())!.AsObject()
            .ContainsKey("allowedLocations").Should().BeFalse();
    }

    [Fact]
    public void ToConfigJson_OmitsAllowedLocationsWhenBlank()
    {
        // Absent must stay absent: an empty array would read as a declared-but-empty allowlist,
        // which denies everything rather than taking the grace path.
        var form = new ConnectionForm { Name = "c", Provider = ConnectionProvider.S3, AllowedLocations = "  \n \n" };

        JsonNode.Parse(form.ToConfigJson())!.AsObject()
            .ContainsKey("allowedLocations").Should().BeFalse();
    }

    [Fact]
    public void ToConfigJson_DefaultsRegionWhenLeftBlank()
    {
        var form = new ConnectionForm { Name = "c", Provider = ConnectionProvider.S3 };

        JsonNode.Parse(form.ToConfigJson())!["region"]!.GetValue<string>().Should().Be("us-east-1");
    }

    // ── The secret is never read back ─────────────────────────────────

    [Fact]
    public void FromConnection_NeverSurfacesAStoredSecret()
    {
        // The form carries no secret field at all: no connector reads a connection secret, so
        // offering one would only invite storing the cloud credential this project refuses to
        // hold. Anything already in a stored config must not survive the round trip either.
        var form = ConnectionForm.FromConnection(
            Stored(ConnectionProvider.S3, """{"region":"eu-west-1","secret":"should-never-surface"}""", hasSecret: true));

        form.ToConfigJson().Should().NotContain("should-never-surface");
    }

    [Fact]
    public void FromConnection_MalformedAllowedLocationEntries_AreSkippedNotThrown()
    {
        // A stored array holding a number would throw out of GetValue<string>() past the parse
        // guard, taking the whole tab down over one bad entry.
        var form = ConnectionForm.FromConnection(
            Stored(ConnectionProvider.S3, """{"region":"eu-west-1","allowedLocations":["good",42,null,{"x":1},"also-good"]}"""));

        form.AllowedLocations.Should().Be("good\nalso-good");
    }

    // ── Robustness ────────────────────────────────────────────────────

    [Fact]
    public void FromConnection_MalformedJson_YieldsAnEmptyFormRatherThanThrowing()
    {
        var form = ConnectionForm.FromConnection(Stored(ConnectionProvider.S3, "{not valid json"));

        form.Provider.Should().Be(ConnectionProvider.S3);
        form.Region.Should().BeNull();
    }

    [Fact]
    public void FromConnection_NullConfig_YieldsAnEmptyForm()
    {
        var stored = new Connection(Guid.NewGuid(), "c", ConnectionProvider.S3, null, null,
            DateTime.UtcNow, DateTime.UtcNow);

        ConnectionForm.FromConnection(stored).Region.Should().BeNull();
    }

    // ── Location splitting, for the test probe ────────────────────────

    [Theory]
    [InlineData("bucket", "bucket", null)]
    [InlineData("bucket/docs", "bucket", "docs")]
    [InlineData("bucket/docs/2026/", "bucket", "docs/2026")]
    [InlineData("/bucket/docs/", "bucket", "docs")]
    public void SplitLocation_SeparatesContainerFromPrefix(string input, string container, string? prefix)
    {
        var (actualContainer, actualPrefix) = ConnectionForm.SplitLocation(input);

        actualContainer.Should().Be(container);
        actualPrefix.Should().Be(prefix);
    }

    [Fact]
    public void FirstAllowedLocation_IsTheProbeDefault()
    {
        var form = new ConnectionForm { AllowedLocations = "first-bucket\nsecond-bucket" };

        form.FirstAllowedLocation().Should().Be("first-bucket");
    }

    [Fact]
    public void FirstAllowedLocation_IsNullWhenNoneDeclared()
    {
        new ConnectionForm().FirstAllowedLocation().Should().BeNull();
    }

    // ── Validation ────────────────────────────────────────────────────

    [Fact]
    public void Validate_RequiresAName()
    {
        new ConnectionForm { Name = "  " }.Validate().Should().Contain("name");
    }

    [Fact]
    public void Validate_FilesystemRequiresARoot()
    {
        new ConnectionForm { Name = "c", Provider = ConnectionProvider.Filesystem }
            .Validate().Should().Contain("root");
    }

    [Fact]
    public void Validate_ValidS3Form_PassesWithNoRegion()
    {
        // Region defaults rather than being required.
        new ConnectionForm { Name = "c", Provider = ConnectionProvider.S3 }
            .Validate().Should().BeNull();
    }

    // ── SFTP ───────────────────────────────────────────────────────────────

    private static ConnectionForm SftpForm() => new()
    {
        Name = "files",
        Provider = ConnectionProvider.Sftp,
        Host = "files.example.com",
        Port = "2222",
        Username = "connapse",
        AllowedRoot = "/srv/knowledge",
        PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nkey\n",
    };

    [Fact]
    public void Sftp_ConfigRoundTrips()
    {
        string json = SftpForm().ToConfigJson();
        var back = ConnectionForm.FromConnection(Stored(ConnectionProvider.Sftp, json));

        back.Host.Should().Be("files.example.com");
        back.Port.Should().Be("2222");
        back.Username.Should().Be("connapse");
        back.AllowedRoot.Should().Be("/srv/knowledge");
    }

    [Fact]
    public void Sftp_NoPort_DefaultsTo22InTheStoredConfig()
    {
        var form = SftpForm() with { Port = null };

        JsonNode.Parse(form.ToConfigJson())!["port"]!.GetValue<int>().Should().Be(22);
    }

    /// <summary>
    /// The pin belongs to the connector, which records it on first connect and compares against
    /// it afterwards. An ordinary save must carry it through — dropping it would silently re-arm
    /// trust on first use, which is the one thing pinning exists to prevent.
    /// </summary>
    [Fact]
    public void Sftp_SavingAnExistingConnection_PreservesThePinnedHostKey()
    {
        var stored = Stored(ConnectionProvider.Sftp,
            """{"host":"h","port":22,"username":"u","allowedRoot":"/srv","hostKeyFingerprint":"SHA256:pinned"}""");

        var form = ConnectionForm.FromConnection(stored);
        form.HostKeyFingerprint.Should().Be("SHA256:pinned");

        JsonNode.Parse(form.ToConfigJson())!["hostKeyFingerprint"]!.GetValue<string>()
            .Should().Be("SHA256:pinned");
    }

    [Fact]
    public void Sftp_ForgettingTheHostKey_DropsItFromTheStoredConfig()
    {
        var stored = Stored(ConnectionProvider.Sftp,
            """{"host":"h","port":22,"username":"u","allowedRoot":"/srv","hostKeyFingerprint":"SHA256:pinned"}""");

        var form = ConnectionForm.FromConnection(stored);
        form.ForgetHostKey = true;

        JsonNode.Parse(form.ToConfigJson())!["hostKeyFingerprint"].Should().BeNull();
    }

    /// <summary>
    /// A secret is never read back into the form, so an operator opening and saving a connection
    /// must not wipe its key. Null means "leave the stored one alone", which is the store's own
    /// rule.
    /// </summary>
    [Fact]
    public void Sftp_FromConnection_LeavesTheKeyBlankSoSavingDoesNotWipeIt()
    {
        var form = ConnectionForm.FromConnection(
            Stored(ConnectionProvider.Sftp, """{"host":"h","username":"u","allowedRoot":"/srv"}""", hasSecret: true));

        form.PrivateKey.Should().BeNull();
        form.ToSecretJson().Should().BeNull();
    }

    [Fact]
    public void Sftp_SecretCarriesTheKeyAndPassphrase()
    {
        var form = SftpForm() with { Passphrase = "hunter2" };

        var secret = JsonNode.Parse(form.ToSecretJson()!)!;

        secret["privateKey"]!.GetValue<string>().Should().Contain("OPENSSH PRIVATE KEY");
        secret["passphrase"]!.GetValue<string>().Should().Be("hunter2");
    }

    /// <summary>
    /// #371 removed the secret field because Connapse does not accept pasted cloud keys. SFTP
    /// brings it back for one provider only, and this is where that stays true.
    /// </summary>
    [Theory]
    [InlineData(ConnectionProvider.S3)]
    [InlineData(ConnectionProvider.Filesystem)]
    public void NonSftpProviders_NeverProduceASecret(ConnectionProvider provider)
    {
        var form = new ConnectionForm
        {
            Name = "c",
            Provider = provider,
            AllowedRoot = "/data",

            // Set deliberately: even with a key sitting in the form, these providers must not
            // store one.
            PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nkey\n",
        };

        form.ToSecretJson().Should().BeNull();
    }

    /// <summary>
    /// SFTP is bounded by a root, not by a bucket allowlist. Written as a positive check on the
    /// cloud providers rather than "not Filesystem", which is what would have swept SFTP into
    /// the wrong branch.
    /// </summary>
    [Fact]
    public void Sftp_DoesNotWriteAllowedLocations()
    {
        var form = SftpForm() with { AllowedLocations = "some-bucket" };

        JsonNode.Parse(form.ToConfigJson())!["allowedLocations"].Should().BeNull();
    }

    [Fact]
    public void Sftp_IsNotACloudProvider()
    {
        SftpForm().IsCloudProvider.Should().BeFalse();
        new ConnectionForm { Provider = ConnectionProvider.S3 }.IsCloudProvider.Should().BeTrue();
        new ConnectionForm { Provider = ConnectionProvider.Filesystem }.IsCloudProvider.Should().BeFalse();
    }

    // ── Connect this computer ──────────────────────────────────────────────

    private static SftpHostSetupResult Reported(
        string user = "jsmith",
        string home = "/C:/Users/jsmith",
        string fingerprint = "SHA256:hostkey") => new(user, home, fingerprint);

    private const string GeneratedKey = "-----BEGIN RSA PRIVATE KEY-----\ngenerated\n";

    [Fact]
    public void ForGuidedSetup_NoPortGiven_DefaultsTo22()
    {
        ConnectionForm.ForGuidedSetup(Reported(), "h", null, GeneratedKey).Port.Should().Be("22");
        ConnectionForm.ForGuidedSetup(Reported(), "h", null, GeneratedKey, "  ").Port.Should().Be("22");
    }

    [Fact]
    public void ForGuidedSetup_APortGiven_IsKept()
    {
        // Its own field rather than something split back out of the host: a colon means
        // something else entirely once the host is an IPv6 literal.
        ConnectionForm.ForGuidedSetup(Reported(), "h", null, GeneratedKey, "2222")
            .Should().BeEquivalentTo(new { Host = "h", Port = "2222" });
    }

    [Fact]
    public void ForGuidedSetup_UsesEveryValueTheHostReported()
    {
        var form = ConnectionForm.ForGuidedSetup(
            Reported(), "host.docker.internal", "Documents", GeneratedKey);

        form.Provider.Should().Be(ConnectionProvider.Sftp);
        form.Username.Should().Be("jsmith");
        form.Host.Should().Be("host.docker.internal");
        form.Port.Should().Be("22");
        form.HostKeyFingerprint.Should().Be("SHA256:hostkey");
        form.PrivateKey.Should().Be(GeneratedKey);
        form.Name.Should().Be("jsmith@host.docker.internal");
    }

    /// <summary>
    /// The restriction that was there by accident. Windows OpenSSH applies no chroot, so a
    /// second drive is perfectly reachable — and confining the flow to the profile would rule
    /// out where most people actually keep things.
    /// </summary>
    [Fact]
    public void ForGuidedSetup_RootOnAnotherDrive_IsAllowed()
    {
        ConnectionForm.ForGuidedSetup(Reported(), "h", "/D:/CodeProjects", GeneratedKey)
            .AllowedRoot.Should().Be("/D:/CodeProjects");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForGuidedSetup_NoFolderChosen_UsesTheHomeDirectory(string? root)
    {
        ConnectionForm.ForGuidedSetup(Reported(), "h", root, GeneratedKey)
            .AllowedRoot.Should().Be("/C:/Users/jsmith");
    }

    /// <summary>
    /// An operator will paste what Explorer shows them, because that is what is on the
    /// clipboard. SFTP takes neither the backslashes nor the bare drive letter.
    /// </summary>
    [Theory]
    [InlineData(@"D:\CodeProjects", "/D:/CodeProjects")]
    [InlineData("D:/CodeProjects", "/D:/CodeProjects")]
    [InlineData(@"C:\Users\jsmith\Documents", "/C:/Users/jsmith/Documents")]
    public void NormaliseRemoteRoot_ConvertsAWindowsPathFromExplorer(string entered, string expected)
    {
        ConnectionForm.NormaliseRemoteRoot(entered, "/fallback").Should().Be(expected);
    }

    [Theory]
    [InlineData("/D:/Projects/", "/D:/Projects")]
    [InlineData("/home/me/docs", "/home/me/docs")]
    [InlineData("home/me", "/home/me")]
    [InlineData("/", "/")]
    public void NormaliseRemoteRoot_ProducesAnAbsoluteUnDoubledPath(string entered, string expected)
    {
        ConnectionForm.NormaliseRemoteRoot(entered, "/fallback").Should().Be(expected);
    }

    /// <summary>
    /// A drive root trims to "/D:" and must stay that, not collapse to the filesystem root —
    /// which would silently widen the connection from one drive to the whole machine.
    /// </summary>
    [Fact]
    public void NormaliseRemoteRoot_DriveRoot_StaysTheDrive()
    {
        ConnectionForm.NormaliseRemoteRoot("/D:/", "/fallback").Should().Be("/D:");
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/D:", true)]
    [InlineData("/D:/", true)]
    [InlineData("/C:/Users/jsmith", false)]
    [InlineData("/D:/CodeProjects", false)]
    [InlineData("/home/me", false)]
    [InlineData(null, false)]
    public void IsBroadRemoteRoot_FlagsWholeDrivesAndTheFilesystemRoot(string? root, bool expected)
    {
        ConnectionForm.IsBroadRemoteRoot(root).Should().Be(expected);
    }

    /// <summary>
    /// The whole flow exists to avoid typing, so what it produces must be savable as-is.
    /// </summary>
    [Fact]
    public void ForGuidedSetup_ProducesAConnectionThatValidatesAndStoresItsKey()
    {
        var pair = SshKeyPairGenerator.Generate();

        var form = ConnectionForm.ForGuidedSetup(
            Reported(), "host.docker.internal", "Documents", pair.PrivateKeyPem);

        form.Validate(isNew: true).Should().BeNull();
        form.ToSecretJson().Should().NotBeNull();

        var config = JsonNode.Parse(form.ToConfigJson())!;
        config["host"]!.GetValue<string>().Should().Be("host.docker.internal");
        config["port"]!.GetValue<int>().Should().Be(22);
        config["hostKeyFingerprint"]!.GetValue<string>().Should().Be("SHA256:hostkey");
    }

    /// <summary>
    /// Carrying the fingerprint into the stored config is what makes the first connection
    /// verified rather than trusted — the entire reason the operator pastes anything back.
    /// </summary>
    [Fact]
    public void ForGuidedSetup_PinsTheFingerprintBeforeTheFirstConnection()
    {
        var form = ConnectionForm.ForGuidedSetup(
            Reported(fingerprint: "SHA256:fromTheHost"), "h", null, GeneratedKey);

        JsonNode.Parse(form.ToConfigJson())!["hostKeyFingerprint"]!.GetValue<string>()
            .Should().Be("SHA256:fromTheHost");
    }

    [Theory]
    [InlineData(nameof(ConnectionForm.Host))]
    [InlineData(nameof(ConnectionForm.Username))]
    [InlineData(nameof(ConnectionForm.AllowedRoot))]
    public void Sftp_MissingARequiredField_IsRefused(string missing)
    {
        var form = SftpForm();

        switch (missing)
        {
            case nameof(ConnectionForm.Host): form.Host = null; break;
            case nameof(ConnectionForm.Username): form.Username = null; break;
            case nameof(ConnectionForm.AllowedRoot): form.AllowedRoot = null; break;
        }

        form.Validate().Should().NotBeNull();
    }

    [Fact]
    public void Sftp_NewConnectionWithoutAKey_IsRefused()
    {
        (SftpForm() with { PrivateKey = null }).Validate(isNew: true)
            .Should().Be("A private key is required.");
    }

    [Fact]
    public void Sftp_ExistingConnectionWithoutAKey_IsAllowed()
    {
        (SftpForm() with { PrivateKey = null }).Validate(isNew: false)
            .Should().BeNull("an operator editing a connection should not have to retype the key");
    }

    /// <summary>
    /// An unparseable port becomes 0 and is refused, rather than silently falling back to 22 and
    /// connecting somewhere the operator did not ask for.
    /// </summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    public void Sftp_UnusablePort_IsRefused(string port)
    {
        (SftpForm() with { Port = port }).Validate()
            .Should().Be("The port must be between 1 and 65535.");
    }
}


