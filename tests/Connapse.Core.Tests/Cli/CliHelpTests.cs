using System.Diagnostics;
using FluentAssertions;

namespace Connapse.Core.Tests.Cli;

public class CliHelpTests
{
    private static readonly string CliProjectPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Connapse.CLI"));

    private async Task<(int ExitCode, string Output)> RunCliAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{CliProjectPath}\" --no-build -- {string.Join(' ', args)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout + stderr);
    }

    [Fact]
    public async Task Help_Flag_Shows_Usage()
    {
        var (exitCode, output) = await RunCliAsync("--help");

        exitCode.Should().Be(0);
        output.Should().Contain("Usage: connapse <command>");
    }

    [Fact]
    public async Task Help_ShortFlag_Shows_Usage()
    {
        var (exitCode, output) = await RunCliAsync("-h");

        exitCode.Should().Be(0);
        output.Should().Contain("Usage: connapse <command>");
    }

    [Fact]
    public async Task Subcommand_Help_Shows_Subcommand_Usage()
    {
        var (exitCode, output) = await RunCliAsync("auth", "--help");

        exitCode.Should().Be(0);
        output.Should().Contain("auth login");
    }

    [Fact]
    public async Task Subcommand_ShortHelp_Shows_Subcommand_Usage()
    {
        var (exitCode, output) = await RunCliAsync("container", "-h");

        exitCode.Should().Be(0);
        output.Should().Contain("container create");
    }
}
