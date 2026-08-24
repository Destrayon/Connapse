using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class SftpHostSetupTests
{
    private const string Key = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQC0example connapse";

    // ── Script generation ──────────────────────────────────────────────────

    [Theory]
    [InlineData(HostPlatform.Windows)]
    [InlineData(HostPlatform.MacOS)]
    [InlineData(HostPlatform.Linux)]
    public void GenerateScript_EmbedsTheKeyAndBothMarkers(HostPlatform platform)
    {
        string script = SftpHostSetup.GenerateScript(Key, platform);

        script.Should().Contain(Key);
        script.Should().Contain(SftpHostSetup.BeginMarker);
        script.Should().Contain(SftpHostSetup.EndMarker);
    }

    [Theory]
    [InlineData(HostPlatform.Windows)]
    [InlineData(HostPlatform.MacOS)]
    [InlineData(HostPlatform.Linux)]
    public void GenerateScript_ReportsAllThreeFieldsConnapseNeeds(HostPlatform platform)
    {
        string script = SftpHostSetup.GenerateScript(Key, platform);

        script.Should().Contain("user=");
        script.Should().Contain("home=");
        script.Should().Contain("fingerprint=");
    }

    /// <summary>
    /// Idempotency is not a nicety here — the operator is told the command is safe to re-run,
    /// and a duplicated key in <c>authorized_keys</c> is the kind of mess nobody goes looking
    /// for.
    /// </summary>
    [Theory]
    [InlineData(HostPlatform.Windows, "Select-String")]
    [InlineData(HostPlatform.MacOS, "grep -qxF")]
    [InlineData(HostPlatform.Linux, "grep -qxF")]
    public void GenerateScript_AddsTheKeyOnlyIfAbsent(HostPlatform platform, string guard)
    {
        SftpHostSetup.GenerateScript(Key, platform).Should().Contain(guard);
    }

    /// <summary>
    /// Somebody else's key in that file is not ours to remove.
    /// </summary>
    [Theory]
    [InlineData(HostPlatform.Windows, "Add-Content")]
    [InlineData(HostPlatform.MacOS, ">> ~/.ssh/authorized_keys")]
    [InlineData(HostPlatform.Linux, ">> ~/.ssh/authorized_keys")]
    public void GenerateScript_AppendsRatherThanOverwrites(HostPlatform platform, string append)
    {
        string script = SftpHostSetup.GenerateScript(Key, platform);

        script.Should().Contain(append);
        script.Should().NotContain("Set-Content -Path $keyFile");

        // A truncating redirect, meaning a single '>' not preceded by another. Asserting
        // NotContain("> ~/.ssh/authorized_keys") does not work: that string is a substring of
        // the appending ">> ~/.ssh/authorized_keys" and the check can never pass.
        script.Should().NotMatchRegex(@"[^>]> ~/\.ssh/authorized_keys");
    }

    /// <summary>
    /// The Windows failure that costs an hour: for an account in the Administrators group,
    /// sshd ignores the profile's authorized_keys entirely and reads a machine-wide file whose
    /// ACL it also insists on. A script that skips this produces authentication failures with
    /// nothing to explain them.
    /// </summary>
    [Fact]
    public void GenerateScript_Windows_HandlesTheAdministratorsAuthorizedKeysFile()
    {
        string script = SftpHostSetup.GenerateScript(Key, HostPlatform.Windows);

        script.Should().Contain("administrators_authorized_keys");
        script.Should().Contain("icacls", "sshd refuses the file unless its ACL is restricted");
    }

    /// <summary>
    /// Verified against a real machine: an account that <em>is</em> in Administrators reports
    /// False from the token check when the process is not elevated, because UAC filters the
    /// SID out. Deciding on that alone writes the key to the profile's authorized_keys, which
    /// sshd then ignores — an authentication failure with nothing to explain it.
    /// </summary>
    [Fact]
    public void GenerateScript_Windows_AsksTheGroupRatherThanTheToken()
    {
        string script = SftpHostSetup.GenerateScript(Key, HostPlatform.Windows);

        script.Should().Contain("Get-LocalGroupMember",
            "actual membership, not what an unelevated token happens to carry");
        script.Should().Contain("$_.SID.Value -eq $mySid",
            "matched by SID, since names vary by domain and casing");
    }

    /// <summary>
    /// Refusing early beats half-succeeding. Without elevation the steps leave SSH looking
    /// configured when it is not.
    /// </summary>
    [Fact]
    public void GenerateScript_Windows_StopsWhenNotElevated()
    {
        string script = SftpHostSetup.GenerateScript(Key, HostPlatform.Windows);

        script.Should().Contain("WindowsBuiltInRole]::Administrator");
        script.Should().Contain("Run as Administrator");
    }

    /// <summary>
    /// Reading the fingerprint needs elevation and can still fail. By that point the key is
    /// installed, so a throw would discard a setup that otherwise worked — the operator loses
    /// verified-first-use, not the connection.
    /// </summary>
    [Fact]
    public void GenerateScript_Windows_SurvivesAnUnreadableHostKey()
    {
        string script = SftpHostSetup.GenerateScript(Key, HostPlatform.Windows);

        script.Should().Contain("$fingerprint = ''", "it must have a value even when reading fails");
        script.Should().Contain("try {");
        script.Should().Contain("} catch { }");
    }

    [Fact]
    public void GenerateScript_Windows_InstallsAndEnablesTheServer()
    {
        string script = SftpHostSetup.GenerateScript(Key, HostPlatform.Windows);

        script.Should().Contain("OpenSSH.Server");
        script.Should().Contain("StartupType Automatic");
        script.Should().Contain("LocalPort 22");
    }

    [Fact]
    public void GenerateScript_MacOs_UsesRemoteLogin()
    {
        SftpHostSetup.GenerateScript(Key, HostPlatform.MacOS)
            .Should().Contain("systemsetup -setremotelogin on");
    }

    /// <summary>Debian and Ubuntu name the unit `ssh`; most others name it `sshd`.</summary>
    [Fact]
    public void GenerateScript_Linux_HandlesEitherServiceName()
    {
        string script = SftpHostSetup.GenerateScript(Key, HostPlatform.Linux);

        script.Should().Contain("--now sshd");
        script.Should().Contain("--now ssh");
    }

    /// <summary>
    /// The key is interpolated into a shell string, so anything that could close it must be
    /// refused rather than escaped. SshKeyPairGenerator already guarantees this; the check is
    /// here because this function would be the one exploited if that guarantee ever lapsed.
    /// </summary>
    [Theory]
    [InlineData("ssh-rsa AAAA test\nrm -rf /")]
    [InlineData("ssh-rsa AAAA'; Remove-Item -Recurse C:\\ ;'")]
    [InlineData("ssh-rsa AAAA\"malicious\"")]
    public void GenerateScript_KeyContainingQuotesOrNewlines_IsRefused(string hostile)
    {
        Action act = () => SftpHostSetup.GenerateScript(hostile, HostPlatform.Windows);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GenerateScript_AGeneratedKeyIsAlwaysAccepted()
    {
        var pair = SshKeyPairGenerator.Generate("my machine");

        foreach (var platform in Enum.GetValues<HostPlatform>())
        {
            Action act = () => SftpHostSetup.GenerateScript(pair.PublicKeyLine, platform);
            act.Should().NotThrow();
        }
    }

    // ── Parsing the result ─────────────────────────────────────────────────

    private static string Block(
        string user = "Diviel",
        string home = "/C:/Users/Diviel",
        string fingerprint = "SHA256:abc123") =>
        $"""
        {SftpHostSetup.BeginMarker}
        user={user}
        home={home}
        fingerprint={fingerprint}
        {SftpHostSetup.EndMarker}
        """;

    [Fact]
    public void ParseResult_ReadsAllThreeFields()
    {
        var result = SftpHostSetup.ParseResult(Block());

        result.Should().NotBeNull();
        result!.Username.Should().Be("Diviel");
        result.HomePath.Should().Be("/C:/Users/Diviel");
        result.Fingerprint.Should().Be("SHA256:abc123");
    }

    /// <summary>
    /// The realistic paste. Nobody selects exactly the block — they grab the whole terminal,
    /// prompts and echoed command included.
    /// </summary>
    [Fact]
    public void ParseResult_IgnoresEverythingOutsideTheMarkers()
    {
        string messy = $"""
        PS C:\Users\Diviel> .\setup.ps1
        Path          : C:\
        Online        : True

        {Block()}

        Copy the block above, including both marker lines, back into Connapse.
        PS C:\Users\Diviel>
        """;

        var result = SftpHostSetup.ParseResult(messy);

        result.Should().NotBeNull();
        result!.Username.Should().Be("Diviel");
    }

    /// <summary>
    /// Terminals wrap, indent, and add carriage returns, and none of that is the operator's
    /// fault. Field names and values are both trimmed, so padding around the '=' is fine.
    /// </summary>
    [Fact]
    public void ParseResult_ToleratesWindowsLineEndingsAndStrayWhitespace()
    {
        string block = Block().Replace("\n", "\r\n").Replace("user=", "  user =  ");

        var result = SftpHostSetup.ParseResult(block);

        result.Should().NotBeNull();
        result!.Username.Should().Be("Diviel");
    }

    [Fact]
    public void ParseResult_ToleratesCarriageReturns()
    {
        SftpHostSetup.ParseResult(Block().Replace("\n", "\r\n"))!
            .Username.Should().Be("Diviel");
    }

    /// <summary>
    /// A fingerprint typed by hand often loses the prefix. Adding it back beats failing later,
    /// where the only symptom is a refused connection with no hint that the stored value was
    /// merely the wrong shape.
    /// </summary>
    [Fact]
    public void ParseResult_FingerprintWithoutThePrefix_GetsItBack()
    {
        SftpHostSetup.ParseResult(Block(fingerprint: "abc123"))!
            .Fingerprint.Should().Be("SHA256:abc123");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just some text the operator pasted by mistake")]
    public void ParseResult_WithoutABlock_IsNull(string? pasted)
    {
        SftpHostSetup.ParseResult(pasted).Should().BeNull();
    }

    /// <summary>
    /// Loose key=value lines with no markers must not be accepted — that would take a paste
    /// from somewhere else entirely and configure a connection out of it.
    /// </summary>
    [Fact]
    public void ParseResult_FieldsWithoutMarkers_IsNull()
    {
        SftpHostSetup.ParseResult("user=x\nhome=/y\nfingerprint=SHA256:z").Should().BeNull();
    }

    [Fact]
    public void ParseResult_MarkersInTheWrongOrder_IsNull()
    {
        SftpHostSetup.ParseResult(
            $"{SftpHostSetup.EndMarker}\nuser=x\nhome=/y\nfingerprint=z\n{SftpHostSetup.BeginMarker}")
            .Should().BeNull();
    }

    [Theory]
    [InlineData("user")]
    [InlineData("home")]
    [InlineData("fingerprint")]
    public void ParseResult_MissingAnyField_IsNull(string omit)
    {
        string block = string.Join('\n',
            Block().Split('\n').Where(l => !l.TrimStart().StartsWith(omit + "=")));

        SftpHostSetup.ParseResult(block).Should().BeNull();
    }

    /// <summary>
    /// The whole point of the round trip: these are the values Connapse cannot know, and the
    /// Windows home path is the one that would otherwise be written wrong by hand.
    /// </summary>
    [Fact]
    public void ParseResult_WindowsHomePath_KeepsItsLeadingSlash()
    {
        SftpHostSetup.ParseResult(Block(home: "/C:/Users/Diviel"))!
            .HomePath.Should().StartWith("/C:/");
    }
}
