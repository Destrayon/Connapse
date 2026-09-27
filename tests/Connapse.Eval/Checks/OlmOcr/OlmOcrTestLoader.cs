using System.Text.Json;
using Connapse.Eval.Checks.Matching;

namespace Connapse.Eval.Checks.OlmOcr;

public sealed class OlmOcrValidationException(string message) : Exception(message);

/// <summary>
/// Loads olmOCR-bench JSONL rows the way <c>load_single_test</c> does: the dataclass constructor
/// rejects unknown fields, <c>__post_init__</c> validates and normalizes text fields. Types the port
/// does not support (<c>format</c>, <c>footnote</c>) are rejected, so a new benchmark revision that
/// starts using them fails loudly instead of being silently skipped.
/// </summary>
public static class OlmOcrTestLoader
{
    private static readonly string[] BaseFields = ["pdf", "page", "id", "type", "max_diffs", "checked", "url"];

    private static readonly Dictionary<string, string[]> TypeFields = new(StringComparer.Ordinal)
    {
        ["present"] = ["text", "case_sensitive", "first_n", "last_n"],
        ["absent"] = ["text", "case_sensitive", "first_n", "last_n"],
        ["order"] = ["before", "after"],
        ["table"] = ["cell", "up", "down", "left", "right", "top_heading", "left_heading", "ignore_markdown_tables"],
        ["baseline"] = ["max_length", "max_length_skips_image_alt_tags", "max_repeats", "check_disallowed_characters"],
        ["math"] = ["math", "ignore_dollar_delimited"],
    };

    public static IReadOnlyList<OlmOcrTest> LoadJsonl(string path)
    {
        List<OlmOcrTest> tests = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            OlmOcrTest test;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                test = Load(doc.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or OlmOcrValidationException)
            {
                throw new OlmOcrValidationException($"{Path.GetFileName(path)} line {lineNumber}: {ex.Message}");
            }
            if (!ids.Add(test.Id))
                throw new OlmOcrValidationException($"Test with duplicate id {test.Id} found, error loading tests.");
            tests.Add(test);
        }
        return tests;
    }

    public static OlmOcrTest Load(JsonElement row)
    {
        string type = OptionalString(row, "type") ?? throw new OlmOcrValidationException("Missing field 'type'");
        if (!TypeFields.TryGetValue(type, out string[]? extra))
            throw new OlmOcrValidationException($"Unsupported test type: {type}");
        foreach (JsonProperty property in row.EnumerateObject())
            if (!BaseFields.Contains(property.Name) && !extra.Contains(property.Name))
                throw new OlmOcrValidationException($"Unexpected field '{property.Name}' for type {type}");

        string pdf = OptionalString(row, "pdf") ?? "";
        string id = OptionalString(row, "id") ?? "";
        int page = row.TryGetProperty("page", out JsonElement p) ? p.GetInt32() : throw new OlmOcrValidationException("Missing field 'page'");
        int maxDiffs = OptionalInt(row, "max_diffs") ?? 0;
        if (pdf.Length == 0)
            throw new OlmOcrValidationException("PDF filename cannot be empty");
        if (id.Length == 0)
            throw new OlmOcrValidationException("Test ID cannot be empty");
        if (maxDiffs < 0)
            throw new OlmOcrValidationException("Max diffs must be positive number or 0");

        switch (type)
        {
            case "present":
            case "absent":
            {
                string text = OlmOcrText.Normalize(Required(row, "text"))!;
                if (PyText.Strip(text).Length == 0)
                    throw new OlmOcrValidationException("Text field cannot be empty");
                return new TextPresenceTest(pdf, page, id, type, maxDiffs, text,
                    OptionalBool(row, "case_sensitive") ?? true, OptionalInt(row, "first_n"), OptionalInt(row, "last_n"));
            }
            case "order":
            {
                string before = OlmOcrText.Normalize(Required(row, "before"))!;
                string after = OlmOcrText.Normalize(Required(row, "after"))!;
                if (PyText.Strip(before).Length == 0)
                    throw new OlmOcrValidationException("Before field cannot be empty");
                if (PyText.Strip(after).Length == 0)
                    throw new OlmOcrValidationException("After field cannot be empty");
                if (maxDiffs > PyText.Length(before) / 2 || maxDiffs > PyText.Length(after) / 2)
                    throw new OlmOcrValidationException("Max diffs is too large for this test, greater than 50% of the search string");
                return new TextOrderTest(pdf, page, id, type, maxDiffs, before, after);
            }
            case "table":
                return new TableTest(pdf, page, id, type, maxDiffs,
                    OlmOcrText.Normalize(Required(row, "cell"))!,
                    OlmOcrText.Normalize(OptionalString(row, "up")), OlmOcrText.Normalize(OptionalString(row, "down")),
                    OlmOcrText.Normalize(OptionalString(row, "left")), OlmOcrText.Normalize(OptionalString(row, "right")),
                    OlmOcrText.Normalize(OptionalString(row, "top_heading")), OlmOcrText.Normalize(OptionalString(row, "left_heading")),
                    OptionalBool(row, "ignore_markdown_tables") ?? false);
            case "baseline":
                return new BaselineTest(pdf, page, id, type, maxDiffs,
                    OptionalInt(row, "max_length"), OptionalBool(row, "max_length_skips_image_alt_tags") ?? false,
                    OptionalInt(row, "max_repeats") ?? 30, OptionalBool(row, "check_disallowed_characters") ?? true);
            default:
            {
                string math = Required(row, "math");
                if (PyText.Strip(math).Length == 0)
                    throw new OlmOcrValidationException("Math test must have non-empty math expression");
                return new MathTest(pdf, page, id, type, maxDiffs, math);
            }
        }
    }

    /// <summary>
    /// benchmark.py adds a <c>baseline</c> test for every PDF that has none, grouped under its own
    /// "baseline" category.
    /// </summary>
    public static IReadOnlyList<BaselineTest> DefaultBaselines(IEnumerable<OlmOcrTest> tests) =>
        tests.GroupBy(t => t.Pdf, StringComparer.Ordinal)
            .Where(g => !g.Any(t => t.Type == "baseline"))
            .Select(g => new BaselineTest(g.Key, 1, $"{g.Key}_baseline", "baseline", 0))
            .ToList();

    private static string Required(JsonElement row, string name) =>
        OptionalString(row, name) ?? throw new OlmOcrValidationException($"Missing field '{name}'");

    private static string? OptionalString(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static int? OptionalInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;

    private static bool? OptionalBool(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
}
