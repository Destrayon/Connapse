using System.Globalization;

namespace Connapse.Eval.Cli;

/// <summary>"command positional… --option value --flag".</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positionals = [];

    public string Command { get; private set; } = "";

    public IReadOnlyList<string> Positionals => _positionals;

    public static CliArgs Parse(string[] args)
    {
        CliArgs parsed = new();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string name = arg[2..];
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                parsed._options[name] = hasValue ? args[++i] : null;
            }
            else if (parsed.Command.Length == 0)
            {
                parsed.Command = arg;
            }
            else
            {
                parsed._positionals.Add(arg);
            }
        }
        return parsed;
    }

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public bool Flag(string name) => _options.ContainsKey(name);

    public int? Int(string name) =>
        Option(name) is string value ? int.Parse(value, CultureInfo.InvariantCulture) : null;

    public IReadOnlyList<string> List(string name) =>
        Option(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    public string Required(string name) =>
        Option(name) ?? throw new ArgumentException($"--{name} is required.");
}
