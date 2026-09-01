using Connapse.Core.Utilities;
using FluentAssertions;
using System.Diagnostics;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The command that finds an administrator's Identity Center groups and optionally makes one.
/// </summary>
[Trait("Category", "Unit")]
public class DirectoryGroupSetupTests
{
    private const string Store = "d-9067f4e3a1";
    private const string User = "c989c98e-e031-7070-201d-f27481dc7b0b";

    private static string Script(string? groupName = null) =>
        DirectoryGroupSetup.GenerateScript("us-west-1", Store, User, groupName);

    [Fact]
    public void GenerateScript_FirstRunWithoutAConnectedUser_ListsAllDirectoryGroups()
    {
        string script = DirectoryGroupSetup.GenerateScript("us-west-1", Store, null, null);

        script.Should().Contain("identitystore list-groups",
            "group discovery must work before the first person can connect through the SAML application");
        script.Should().NotContain("list-group-memberships-for-member",
            "listing available grant groups must not depend on a connected person's memberships");
    }

    [Fact]
    public void GenerateScript_WithNoGroupName_OnlyDiscovers()
    {
        // Discovery is the default because most directories synchronise their groups from an
        // identity provider, and those are the groups worth granting to. Creating one regardless
        // would add a group the provider does not know about.
        string script = Script();

        script.Should().Contain("identitystore list-groups");
        script.Should().Contain("GROUP_NAME=\"\"",
            "no name given means the create branch never runs");
    }

    [Fact]
    public void ParseResults_ReturnsEveryDiscoveredGroupForTheAdministratorToChoose()
    {
        string pasted = $$"""
            {{DirectoryGroupSetup.BeginMarker}}
            groupId=11111111-1111-1111-1111-111111111111
            groupName=Finance Readers
            groupId=22222222-2222-2222-2222-222222222222
            groupName=Engineering Readers
            {{DirectoryGroupSetup.EndMarker}}
            """;

        DirectoryGroupSetup.ParseResults(pasted).Should().Equal(
            ("11111111-1111-1111-1111-111111111111", "Finance Readers"),
            ("22222222-2222-2222-2222-222222222222", "Engineering Readers"));
    }

    [Fact]
    public void ParseResults_PreservesPunctuationInDiscoveredDisplayNames()
    {
        string pasted = $$"""
            {{DirectoryGroupSetup.BeginMarker}}
            groupId=11111111-1111-1111-1111-111111111111
            groupName=Finance's $Readers \ West
            {{DirectoryGroupSetup.EndMarker}}
            """;

        DirectoryGroupSetup.ParseResults(pasted).Should().Equal(
            ("11111111-1111-1111-1111-111111111111", "Finance's $Readers \\ West"));
    }

    [Fact]
    public void GenerateScript_ExplainsHowToContinueWhenNoGroupsExist()
    {
        DirectoryGroupSetup.GenerateScript("us-west-1", Store, null, null)
            .Should().Contain("No groups found. Enter a group name in Connapse to create one");
    }

    [Fact]
    public void GenerateScript_CreatesNoAccessGrant()
    {
        // The standing constraint of the whole feature. The grant command is printed for the
        // administrator to run, and running it is their decision, not this script's.
        string script = Script("Connapse Readers");

        // Not even printed as an example any more. A command here could not name a bucket --
        // that is a property of a connection -- and the version that guessed shipped YOUR-BUCKET,
        // which was duly run unreplaced and rejected by AWS. The connection builds the real one.
        script.Should().NotContain("create-access-grant");
        script.Should().NotContain("YOUR-BUCKET");

        script.Should().NotContain("s3control",
            "this step chooses a grantee; connections create grants for their own buckets");
    }

    [Fact]
    public void GeneratedScript_FirstRunOffersEveryDirectoryGroup()
    {
        const string firstId = "11111111-1111-1111-1111-111111111111";
        const string secondId = "22222222-2222-2222-2222-222222222222";
        string fakeAws = $$"""
            aws() {
              case "$*" in
                *"identitystore list-groups"*) printf '%s\n' '{{firstId}} {{secondId}}' ;;
                *"--group-id {{firstId}}"*) printf '%s\n' 'Finance Readers' ;;
                *"--group-id {{secondId}}"*) printf '%s\n' 'Engineering Readers' ;;
                *) printf 'Unexpected AWS call: %s\n' "$*" >&2; return 1 ;;
              esac
            }
            """;

        string output = RunBash(fakeAws + Environment.NewLine
            + DirectoryGroupSetup.GenerateScript("us-west-1", Store, null, null));

        DirectoryGroupSetup.ParseResults(output).Should().Equal(
            (firstId, "Finance Readers"),
            (secondId, "Engineering Readers"));
    }

