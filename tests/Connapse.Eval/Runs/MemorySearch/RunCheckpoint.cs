using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// Finished runs, appended to a file as they finish, so a run killed part way restarts without losing them or scoring
/// them twice. Citations are stored as dataset ids because document ids change when a restart indexes the corpus again.
/// </summary>
public sealed class RunCheckpoint
{
    private readonly string? _path;
    private readonly Dictionary<string, (AgentRun Run, RunScore Score)> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _marks = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public RunCheckpoint(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path))
            return;
        foreach (string line in File.ReadLines(path).Where(l => l.Length > 0))
            Load(line);
        // A kill can cut the last line short; this keeps the next line off the end of it.
        File.AppendAllText(path, "\n");
    }

    public static string? PathFor(string? outPath) => outPath is null ? null : Path.ChangeExtension(outPath, ".checkpoint.jsonl");

    public int Restored { get; private set; }

    public bool TryGet(string key, out (AgentRun Run, RunScore Score) result)
    {
        lock (_lock)
            return _runs.TryGetValue(key, out result);
    }

    public (AgentRun Run, RunScore Score) Get(string key) =>
        TryGet(key, out (AgentRun Run, RunScore Score) result) ? result : throw new InvalidOperationException($"The checkpoint has no run {key}.");

    public void Save(string key, AgentRun run, RunScore score, DocumentIndex index)
    {
        AgentRun stored = run with { Cited = [.. run.Cited.Select(c => index.Resolve(c) ?? c)] };
        lock (_lock)
        {
            _runs[key] = (stored, score);
            Append(new JsonObject { ["key"] = key, ["run"] = JsonSerializer.SerializeToNode(stored), ["score"] = JsonSerializer.SerializeToNode(score) });
        }
    }

    public bool HasMark(string key)
    {
        lock (_lock)
            return _marks.ContainsKey(key);
    }

    public void Mark(string key, string value = "done")
    {
        lock (_lock)
        {
            _marks[key] = value;
            Append(new JsonObject { ["key"] = key, ["mark"] = value });
        }
    }

    /// <summary>Records <paramref name="value"/> the first time and stops a restart that would mix two different conditions after it.</summary>
    public void RequireSame(string key, string value)
    {
        lock (_lock)
        {
            if (!_marks.TryGetValue(key, out string? recorded))
                Mark(key, value);
            else if (recorded != value)
                throw new InvalidOperationException($"The checkpoint at {_path} has {key} {recorded}, not {value}; a restart would mix the two.");
        }
    }

    private void Load(string line)
    {
        JsonObject row;
        try
        {
            row = JsonNode.Parse(line)!.AsObject();
        }
        catch (JsonException)
        {
            return;
        }
        string key = (string)row["key"]!;
        if (row["mark"] is { } mark)
        {
            _marks[key] = (string)mark!;
            return;
        }
        if (!_runs.ContainsKey(key))
            Restored++;
        _runs[key] = (row["run"].Deserialize<AgentRun>()!, row["score"].Deserialize<RunScore>()!);
    }

    private void Append(JsonObject row)
    {
        if (_path is not null)
            File.AppendAllText(_path, row.ToJsonString() + "\n");
    }
}
