using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connapse.Eval.Model;

public static class EvalJson
{
    /// <summary>Indented, camelCase, NaN-tolerant (means over zero queries are NaN).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Same as <see cref="Options"/> but on one line, for JSONL files.</summary>
    public static readonly JsonSerializerOptions Line = new(Options) { WriteIndented = false };
}