    [Fact]
    public void GenerateScript_LooksTheGroupUpBeforeCreatingIt()
    {
        // Otherwise running it twice leaves two groups with the same display name, and the grant
        // points at whichever one the operator happened to copy.
        string script = Script("Connapse Readers");

        script.Should().Contain("get-group-id");
        script.Should().Contain("already exists");
    }

    [Fact]
    public void GenerateScript_AddsOnlyTheConnectedUser()
    {
        // Adding anyone else to a group that carries a grant is the privilege escalation AWS warns
        // about when Identity Store mutations are used alongside SCIM.
        string script = Script("Connapse Readers");

        script.Should().Contain("create-group-membership");
        script.Should().Contain($"USER_ID=\"{User}\"");
        script.Should().NotContain("list-users", "it never goes looking for other people");
    }

    [Fact]
    public void GenerateScript_SaysWhatADeniedCreateMeans()
    {
        // An organisation may block this with a service control policy precisely so the identity
        // provider stays authoritative. That is a decision, not a fault, and should not read as a
        // broken script.
        Script("Connapse Readers").Should().Contain("AccessDenied")
            .And.Contain("identity provider");
    }

    [Fact]
    public void GenerateScript_WarnsThatACreatedGroupDriftsFromTheProvider()
    {
        // SCIM reconciles deltas, so a group made here is never corrected by a later sync. The
        // administrator cannot see that from AWS and should meet it at the moment they cause it.
        Script("Connapse Readers").Should().Contain("identity provider will not manage it");
    }

    [Fact]
    public void GenerateScript_SurvivesBeingPastedIntoAnInteractiveShell()
    {
        // `set -e` and a bare `exit` end the session rather than the script when pasted, which is
        // how CloudShell disconnects part-way through instead of reporting a problem.
        var commands = Script("Connapse Readers").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        commands.Should().NotContain(l => l == "set -e" || l.StartsWith("set -e "));
        commands.Should().NotContain(l => l == "exit 1" || l == "exit");
    }

    [Theory]
    [InlineData("Connapse Readers", "Connapse Readers")]
    [InlineData("  Data Team  ", "Data Team")]
    [InlineData(null, "")]
    [InlineData("", "")]
    // Reserved by Identity Center, which refuses them for users and groups alike.
    [InlineData("Administrator", "")]
    [InlineData("awsadministrators", "")]
    // Would end the double-quoted assignment early, or expand into something else entirely.
    [InlineData("Readers\"; rm -rf /", "")]
    [InlineData("$(id)", "")]
    [InlineData("back`tick`", "")]
    public void SanitiseGroupName_KeepsOnlyANameThatIsSafeAndAccepted(string? given, string expected)
    {
        DirectoryGroupSetup.SanitiseGroupName(given).Should().Be(expected);
    }

    [Fact]
    public void SanitiseGroupName_RejectsAPastedParagraph()
    {
        DirectoryGroupSetup.SanitiseGroupName(new string('a', 129)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("d-9067f4e3a1", "d-9067f4e3a1")]
    [InlineData("c989c98e-e031-7070-201d-f27481dc7b0b", "c989c98e-e031-7070-201d-f27481dc7b0b")]
    [InlineData("1234567890-c989c98e-e031-7070-201d-f27481dc7b0b",
                "1234567890-c989c98e-e031-7070-201d-f27481dc7b0b")]
    [InlineData(null, "")]
    [InlineData("d-9067f4e3a1; rm -rf /", "")]
    [InlineData("$STORE", "")]
    public void SanitiseId_KeepsOnlyWhatCouldBeAnIdentityStoreOrUserId(string? given, string expected)
    {
        // Both forms are machine-generated, so an allowlist of their characters never refuses a
        // real value while rejecting anything that could carry shell syntax.
        DirectoryGroupSetup.SanitiseId(given).Should().Be(expected);
    }

    [Fact]
    public void GenerateScript_WithNothingDiscovered_StillParsesAndSaysSo()
    {
        // The state before anybody has connected: no user to list groups for. The script must still
        // be runnable, because creating the group ahead of the first sign-in is a reasonable order.
        string script = DirectoryGroupSetup.GenerateScript("us-west-1", Store, null, "Connapse Readers");

        script.Should().Contain("USER_ID=\"\"");
        script.Should().Contain("create-group");
    }

    private static string RunBash(string script)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"connapse-groups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "script.sh"), script.Replace("\r\n", "\n"));

        try
        {
            string bash = OperatingSystem.IsWindows()
                ? @"C:\Program Files\Git\bin\bash.exe"
                : "bash";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = bash,
                Arguments = "script.sh",
                WorkingDirectory = directory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Could not start Bash.");

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, output);
            return output;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
