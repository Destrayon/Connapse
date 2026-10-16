using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Connapse.CustomSource.Conformance;

/// <summary>
/// Hooks that run shell commands the user supplies. Inputs arrive as environment variables:
/// <c>CONNAPSE_SCOPE</c> and <c>CONNAPSE_DOCUMENT_ID</c> for the change commands; <c>CONNAPSE_LINK_URL</c>,
/// <c>CONNAPSE_LINK_TOKEN</c> and <c>CONNAPSE_RETURN_URL</c> for the sign-in command, which prints either the
/// code or the URL the middle man redirected to. A missing command makes its hook unsupported; a command that
/// exits non-zero fails the check that called it.
/// </summary>
public sealed partial class CommandHooks(string? createCommand, string? deleteCommand, string? signInCommand) : IConformanceHooks
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public async Task<bool> CreateDocumentAsync(string scope, CancellationToken ct)
    {
        if (createCommand is null) return false;
        await RunAsync(createCommand, new() { ["CONNAPSE_SCOPE"] = scope }, ct);
        return true;
    }

    public async Task<bool> DeleteDocumentAsync(string scope, string documentId, CancellationToken ct)
    {
        if (deleteCommand is null) return false;
        await RunAsync(deleteCommand, new() { ["CONNAPSE_SCOPE"] = scope, ["CONNAPSE_DOCUMENT_ID"] = documentId }, ct);
        return true;
    }

    public async Task<string?> CompleteSignInAsync(Uri linkUrl, string linkToken, Uri returnUrl, CancellationToken ct)
    {
        if (signInCommand is null) return null;
        string output = (await RunAsync(signInCommand, new()
        {
            ["CONNAPSE_LINK_URL"] = linkUrl.ToString(),
            ["CONNAPSE_LINK_TOKEN"] = linkToken,
            ["CONNAPSE_RETURN_URL"] = returnUrl.ToString(),
        }, ct)).Trim();

        // Either the bare code or the whole redirect URL; take the code from its query in the second case.
        Match code = CodeParameter().Match(output);
        return code.Success ? Uri.UnescapeDataString(code.Groups[1].Value) : output.Split('\n')[^1].Trim();
    }

    private static async Task<string> RunAsync(string command, Dictionary<string, string> environment, CancellationToken ct)
    {
        bool windows = OperatingSystem.IsWindows();
        ProcessStartInfo start = new(windows ? "cmd.exe" : "/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (windows)
        {
            // cmd.exe reads its own command line: /s keeps everything inside the outer quotes as written,
            // where an argument list would escape the command's quotes in a way cmd does not understand.
            start.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }
        foreach ((string name, string value) in environment) start.Environment[name] = value;

        using Process process = Process.Start(start) ?? throw new CheckFailedException($"Could not start: {command}");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new CheckFailedException($"The hook command did not finish within {CommandTimeout.TotalMinutes:0} minutes: {command}");
        }

        if (process.ExitCode != 0)
            throw new CheckFailedException($"The hook command exited with {process.ExitCode}: {(await stderr).Trim()}");
        return await stdout;
    }

    [GeneratedRegex(@"[?&]code=([^&\s]+)")]
    private static partial Regex CodeParameter();
}
