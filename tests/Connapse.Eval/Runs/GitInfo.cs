using System.Diagnostics;

namespace Connapse.Eval.Runs;

public static class GitInfo
{
    public static (string Sha, bool Dirty) Read(string repoRoot)
    {
        string? sha = Run(repoRoot, "rev-parse HEAD");
        string? status = Run(repoRoot, "status --porcelain");
        return (sha?.Trim() ?? "unknown", status is null || status.Trim().Length > 0);
    }

    private static string? Run(string workingDirectory, string arguments)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
