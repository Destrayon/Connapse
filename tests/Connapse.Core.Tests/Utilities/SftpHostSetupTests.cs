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
        string user = "jsmith",
        string home = "/C:/Users/jsmith",
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
        result!.Username.Should().Be("jsmith");
        result.HomePath.Should().Be("/C:/Users/jsmith");
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
        PS C:\Users\jsmith> .\setup.ps1
        Path          : C:\
        Online        : True

        {Block()}

        Copy the block above, including both marker lines, back into Connapse.
        PS C:\Users\jsmith>
        """;

        var result = SftpHostSetup.ParseResult(messy);

        result.Should().NotBeNull();
        result!.Username.Should().Be("jsmith");
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
        result!.Username.Should().Be("jsmith");
    }

    [Fact]
    public void ParseResult_ToleratesCarriageReturns()
    {
        SftpHostSetup.ParseResult(Block().Replace("\n", "\r\n"))!
            .Username.Should().Be("jsmith");
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

    /// <summary>
    /// The identifying fields only. The fingerprint used to be required too, which dead-ended the
    /// flow when it could not be read — see
    /// <see cref="ParseResult_WithoutAFingerprint_StillParses"/>.
    /// </summary>
    [Theory]
    [InlineData("user")]
    [InlineData("home")]
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
        SftpHostSetup.ParseResult(Block(home: "/C:/Users/jsmith"))!
            .HomePath.Should().StartWith("/C:/");
    }
    // ── The installed key is restricted (Codex review on #405) ─────────────

    /// <summary>
    /// A bare entry grants everything the account can do — interactive shell, port forwarding,
    /// agent forwarding. Connapse only reads files, and its path confinement is an application
    /// rule: it bounds what Connapse does with the key, not what the key can do. Anyone holding
    /// the stored private half is not bound by it at all.
    /// </summary>
    [Theory]
    [InlineData(HostPlatform.Windows)]
    [InlineData(HostPlatform.MacOS)]
    [InlineData(HostPlatform.Linux)]
    public void GenerateScript_InstallsTheKeyRestrictedToSftp(HostPlatform platform)
    {
        string script = SftpHostSetup.GenerateScript(Key, platform);

        script.Should().Contain("restrict,", "restrict disables PTY, port, agent and X11 forwarding");
        script.Should().Contain("""command="internal-sftp" """.TrimEnd(),
            "a forced command replaces whatever the client asks for, so a shell request gets SFTP");
        script.Should().Contain($"restrict,command=\"internal-sftp\" {Key}",
            "the restrictions must prefix the key on the same authorized_keys line");
    }

    [Theory]
    [InlineData(HostPlatform.Windows)]
    [InlineData(HostPlatform.MacOS)]
    [InlineData(HostPlatform.Linux)]
    public void GenerateScript_NeverInstallsABareKey(HostPlatform platform)
    {
        string script = SftpHostSetup.GenerateScript(Key, platform);

        // The key must never appear without its restrictions immediately before it.
        foreach (int index in AllIndexesOf(script, Key))
        {
            script[..index].Should().EndWith(SftpHostSetup.KeyRestrictions,
                "an unrestricted copy of the key would defeat the restricted one");
        }
    }

    private static IEnumerable<int> AllIndexesOf(string haystack, string needle)
    {
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            yield return i;
        }
    }

    // ── A missing fingerprint must not dead-end the flow ───────────────────

    /// <summary>
    /// Reading the fingerprint needs elevation and can still fail, by which point sshd is running
    /// and the key is installed. Requiring one here left the operator with a configured host, an
    /// authorized key, and no way to finish or undo.
    /// </summary>
    [Fact]
    public void ParseResult_WithoutAFingerprint_StillParses()
    {
        string block = $"""
        {SftpHostSetup.BeginMarker}
        user=jsmith
        home=/C:/Users/jsmith
        fingerprint=
        {SftpHostSetup.EndMarker}
        """;

        var result = SftpHostSetup.ParseResult(block);

        result.Should().NotBeNull();
        result!.Username.Should().Be("jsmith");
        result.Fingerprint.Should().BeEmpty("blank means trust on first use, which is defined behaviour");
    }

    [Fact]
    public void ParseResult_FingerprintLineAbsentEntirely_StillParses()
    {
        string block = $"""
        {SftpHostSetup.BeginMarker}
        user=jsmith
        home=/C:/Users/jsmith
        {SftpHostSetup.EndMarker}
        """;

        SftpHostSetup.ParseResult(block)!.Fingerprint.Should().BeEmpty();
    }

    /// <summary>
    /// The identifying fields are still required: without a username or home path there is no
    /// connection to build, and guessing either would point somewhere wrong.
    /// </summary>
    [Theory]
    [InlineData("user")]
    [InlineData("home")]
    public void ParseResult_MissingAnIdentifyingField_IsStillNull(string omit)
    {
        string block = string.Join('\n',
            $"""
            {SftpHostSetup.BeginMarker}
            user=jsmith
            home=/C:/Users/jsmith
            fingerprint=SHA256:abc
            {SftpHostSetup.EndMarker}
            """.Split('\n').Where(l => !l.TrimStart().StartsWith(omit + "=")));

        SftpHostSetup.ParseResult(block).Should().BeNull();
    }

    [Fact]
    public void GenerateScript_SaysSoWhenTheFingerprintCouldNotBeRead()
    {
        SftpHostSetup.GenerateScript(Key, HostPlatform.Windows)
            .Should().Contain("could not be read",
                "a silent empty fingerprint would look like the script half-worked");
    }

    [Fact]
    public void GenerateScript_Windows_ScopesTheFirewallRuleToLocalSubnets()
    {
        // An unscoped New-NetFirewallRule accepts port 22 from any address on any profile —
        // the rest of a hotel network, and the internet on any host whose router forwards it.
        // Nothing about indexing local files needs that, and the operator is not being asked.
        string script = SftpHostSetup.GenerateScript(Key, HostPlatform.Windows);

        script.Should().Contain("-RemoteAddress LocalSubnet",
            "the rule must not accept port 22 from arbitrary networks");
        script.Should().Contain("New-NetFirewallRule");
    }

    [Theory]
    [InlineData(HostPlatform.Windows)]
    [InlineData(HostPlatform.Linux)]
    [InlineData(HostPlatform.MacOS)]
    public void GenerateScript_ReportsWhetherTheHostStillAcceptsPasswords(HostPlatform platform)
    {
        // The restrictions on Connapse's key bind that key alone. Turning on sshd exposes every
        // other account too, and by password if the host allows it — which the operator cannot
        // know unless the script says so.
        string script = SftpHostSetup.GenerateScript(Key, platform);

        script.Should().Contain("PasswordAuthentication",
            "enabling SSH without naming the host's password policy hides half the change");
        script.Should().Contain("accepts SSH logins by password");
    }

    // ── Setting up a server the operator already reaches over SSH (#407) ───────────

    /// <summary>
    /// The five things that only make sense when the SSH server does not exist yet. A server
    /// the operator is running this on is already serving SSH, so each of these would either
    /// do nothing or change something they did not ask to have changed.
    /// </summary>
    public static TheoryData<string> PrivilegedSteps => new()
    {
        "Add-WindowsCapability",
        "Set-Service",
        "Start-Service",
        "New-NetFirewallRule",
    };

    [Theory]
    [MemberData(nameof(PrivilegedSteps))]
    public void GenerateScript_Remote_DoesNotStandUpAnSshServer(string step)
    {
        string remote = SftpHostSetup.GenerateScript(Key, HostPlatform.Windows, SftpSetupTarget.RemoteServer);

        remote.Should().NotContain(step,
            "the operator is already connected over SSH, so the server is running and the port is open");

        SftpHostSetup.GenerateScript(Key, HostPlatform.Windows, SftpSetupTarget.ThisComputer)
            .Should().Contain(step, "the local variant is the one that has to create all this");
    }

    [Theory]
    [InlineData(HostPlatform.Linux)]
    [InlineData(HostPlatform.MacOS)]
    public void GenerateScript_RemoteUnix_NeedsNoPrivilege(HostPlatform platform)
    {
        // Writing one line into the operator's own ~/.ssh/authorized_keys is not privileged
        // work, and asking for sudo on a machine they may not own is a real obstacle.
        string remote = SftpHostSetup.GenerateScript(Key, platform, SftpSetupTarget.RemoteServer);

        remote.Should().NotContain("sudo");
        remote.Should().Contain("authorized_keys", "the key still has to be installed");
    }

    [Theory]
    [InlineData(HostPlatform.Windows)]
    [InlineData(HostPlatform.Linux)]
    [InlineData(HostPlatform.MacOS)]
    public void GenerateScript_Remote_StillInstallsTheKeyRestricted(HostPlatform platform)
    {
        // Everything the local variant does about *authorisation* is unchanged. Only the
        // steps that bring a server into existence are dropped.
        string remote = SftpHostSetup.GenerateScript(Key, platform, SftpSetupTarget.RemoteServer);

        remote.Should().Contain(SftpHostSetup.KeyRestrictions.Trim(),
            "an unrestricted key would grant a shell on someone else's server");
        remote.Should().Contain(SftpHostSetup.BeginMarker);
        remote.Should().Contain("fingerprint=", "verified first use is the whole point of the round trip");
        remote.Should().Contain("PasswordAuthentication",
            "a remote host is likelier to be internet-facing, not less");
    }

    [Fact]
    public void GenerateScript_RemoteWindows_AsksForElevationOnlyWhenTheAccountIsAnAdministrator()
    {
        // The local script demands elevation up front because installing the SSH server needs
        // it regardless. Remotely only one branch does — the machine-wide file an administrator
        // account's keys go in — so demanding it unconditionally would put a UAC prompt in
        // front of a step that writes one line into the operator's own home directory.
        string remote = SftpHostSetup.GenerateScript(Key, HostPlatform.Windows, SftpSetupTarget.RemoteServer);

        remote.Should().Contain("if ($isAdmin -and -not $elevated)",
            "elevation is demanded only once membership is known");
        remote.Should().Contain("administrators_authorized_keys",
            "which is the file that makes elevation necessary at all");

        int guard = remote.IndexOf("$isAdmin -and -not $elevated", StringComparison.Ordinal);
        int membership = remote.IndexOf("Get-LocalGroupMember", StringComparison.Ordinal);
        guard.Should().BeGreaterThan(membership, "the question has to be asked before it is acted on");
    }

    [Fact]
    public void GenerateScript_RemoteWindows_RefusesToGuessMembershipItCouldNotRead()
    {
        // UAC filters the Administrators SID out of an unelevated token, so falling back to a
        // token check would answer "not an administrator" for an account that is one — and the
        // key would go in a file sshd never reads, failing with nothing to explain it.
        string remote = SftpHostSetup.GenerateScript(Key, HostPlatform.Windows, SftpSetupTarget.RemoteServer);

        remote.Should().Contain("Could not read the Administrators group",
            "refusing beats guessing wrong in the direction that fails silently");
    }

    [Fact]
    public void GenerateScript_DefaultsToThisComputer()
    {
        // The two-argument form predates the target and is still what the local flow calls.
        SftpHostSetup.GenerateScript(Key, HostPlatform.Windows)
            .Should().Be(SftpHostSetup.GenerateScript(Key, HostPlatform.Windows, SftpSetupTarget.ThisComputer));
    }

    [Theory]
    [InlineData(HostPlatform.Linux, SftpSetupTarget.ThisComputer)]
    [InlineData(HostPlatform.Linux, SftpSetupTarget.RemoteServer)]
    [InlineData(HostPlatform.MacOS, SftpSetupTarget.ThisComputer)]
    [InlineData(HostPlatform.MacOS, SftpSetupTarget.RemoteServer)]
    public void GenerateScript_Unix_NeverPassesAMarkerAsAPrintfFormatString(
        HostPlatform platform, SftpSetupTarget target)
    {
        // Both markers start with dashes, and printf reads a leading '-' as an option. As the
        // format string the end marker made bash print "invalid option" instead of the marker,
        // so the block came back unterminated and ParseResult refused it — with the host already
        // configured and the key already installed. Asserting the marker merely *appears* in the
        // script does not catch this: it appeared, as the thing that failed to print.
        string script = SftpHostSetup.GenerateScript(Key, platform, target);

        foreach (string line in script.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("printf ", StringComparison.Ordinal)) continue;

            string format = trimmed["printf ".Length..].TrimStart();
            format = format.StartsWith('\'') ? format[1..] : format;

            format.Should().NotStartWith("-",
                $"printf would read this as an option, not text: {trimmed}");
        }
    }

    [Theory]
    [InlineData(HostPlatform.Linux)]
    [InlineData(HostPlatform.MacOS)]
    public void GenerateScript_Unix_EmitsBothMarkersThroughAStringPlaceholder(HostPlatform platform)
    {
        // The positive half of the rule above: the markers are arguments, which is what makes
        // them survive their own leading dashes.
        string script = SftpHostSetup.GenerateScript(Key, platform, SftpSetupTarget.RemoteServer);

        script.Should().Contain($"'{SftpHostSetup.BeginMarker}'");
        script.Should().Contain($"'{SftpHostSetup.EndMarker}'");
    }

    // ── Addresses the host reports about itself ───────────────────────────────────

    [Fact]
    public void ParseResult_ShortNameAndFqdn_OffersTheFqdnFirstAndTheShortNameLast()
    {
        // The short name is the one the operator recognises and the one likeliest to fail: their
        // workstation resolves it only because its DNS client appends a search suffix, and a
        // container has none. So it stays in the list, at the bottom.
        string block = $"""
            {SftpHostSetup.BeginMarker}
            user=jsmith
            home=/home/jsmith
            fingerprint=SHA256:abc
            host=jsmithserver
            fqdn=jsmithserver.attlocal.net
            addresses=192.168.1.194
            {SftpHostSetup.EndMarker}
            """;

        SftpHostSetup.ParseResult(block)!.Addresses
            .Should().Equal("jsmithserver.attlocal.net", "192.168.1.194", "jsmithserver");
    }

    [Fact]
    public void ParseResult_FqdnEqualsTheShortName_IsNotOfferedTwice()
    {
        // hostname -f falls back to the short name on a host with no domain configured.
        string block = $"""
            {SftpHostSetup.BeginMarker}
            user=jsmith
            home=/home/jsmith
            fingerprint=SHA256:abc
            host=fileserver
            fqdn=fileserver
            addresses=192.168.1.50
            {SftpHostSetup.EndMarker}
            """;

        SftpHostSetup.ParseResult(block)!.Addresses.Should().Equal("fileserver", "192.168.1.50");
    }

    [Fact]
    public void ParseResult_NoFqdnReported_OffersAddressesAheadOfTheShortName()
    {
        // With no fully-qualified name to lead with, an address beats the short name: it needs
        // no resolver at all, where the short name needs one that appends a search suffix.
        string block = $"""
            {SftpHostSetup.BeginMarker}
            user=jsmith
            home=/home/jsmith
            fingerprint=SHA256:abc
            host=fileserver
            addresses=192.168.1.50,10.8.0.3
            {SftpHostSetup.EndMarker}
            """;

        SftpHostSetup.ParseResult(block)!.Addresses
            .Should().Equal("192.168.1.50", "10.8.0.3", "fileserver");
    }

    [Fact]
    public void ParseResult_HostAlreadyAmongTheAddresses_IsNotOfferedTwice()
    {
        string block = $"""
            {SftpHostSetup.BeginMarker}
            user=jsmith
            home=/home/jsmith
            fingerprint=SHA256:abc
            host=192.168.1.50
            addresses=192.168.1.50,10.8.0.3
            {SftpHostSetup.EndMarker}
            """;

        SftpHostSetup.ParseResult(block)!.Addresses.Should().Equal("192.168.1.50", "10.8.0.3");
    }

    [Fact]
    public void ParseResult_NoAddressesReported_StillParses()
    {
        // An older setup command sent neither field, and a machine whose address lookup found
        // nothing still installed the key correctly. Refusing the block would strand a host that
        // is already configured — the same trap the optional fingerprint avoids.
        string block = $"""
            {SftpHostSetup.BeginMarker}
            user=jsmith
            home=/home/jsmith
            fingerprint=SHA256:abc
            {SftpHostSetup.EndMarker}
            """;

        var result = SftpHostSetup.ParseResult(block);

        result.Should().NotBeNull();
        result!.Addresses.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HostPlatform.Linux)]
    [InlineData(HostPlatform.MacOS)]
    [InlineData(HostPlatform.Windows)]
    public void GenerateScript_ReportsTheMachinesOwnNameAndAddresses(HostPlatform platform)
    {
        string script = SftpHostSetup.GenerateScript(Key, platform, SftpSetupTarget.RemoteServer);

        script.Should().Contain("host=");
        script.Should().Contain("addresses=");
    }
}
