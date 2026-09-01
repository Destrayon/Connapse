using System.Diagnostics;
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// Every script Connapse hands an administrator to paste, checked by a shell rather than by
/// string matching.
/// </summary>
/// <remarks>
/// These scripts are C# string literals that nothing compiles, so a syntax error in one is invisible
/// until somebody pastes it into CloudShell. That has now happened twice: a heredoc that put an
/// interactive shell into continuation mode, and a stray quote after a closing <c>fi</c> that left
/// the shell waiting for a string that never arrived. Both presented as a paste that hung with no
/// error.
/// <para>
/// The per-script assertions elsewhere in this project — no <c>set -e</c>, no bare <c>exit</c>, no
/// line continuations — encode rules a parser cannot know. This is the other half: whether the
/// thing is a valid script at all. Hand-written substring checks kept being written after each new
/// way of breaking it was discovered, which is a poor substitute for asking bash.
/// </para>
/// <para>
/// Every generated variant is checked, not one sample per generator. The scripts differ by
/// substitution, and a substitution is exactly what can turn a valid script into an invalid one —
/// so linting only the shape that happens to be listed first would miss the case this is for.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class GeneratedScriptLintTests(ITestOutputHelper output)
{
    /// <summary>
    /// Set in CI. Decides whether a missing ShellCheck is a failure or a notice, because a
    /// development machine on Windows has no reason to have it and CI always does.
    /// </summary>
    private const string CiVariable = "CI";

    /// <summary>
    /// Every script a generator can produce, named so a failure says which one, and covering each
    /// substitution that changes the result.
    /// </summary>
    public static TheoryData<string, string> Scripts() => new()
    {
        { "aws-iam-user/default-name", AwsIamUserSetup.GenerateScript(null) },

        // The name is an allowlist away from arbitrary text, and the allowlist keeps the characters
        // that survive IAM. This asserts the ones it keeps are also ones a shell is happy with.
        { "aws-iam-user/awkward-name", AwsIamUserSetup.GenerateScript("Team+Reader=1,dev.ops@x-y") },

        { "access-grants/region", AccessGrantsSetup.GenerateScript("eu-central-1") },

        // The branch where discovery has not run yet, so the script must still parse in order to
        // reach the line that says so.
        { "access-grants/no-region", AccessGrantsSetup.GenerateScript(null) },

        // Rejected by the allowlist, so this is the no-region script — asserted here rather than
        // assumed, since the whole point of the allowlist is that this text never reaches the shell.
        { "access-grants/rejected-region", AccessGrantsSetup.GenerateScript("us-east-1\"; rm -rf /") },

        { "identity-center", IdentityCenterSetup.GenerateScript() },

        // Both branches: discovery alone, and the create path a group name opens up.
        { "directory-group/discover",
            DirectoryGroupSetup.GenerateScript("us-west-1", "d-9067f4e3a1", "c989c98e-e031", null) },
        { "directory-group/create",
            DirectoryGroupSetup.GenerateScript("us-west-1", "d-9067f4e3a1", "c989c98e-e031", "Connapse Readers") },

        // One bucket and several: with a single entry a quoted word reads as a command being run,
        // which ShellCheck flags and a bare list would have shipped.
        { "access-grant/one-bucket",
            AccessGrantScript.GenerateScript("us-west-1", ["reports"], "69f9f9de-00f1", true) },
        { "access-grant/several-buckets",
            AccessGrantScript.GenerateScript("us-west-1", ["a", "b/team"], "69f9f9de-00f1", true) },
        { "access-grant/nothing-chosen",
            AccessGrantScript.GenerateScript(null, [], null, false) },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void GeneratedScript_ParsesAsShell(string name, string script)
    {
        // -n parses without running anything, which is the only safe way to check a script whose
        // whole purpose is to create AWS resources.
        var (exitCode, shellOutput) = Run("bash", "-n", script);

        exitCode.Should().Be(0,
            "the {0} script must parse as shell; bash said:{1}{2}", name, Environment.NewLine, shellOutput);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void GeneratedScript_PassesShellCheck(string name, string script)
    {
        // The half `bash -n` cannot do. A script can parse perfectly and still be wrong: an unquoted
        // expansion that word-splits on a path with a space, a comparison that is always true, a
        // variable read one branch before it is set. Those are the bugs that reach an administrator
        // as "it ran and did nothing".
        if (!IsOnPath("shellcheck"))
        {
            // A lint that quietly passes when it did not run is worse than no lint, so this is loud
            // where it can be enforced and merely stated where it cannot. Windows development
            // machines have no ShellCheck and were never told to get one; the Ubuntu runner ships
            // with it, and gates the pull request.
            Environment.GetEnvironmentVariable(CiVariable).Should().BeNullOrEmpty(
                "CI must have ShellCheck available, and it was not found on PATH");

            output.WriteLine($"ShellCheck is not installed, so {name} was only parsed, not linted.");
            return;
        }

        // -s bash, because there is no shebang to read one from and these are pasted into
        // CloudShell, whose shell is bash. Told to assume sh instead, ShellCheck reports the
        // bash-only constructs the scripts deliberately use.
        var (exitCode, shellOutput) = Run("shellcheck", "-s bash", script);

        exitCode.Should().Be(0,
            "the {0} script must be clean under ShellCheck; it said:{1}{2}",
            name, Environment.NewLine, shellOutput);
    }

    /// <summary>Whether <paramref name="tool"/> can be started at all.</summary>
    private static bool IsOnPath(string tool)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = ToolPath(tool),
                Arguments = "--version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });

            probe?.WaitForExit();
            return probe is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="script"/> to a file, runs <paramref name="tool"/> over it, and reports
    /// what it said.
    /// </summary>
    /// <remarks>
    /// Written to a file in a temporary directory and named relatively, with that directory as the
    /// working directory. Two things forced that shape. Git Bash on Windows cannot open a path in
    /// <c>C:\</c> form, so an absolute name works on the CI runner and fails on every development
    /// machine; and feeding the script on stdin instead reported syntax errors on scripts that
    /// <c>bash -n</c> accepts from a file, so the pipe is not a faithful substitute for one.
    /// </remarks>
    private static (int ExitCode, string Output) Run(string tool, string arguments, string script)
    {
        string folder = $"connapse-lint-{Guid.NewGuid():N}";
        string directory = Path.Combine(Path.GetTempPath(), folder);
        Directory.CreateDirectory(directory);

        // Relative to the temp root rather than to the script's own folder. Bash holds its working
        // directory open for as long as it runs, and Windows will not delete a directory something
        // has open — so pointing it at the folder being cleaned up fails the test on tidying rather
        // than on anything to do with the script.
        string fileName = $"{folder}/script.sh";

        try
        {
            File.WriteAllText(Path.Combine(directory, "script.sh"), script);

            using var process = Process.Start(new ProcessStartInfo
            {
                // Found on PATH. A missing bash fails the test rather than skipping it: CI runs on
                // Ubuntu and Windows development uses Git Bash, so both have one.
                FileName = ToolPath(tool),
                Arguments = $"{arguments} {fileName}",
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException($"Could not start {tool}.");

            string result = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ToolPath(string tool)
    {
        if (OperatingSystem.IsWindows() && tool == "bash")
        {
            const string gitBash = @"C:\Program Files\Git\bin\bash.exe";
            if (File.Exists(gitBash))
                return gitBash;
        }

        return tool;
    }
}
