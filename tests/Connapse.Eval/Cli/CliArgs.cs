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

    /// <summary>The options each command accepts; anything else is a typo that would otherwise be ignored.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedOptions =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["run"] = ["suite", "config", "system", "datasets", "resume", "limit-queries"],
            ["compare"] = ["allow-dataset-mismatch"],
            ["report"] = [],
            ["pool"] = ["out"],
            ["datasets"] = ["suite", "datasets"],
        };

    /// <summary>Throws when an option is not accepted by the command. Unknown commands are left to the caller.</summary>
    public void EnsureKnownOptions()
    {
        if (!AllowedOptions.TryGetValue(Command, out IReadOnlyList<string>? allowed))
            return;
        string[] unknown = _options.Keys.Where(o => !allowed.Contains(o)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException(
                $"Unknown option{(unknown.Length > 1 ? "s" : "")} for '{Command}': {string.Join(", ", unknown.Select(o => "--" + o))}. "
                + $"Allowed: {(allowed.Count == 0 ? "none" : string.Join(", ", allowed.Select(o => "--" + o)))}.");
    }

    public int? PositiveInt(string name)
    {
        if (!Flag(name))
            return null;
        string? value = Option(name);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 1)
            throw new ArgumentException($"--{name} needs a positive integer (got '{value ?? ""}').");
        return parsed;
    }

    public IReadOnlyList<string> List(string name) =>
        Option(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    public string Required(string name) =>
        Option(name) ?? throw new ArgumentException($"--{name} is required.");
}
