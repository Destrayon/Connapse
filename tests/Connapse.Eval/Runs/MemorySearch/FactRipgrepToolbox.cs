using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Connapse.Core;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// The ripgrep arm of the memory-agent suite (CON-322): the same one-fact-per-line file as <see cref="FactGrepToolbox"/>,
/// searched by running the real <c>rg</c> binary with the options of Claude Code's own ripgrep-backed Grep tool (case,
/// context lines, only-matching, multiline, count, head_limit and offset), and read by line range with <c>read</c>.
/// The file is written again on every call, so a write through <paramref name="writes"/> shows up on the next one.
/// The binary is <c>rg</c> on the PATH, or the one named by <c>CONNAPSE_EVAL_RG</c>.
/// </summary>
public sealed class FactRipgrepToolbox(
    Func<CancellationToken, Task<IReadOnlyList<MemoryFact>>> facts, IToolbox? writes = null, TimeProvider? clock = null) : IToolbox
{
    public const string RgTool = "rg";

    /// <summary>Claude Code's Grep tool shows at most this many output lines unless told otherwise.</summary>
    public const int DefaultHeadLimit = 250;

    /// <summary>The rg binary: <c>CONNAPSE_EVAL_RG</c> when set, else <c>rg</c> on the PATH.</summary>
    public static string Binary => Environment.GetEnvironmentVariable("CONNAPSE_EVAL_RG") is { Length: > 0 } rg ? rg : "rg";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private static JsonElement Schema(object o) => JsonSerializer.SerializeToElement(o);

    public static readonly ToolSpec Rg = new(
        RgTool,
        "Search the memory file with ripgrep (rg). The pattern is a ripgrep (Rust) regular expression. Output lines are "
        + $"\"line:text\" for matches and \"line-text\" for context lines. Line 1 is the header: {FactGrepToolbox.Header}. "
        + "Each other line is one fact that is true now or has ended.",
        new Dictionary<string, JsonElement>
        {
            ["pattern"] = Schema(new { type = "string", description = "The regular expression to search for" }),
            ["-i"] = Schema(new { type = "boolean", description = "Case insensitive search (rg -i)" }),
            ["-A"] = Schema(new { type = "integer", description = "Lines to show after each match (rg -A)" }),
            ["-B"] = Schema(new { type = "integer", description = "Lines to show before each match (rg -B)" }),
            ["-C"] = Schema(new { type = "integer", description = "Lines to show before and after each match (rg -C)" }),
            ["-o"] = Schema(new { type = "boolean", description = "Print only the matched parts of each line (rg -o)" }),
            ["multiline"] = Schema(new { type = "boolean", description = "Let patterns span lines; . matches newlines (rg -U --multiline-dotall)" }),
            ["output_mode"] = Schema(new { type = "string", @enum = new[] { "content", "count" }, description = "\"content\" (default) shows matching lines; \"count\" shows how many lines match" }),
            ["head_limit"] = Schema(new { type = "integer", description = $"Show only the first N output lines. Default {DefaultHeadLimit}; 0 for all" }),
            ["offset"] = Schema(new { type = "integer", description = "Skip the first N output lines before applying head_limit. Default 0" }),
        },
        ["pattern"]);

    public IReadOnlyList<ToolSpec> Tools { get; } = [Rg, FactGrepToolbox.Read, .. writes?.Tools ?? []];

    public async Task<string> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (name is RgTool or FactGrepToolbox.ReadTool)
        {
            List<string> lines = FactGrepToolbox.Render(await facts(ct), _clock.GetUtcNow());
            return name == RgTool ? await SearchAsync(lines, input, ct) : FactGrepToolbox.ReadLines(lines, input);
        }
        return writes is not null && writes.Tools.Any(t => t.Name == name)
            ? await writes.CallAsync(name, input, ct)
            : $"Error: Unknown tool '{name}'.";
    }

    /// <summary>The rg arguments for a call, or an error message. The file path is appended by the caller.</summary>
    public static (List<string>? Args, string? Error) Arguments(IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("pattern", out JsonElement p) || p.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(p.GetString()))
            return (null, "Error: Missing required argument 'pattern'.");
        List<string> args = ["--no-config", "--color=never", "--no-heading", "--no-filename"];
        bool count = Text(input, "output_mode") == "count";
        if (count)
            args.Add("--count");
        else
            args.Add("--line-number");
        if (Flag(input, "-i"))
            args.Add("--ignore-case");
        if (Flag(input, "-o"))
            args.Add("--only-matching");
        if (Flag(input, "multiline"))
            args.AddRange(["--multiline", "--multiline-dotall"]);
        foreach ((string key, string option) in (ReadOnlySpan<(string, string)>)[("-A", "--after-context"), ("-B", "--before-context"), ("-C", "--context")])
            if (Int(input, key) is int n)
            {
                if (n < 0)
                    return (null, $"Error: '{key}' must be 0 or more.");
                if (!count)
                    args.Add($"{option}={n.ToString(CultureInfo.InvariantCulture)}");
            }
        args.AddRange(["--regexp", p.GetString()!]);
        return (args, null);
    }

    /// <summary>Runs rg over the lines written to a scratch file, then applies offset and head_limit as Claude Code's Grep tool does.</summary>
    public static async Task<string> SearchAsync(IReadOnlyList<string> lines, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        (List<string>? args, string? error) = Arguments(input);
        if (args is null)
            return error!;
        int offset = Int(input, "offset") ?? 0;
        int head = Int(input, "head_limit") ?? DefaultHeadLimit;
        if (offset < 0 || head < 0)
            return "Error: 'offset' and 'head_limit' must be 0 or more.";

        string file = Path.Combine(Path.GetTempPath(), $"connapse-memory-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(file, string.Join('\n', lines) + "\n", new UTF8Encoding(false), ct);
            args.Add(file);
            (int exit, string stdout, string stderr) = await RunAsync(args, ct);
            if (exit == 1)
                return "No lines match.";
            if (exit != 0)
                return $"Error: {stderr.Trim()}";

            List<string> output = [.. stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')];
            List<string> shown = [.. output.Skip(offset).Take(head == 0 ? int.MaxValue : head)];
            StringBuilder text = new(string.Join('\n', shown));
            int hidden = output.Count - offset - shown.Count;
            if (hidden > 0)
                text.Append(CultureInfo.InvariantCulture, $"\n({hidden} more output lines not shown; raise head_limit or use offset)");
            return text.ToString();
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ProcessStartInfo start = new(Binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
            start.ArgumentList.Add(a);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start rg.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (2, "", "the search took too long.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private static bool Flag(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
}
