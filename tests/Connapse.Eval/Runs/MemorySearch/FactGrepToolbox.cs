using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Connapse.Core;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// The grep arm of the memory-agent suite (CON-321): a memory space's live facts as a text file, one fact per line,
/// searched with a regex <c>grep</c> and read by line range with <c>read</c>. The file is rendered again on every call,
/// so a write through <paramref name="writes"/> shows up on the next one. Write tools, when given, are offered beside
/// grep and read and called unchanged.
/// </summary>
public sealed class FactGrepToolbox(
    Func<CancellationToken, Task<IReadOnlyList<MemoryFact>>> facts, IToolbox? writes = null, TimeProvider? clock = null) : IToolbox
{
    public const string GrepTool = "grep";
    public const string ReadTool = "read";
    public const int MaxGrepLines = 50;
    public const int MaxReadLines = 100;
    public const string Header = "id | subject | attribute | value | valid from | status";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static readonly ToolSpec Grep = new(
        GrepTool,
        "Search the memory file with a regular expression (.NET syntax) and return the matching lines with their line numbers, "
        + $"at most {MaxGrepLines}. Line 1 is the header: {Header}. Each other line is one fact that is true now or has ended.",
        new Dictionary<string, JsonElement>
        {
            ["pattern"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The regular expression to match against each line" }),
            ["ignoreCase"] = JsonSerializer.SerializeToElement(new { type = "boolean", description = "Match without regard to case. Default false" }),
        },
        ["pattern"]);

    public static readonly ToolSpec Read = new(
        ReadTool,
        $"Read a range of lines of the memory file, with their line numbers, at most {MaxReadLines} at a time. Line 1 is the "
        + $"header: {Header}.",
        new Dictionary<string, JsonElement>
        {
            ["offset"] = JsonSerializer.SerializeToElement(new { type = "integer", description = "The first line to read (1-based). Default 1" }),
            ["limit"] = JsonSerializer.SerializeToElement(new { type = "integer", description = $"How many lines to read (1-{MaxReadLines}). Default {MaxReadLines}" }),
        },
        []);

    public IReadOnlyList<ToolSpec> Tools { get; } = [Grep, Read, .. writes?.Tools ?? []];

    public async Task<string> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (name is GrepTool or ReadTool)
        {
            List<string> lines = Render(await facts(ct), _clock.GetUtcNow());
            return name == GrepTool ? GrepLines(lines, input) : ReadLines(lines, input);
        }
        return writes is not null && writes.Tools.Any(t => t.Name == name)
            ? await writes.CallAsync(name, input, ct)
            : $"Error: Unknown tool '{name}'.";
    }

    /// <summary>
    /// The file: the header, then every fact that was not corrected away, oldest saved first. A fact whose end date has
    /// passed is marked ended; the others are current.
    /// </summary>
    public static List<string> Render(IEnumerable<MemoryFact> facts, DateTimeOffset now)
    {
        List<string> lines = [Header];
        foreach (MemoryFact f in facts.Where(f => f.SupersededAt is null).OrderBy(f => f.RecordedAt).ThenBy(f => f.Id))
        {
            string status = f.ValidTo is { } end && end <= now ? $"ended {Day(end)}" : "current";
            lines.Add($"{f.Id} | {Flat(f.Subject)} | {Flat(f.Attribute)} | {Flat(f.Value)} | {(f.ValidFrom is { } from ? Day(from) : "")} | {status}");
        }
        return lines;
    }

    private static string Day(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Flat(string text) => text.ReplaceLineEndings(" ");

    public static string GrepLines(IReadOnlyList<string> lines, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (!input.TryGetValue("pattern", out JsonElement p) || p.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(p.GetString()))
            return "Error: Missing required argument 'pattern'.";
        bool ignoreCase = input.TryGetValue("ignoreCase", out JsonElement ic) && ic.ValueKind == JsonValueKind.True;
        Regex regex;
        try
        {
            regex = new Regex(p.GetString()!, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return $"Error: Invalid pattern: {ex.Message}";
        }

        List<int> matches = [];
        try
        {
            for (int i = 0; i < lines.Count; i++)
                if (regex.IsMatch(lines[i]))
                    matches.Add(i);
        }
        catch (RegexMatchTimeoutException)
        {
            return "Error: The pattern took too long to match.";
        }
        if (matches.Count == 0)
            return "No lines match.";

        StringBuilder text = new();
        foreach (int i in matches.Take(MaxGrepLines))
            text.Append(CultureInfo.InvariantCulture, $"{i + 1}: {lines[i]}\n");
        if (matches.Count > MaxGrepLines)
            text.Append(CultureInfo.InvariantCulture, $"({matches.Count - MaxGrepLines} more matching lines not shown; narrow the pattern)\n");
        return text.ToString().TrimEnd('\n');
    }

    public static string ReadLines(IReadOnlyList<string> lines, IReadOnlyDictionary<string, JsonElement> input)
    {
        int offset = Int(input, "offset") ?? 1;
        int limit = Int(input, "limit") ?? MaxReadLines;
        if (offset < 1)
            return "Error: 'offset' must be 1 or more.";
        if (limit is < 1 or > MaxReadLines)
            return $"Error: 'limit' must be between 1 and {MaxReadLines}.";
        if (offset > lines.Count)
            return $"The file has {lines.Count} lines.";

        int last = Math.Min(lines.Count, offset + limit - 1);
        StringBuilder text = new();
        for (int i = offset; i <= last; i++)
            text.Append(CultureInfo.InvariantCulture, $"{i}: {lines[i - 1]}\n");
        text.Append(CultureInfo.InvariantCulture, $"(lines {offset}-{last} of {lines.Count})");
        return text.ToString();
    }

    private static int? Int(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
}
