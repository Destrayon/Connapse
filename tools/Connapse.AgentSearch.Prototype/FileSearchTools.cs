using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;

namespace Connapse.AgentSearch.Prototype;

/// <summary>
/// Exact-text search, file listing and file reading over the original files. Arguments are typed
/// parameters rather than a raw rg command line, so an agent cannot pass `--pre`, `-z` or
/// `--search-zip` (which run external programs) or any other flag the tool does not offer.
/// </summary>
[McpServerToolType]
public sealed class FileSearchTools(FileRoot root)
{
    private const int MaxOutputChars = 20_000;
    private const int MaxLineChars = 2_000;
    private const int HintReserve = 100;

    [McpServerTool(Name = "grep_files"), Description(
        "Exact-text or regex search over the original files, like ripgrep. Returns matching lines as " +
        "'/path:line:text'. Use for exact names, identifiers, error messages, config keys and literal strings.")]
    public async Task<string> GrepFiles(
        [Description("Regex (Rust/ripgrep syntax). Set fixed_strings=true to match literally.")] string pattern,
        [Description("Folder or file to search, e.g. '/src/Connapse.Web'. Default: everything.")] string? path = null,
        [Description("Only search files matching this glob, e.g. '*.cs' or '**/Endpoints/*.cs'.")] string? glob = null,
        [Description("Case-insensitive match.")] bool ignore_case = false,
        [Description("Treat pattern as a literal string, not a regex.")] bool fixed_strings = false,
        [Description("Lines of context before and after each match (0-10).")] int context = 0,
        [Description("Return only the paths of matching files.")] bool files_only = false,
        [Description("Stop after this many matching lines per file (1-100). Default 20.")] int max_per_file = 20,
        CancellationToken ct = default)
    {
        string? target = root.Resolve(path);
        if (target is null) return "Error: path is outside the searchable folder.";

        var args = new List<string> { "--no-heading", "--with-filename", "--line-number", "--color", "never",
            "--path-separator", "/", "--no-config" };
        if (ignore_case) args.Add("-i");
        if (fixed_strings) args.Add("-F");
        if (files_only) args.Add("-l");
        else args.AddRange(["-m", Math.Clamp(max_per_file, 1, 100).ToString()]);
        if (context > 0 && !files_only) args.AddRange(["-C", Math.Clamp(context, 0, 10).ToString()]);
        if (!string.IsNullOrWhiteSpace(glob)) args.AddRange(["--glob", glob]);
        args.AddRange(["-e", pattern, "--", RelativeTarget(target)]);

        (int exit, string output, string error) = await RunRipgrep(args, ct);
        if (exit == 1) return "No matches.";
        if (exit != 0) return "Error: " + error.Trim();
        return Truncate(PrefixPaths(output));
    }

    [McpServerTool(Name = "find_files"), Description(
        "List files whose path matches a glob, e.g. '**/*Settings*.cs' or 'docs/**/*.md'.")]
    public async Task<string> FindFiles(
        [Description("Glob matched against the path relative to the folder.")] string glob,
        [Description("Folder to list under. Default: everything.")] string? path = null,
        CancellationToken ct = default)
    {
        string? target = root.Resolve(path);
        if (target is null) return "Error: path is outside the searchable folder.";

        var args = new List<string> { "--files", "--path-separator", "/", "--no-config", "--glob", glob, "--",
            RelativeTarget(target) };
        (int exit, string output, string error) = await RunRipgrep(args, ct);
        if (exit == 1 || output.Length == 0) return "No files.";
        if (exit != 0) return "Error: " + error.Trim();
        return Truncate(PrefixPaths(output));
    }

    [McpServerTool(Name = "read_file"), Description(
        "Read a file with line numbers. Use offset and limit to page through long files.")]
    public async Task<string> ReadFile(
        [Description("File path as shown by grep_files or find_files, e.g. '/src/Connapse.Web/Program.cs'.")] string path,
        [Description("First line to return, 1-based. Default 1.")] int offset = 1,
        [Description("Number of lines to return (1-1000). Default 300.")] int limit = 300,
        CancellationToken ct = default)
    {
        string? full = root.Resolve(path);
        if (full is null) return "Error: path is outside the searchable folder.";
        if (!File.Exists(full)) return "Error: no such file.";

        string[] lines = await File.ReadAllLinesAsync(full, ct);
        long start = Math.Max(1, offset);
        long end = Math.Min(lines.Length, start + Math.Clamp(limit, 1, 1000) - 1);
        var sb = new StringBuilder();
        sb.AppendLine($"{root.Display(full)} ({lines.Length} lines)");
        // Stop before the budget runs out so the continuation hint always fits and points at the first unshown line.
        long next = start;
        for (; next <= end; next++)
        {
            string line = lines[next - 1];
            if (line.Length > MaxLineChars) line = line[..MaxLineChars] + " ... (line cut)";
            if (sb.Length + line.Length + 16 > MaxOutputChars - HintReserve) break;
            sb.Append(next).Append('\t').AppendLine(line);
        }
        if (next <= lines.Length)
            sb.AppendLine($"... more lines; continue with offset={next}");
        return sb.ToString();
    }

    private string RelativeTarget(string full) =>
        full == root.Root ? "." : Path.GetRelativePath(root.Root, full).Replace('\\', '/');

    private static string PrefixPaths(string output)
    {
        var sb = new StringBuilder();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string l = line.TrimEnd('\r');
            if (l == "--") { sb.AppendLine(l); continue; }
            sb.Append('/').AppendLine(l.StartsWith("./", StringComparison.Ordinal) ? l[2..] : l);
        }
        return sb.ToString();
    }

    private static string Truncate(string text) =>
        text.Length <= MaxOutputChars
            ? text
            : text[..MaxOutputChars] + "\n... output truncated; narrow the search with path, glob or a tighter pattern.";

    private async Task<(int Exit, string Output, string Error)> RunRipgrep(List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(root.Ripgrep)
        {
            WorkingDirectory = root.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        try
        {
            // Keep a little more than the output budget so Truncate still sees that there was more.
            Task<string> stdout = ReadBounded(process.StandardOutput, MaxOutputChars + 1, ct);
            Task<string> stderr = ReadBounded(process.StandardError, 4_000, ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already exited
            throw;
        }
    }

    // Keeps the first `cap` characters and discards the rest, but reads to the end so rg never blocks on a full pipe.
    private static async Task<string> ReadBounded(StreamReader reader, int cap, CancellationToken ct)
    {
        var kept = new StringBuilder();
        char[] buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (kept.Length < cap)
                kept.Append(buffer, 0, Math.Min(read, cap - kept.Length));
        }
        return kept.ToString();
    }
}
