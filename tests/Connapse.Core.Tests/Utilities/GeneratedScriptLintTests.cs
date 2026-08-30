using System.Diagnostics;
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

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
/// </remarks>
[Trait("Category", "Unit")]
public class GeneratedScriptLintTests
{
    /// <summary>Every generated script, named so a failure says which one.</summary>
    public static TheoryData<string, string> Scripts() => new()
    {
        { "aws-iam-user", AwsIamUserSetup.GenerateScript(null) },
        { "access-grants", AccessGrantsSetup.GenerateScript("us-west-1") },
        { "identity-center", IdentityCenterSetup.GenerateScript() },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void GeneratedScript_IsValidShell(string name, string script)
    {
        // -n parses without running anything, which is the only safe way to check a script whose
        // whole purpose is to create AWS resources.
        var (exitCode, output) = RunShell(script);

        exitCode.Should().Be(0,
            "the {0} script must parse as shell; bash said:{1}{2}", name, Environment.NewLine, output);
    }

    /// <summary>Parses <paramref name="script"/> with <c>bash -n</c> and reports what it said.</summary>
    /// <remarks>
    /// Written to a file in a temporary directory and named relatively, with that directory as the
    /// working directory. Two things forced that shape. Git Bash on Windows cannot open a path in
    /// <c>C:\</c> form, so an absolute name works on the CI runner and fails on every development
    /// machine; and feeding the script on stdin instead reported syntax errors on scripts that
    /// <c>bash -n</c> accepts from a file, so the pipe is not a faithful substitute for one.
    /// </remarks>
    private static (int ExitCode, string Output) RunShell(string script)
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
                // Found on PATH. Both supported platforms have it: CI runs on Ubuntu, and Windows
                // development uses Git Bash. A missing bash fails the test rather than skipping it,
                // because a lint that quietly passes when it did not run is worse than no lint.
                FileName = "bash",
                Arguments = $"-n {fileName}",
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Could not start bash.");

            string output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
