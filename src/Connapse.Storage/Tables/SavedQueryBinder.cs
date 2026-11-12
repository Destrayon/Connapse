using System.Globalization;
using System.Text;
using System.Text.Json;
using Connapse.Core;

namespace Connapse.Storage.Tables;

/// <summary>A saved query with its arguments filled in: exactly one of the two requests is set, by its kind.</summary>
public sealed record BoundSavedQuery(TableReadRequest? Read, TableAggregateRequest? Aggregate)
{
    public Guid TableId => Read?.TableId ?? Aggregate!.TableId;

    public TableJoin? Join => Read is not null ? Read.Join : Aggregate!.Join;
}

/// <summary>
/// Checks a saved query's definition and fills its parameters (CON-311). A parameter only ever fills a filter value:
/// a <c>{"param": "&lt;name&gt;"}</c> written as a condition's <c>value</c> or as an item of its <c>values</c>. The
/// filled JSON then goes through the same strict <see cref="TableRequestJson"/> parser an agent's own request does,
/// so a saved query can't ask for anything an agent couldn't. Every refusal is a <see cref="TableQueryException"/>
/// that says what to fix.
/// </summary>
public static class SavedQueryBinder
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1000;
    public const int MaxParameters = 20;
    public const int MaxRequestLength = 16 * 1024;

    private const string ParamKey = "param";

    /// <summary>
    /// True when <paramref name="name"/> can name a saved query or a parameter: a lowercase letter, then lowercase
    /// letters, digits, <c>_</c> or <c>-</c>, at most 64 characters.
    /// </summary>
    public static bool IsName(string? name) =>
        name is { Length: >= 1 and <= MaxNameLength }
        && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    /// <summary>
    /// Refuses a definition that couldn't run: a bad name or description, a parameter declared twice or never used, a
    /// placeholder for an undeclared parameter or outside a filter value, a default of the wrong type, a login
    /// parameter that isn't text or has a default, or a request the table parser refuses. Returns the request filled
    /// with sample values, which names the tables the query reads.
    /// </summary>
    public static BoundSavedQuery Validate(SavedQueryDefinition definition)
    {
        if (definition.Parameters is null)
            throw new TableQueryException("The parameters must be a list; send [] for none.");
        if (!IsName(definition.Name))
            throw new TableQueryException(
                $"The name must start with a lowercase letter and have only lowercase letters, digits, _ and - (at most {MaxNameLength} characters), such as \"sales_by_region\".");
        if (string.IsNullOrWhiteSpace(definition.Description) || definition.Description.Length > MaxDescriptionLength)
            throw new TableQueryException(
                $"The description must say what the query answers, in at most {MaxDescriptionLength} characters; agents pick a query by it.");
        if (definition.Parameters.Count > MaxParameters)
            throw new TableQueryException($"A saved query can have at most {MaxParameters} parameters.");

        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (SavedQueryParameter parameter in definition.Parameters)
        {
            if (!IsName(parameter.Name))
                throw new TableQueryException(
                    $"Parameter names must start with a lowercase letter and have only lowercase letters, digits, _ and -, such as \"region\"; \"{Shown(parameter.Name)}\" doesn't.");
            if (!declared.Add(parameter.Name))
                throw new TableQueryException($"The parameter \"{parameter.Name}\" is declared twice.");
            if (parameter.Description is { Length: > MaxDescriptionLength })
                throw new TableQueryException($"The description of \"{parameter.Name}\" is longer than {MaxDescriptionLength} characters.");
            if (parameter.FromLogin is not null)
            {
                if (parameter.Type != SavedQueryParameterType.Text)
                    throw new TableQueryException($"\"{parameter.Name}\" is filled from the caller's login, so its type must be text.");
                if (parameter.Default is not null)
                    throw new TableQueryException($"\"{parameter.Name}\" is filled from the caller's login, so it can't have a default.");
            }
            else if (parameter.Default is not null)
            {
                JsonElement value;
                try
                {
                    value = Parse(parameter.Default);
                }
                catch (JsonException)
                {
                    throw new TableQueryException($"The default of \"{parameter.Name}\" must be a JSON value, such as \"west\" or 5.");
                }
                if (!Fits(value, parameter.Type))
                    throw new TableQueryException($"The default of \"{parameter.Name}\" isn't {Expected(parameter.Type)}.");
            }
        }

        JsonElement template = Template(definition);
        HashSet<string> used = new(StringComparer.Ordinal);
        Collect(template, slot: false, used);
        foreach (string name in used.Where(n => !declared.Contains(n)))
            throw new TableQueryException($"The request uses the parameter \"{Shown(name)}\", which isn't declared.");
        foreach (string name in declared.Where(n => !used.Contains(n)))
            throw new TableQueryException(
                $"The parameter \"{name}\" isn't used; write {{\"param\": \"{name}\"}} as a filter value, or remove it.");

        Dictionary<string, JsonElement> samples = definition.Parameters.ToDictionary(p => p.Name, p => Sample(p.Type), StringComparer.Ordinal);
        return Parse(definition.Kind, Fill(template, samples));
    }

    /// <summary>
    /// The login values <paramref name="definition"/> needs that <paramref name="login"/> doesn't have, so the caller
    /// is refused before anything runs; empty when none are missing.
    /// </summary>
    public static IReadOnlyList<SavedQueryLoginField> MissingLogin(SavedQueryDefinition definition, SavedQueryLogin login) =>
        definition.Parameters
            .Where(p => p.FromLogin is { } field && string.IsNullOrEmpty(login.Value(field)))
            .Select(p => p.FromLogin!.Value)
            .Distinct()
            .ToList();

    /// <summary>
    /// Fills <paramref name="definition"/>'s parameters from <paramref name="arguments"/> (a JSON object of name to
    /// value, or nothing) and <paramref name="login"/>, and parses the result. An unknown argument, a missing required
    /// one, one of the wrong type, or one for a parameter filled from the login is refused. Call
    /// <see cref="MissingLogin"/> first: a missing login value here is refused as a bad request.
    /// </summary>
    public static BoundSavedQuery Bind(SavedQueryDefinition definition, JsonElement? arguments, SavedQueryLogin login)
    {
        Dictionary<string, JsonElement> sent = Arguments(arguments);
        Dictionary<string, SavedQueryParameter> parameters = definition.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (string name in sent.Keys)
        {
            if (!parameters.TryGetValue(name, out SavedQueryParameter? parameter))
                throw new TableQueryException(
                    $"\"{Shown(name)}\" isn't a parameter of \"{definition.Name}\". {Accepted(definition)}");
            if (parameter.FromLogin is not null)
                throw new TableQueryException($"\"{name}\" is filled from your login; don't send it.");
        }

        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        foreach (SavedQueryParameter parameter in definition.Parameters)
        {
            if (parameter.FromLogin is { } field)
            {
                string value = login.Value(field) is { Length: > 0 } v
                    ? v
                    : throw new TableQueryException($"\"{parameter.Name}\" is filled from your login, which has no {LoginName(field)}.");
                values[parameter.Name] = JsonSerializer.SerializeToElement(value);
            }
            else if (sent.TryGetValue(parameter.Name, out JsonElement value))
            {
                values[parameter.Name] = Fits(value, parameter.Type)
                    ? value
                    : throw new TableQueryException($"\"{parameter.Name}\" must be {Expected(parameter.Type)}.");
            }
            else if (parameter.Default is not null)
            {
                values[parameter.Name] = Parse(parameter.Default);
            }
            else
            {
                throw new TableQueryException($"\"{parameter.Name}\" is required. {Accepted(definition)}");
            }
        }

        return Parse(definition.Kind, Fill(Template(definition), values));
    }

    private static BoundSavedQuery Parse(SavedQueryKind kind, JsonElement request) => kind switch
    {
        SavedQueryKind.Read => new BoundSavedQuery(TableRequestJson.ParseRead(request), null),
        SavedQueryKind.Aggregate => new BoundSavedQuery(null, TableRequestJson.ParseAggregate(request)),
        _ => throw new TableQueryException("The kind must be read or aggregate."),
    };

    private static JsonElement Template(SavedQueryDefinition definition)
    {
        if (definition.Request is null || definition.Request.Length > MaxRequestLength)
            throw new TableQueryException($"The request must be a JSON object of at most {MaxRequestLength / 1024} KB.");
        JsonElement template;
        try
        {
            template = Parse(definition.Request);
        }
        catch (JsonException)
        {
            throw new TableQueryException("The request must be a JSON object, such as {\"table\": \"<id>\"}.");
        }
        return template.ValueKind == JsonValueKind.Object
            ? template
            : throw new TableQueryException("The request must be a JSON object, such as {\"table\": \"<id>\"}.");
    }

    private static Dictionary<string, JsonElement> Arguments(JsonElement? arguments)
    {
        Dictionary<string, JsonElement> sent = new(StringComparer.Ordinal);
        if (arguments is not { } json || json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return sent;
        if (json.ValueKind != JsonValueKind.Object)
            throw new TableQueryException("The arguments must be a JSON object of parameter name to value, such as {\"region\": \"west\"}.");
        foreach (JsonProperty property in json.EnumerateObject())
        {
            if (!sent.TryAdd(property.Name, property.Value.Clone()))
                throw new TableQueryException($"\"{Shown(property.Name)}\" is sent twice.");
        }
        return sent;
    }

    /// <summary>The parameter a placeholder names, or null when <paramref name="json"/> isn't one.</summary>
    private static string? Placeholder(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return null;
        using JsonElement.ObjectEnumerator properties = json.EnumerateObject();
        if (!properties.MoveNext() || properties.Current.Name != ParamKey)
            return null;
        JsonElement name = properties.Current.Value;
        if (properties.MoveNext())
            return null;
        return name.ValueKind == JsonValueKind.String
            ? name.GetString()!
            : throw new TableQueryException("A placeholder is {\"param\": \"<name>\"}, with the parameter's name as a string.");
    }

    /// <summary>
    /// Adds every parameter a placeholder in <paramref name="json"/> names to <paramref name="used"/>.
    /// <paramref name="slot"/> is true where a filter value goes: under a condition's <c>value</c> or in its <c>values</c>.
    /// </summary>
    private static void Collect(JsonElement json, bool slot, HashSet<string> used)
    {
        if (Placeholder(json) is { } name)
        {
            if (!slot)
                throw new TableQueryException(
                    $"{{\"param\": \"{Shown(name)}\"}} can only stand for a filter's \"value\" or an item of its \"values\".");
            used.Add(name);
            return;
        }
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in json.EnumerateObject())
                {
                    if (property.Name == "values" && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in property.Value.EnumerateArray())
                            Collect(item, slot: true, used);
                    }
                    else
                    {
                        Collect(property.Value, slot: property.Name == "value", used);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in json.EnumerateArray())
                    Collect(item, slot: false, used);
                break;
        }
    }

    /// <summary><paramref name="template"/> with every placeholder replaced by its value.</summary>
    private static JsonElement Fill(JsonElement template, IReadOnlyDictionary<string, JsonElement> values)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
            Write(template, values, writer);
        return Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void Write(JsonElement json, IReadOnlyDictionary<string, JsonElement> values, Utf8JsonWriter writer)
    {
        if (Placeholder(json) is { } name)
        {
            (values.TryGetValue(name, out JsonElement value)
                ? value
                : throw new TableQueryException($"The parameter \"{Shown(name)}\" isn't declared.")).WriteTo(writer);
            return;
        }
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in json.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, values, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in json.EnumerateArray())
                    Write(item, values, writer);
                writer.WriteEndArray();
                break;
            default:
                json.WriteTo(writer);
                break;
        }
    }

    /// <summary>True when <paramref name="value"/> is a value of <paramref name="type"/>; null never is.</summary>
    public static bool Fits(JsonElement value, SavedQueryParameterType type) => type switch
    {
        SavedQueryParameterType.Text => value.ValueKind == JsonValueKind.String,
        SavedQueryParameterType.WholeNumber => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        SavedQueryParameterType.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) && double.IsFinite(d),
        SavedQueryParameterType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        SavedQueryParameterType.Date => value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        SavedQueryParameterType.Timestamp => value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _),
        _ => false,
    };

    private static JsonElement Sample(SavedQueryParameterType type) => type switch
    {
        SavedQueryParameterType.Text => JsonSerializer.SerializeToElement("x"),
        SavedQueryParameterType.WholeNumber or SavedQueryParameterType.Number => JsonSerializer.SerializeToElement(1),
        SavedQueryParameterType.Boolean => JsonSerializer.SerializeToElement(true),
        SavedQueryParameterType.Date => JsonSerializer.SerializeToElement("2000-01-01"),
        _ => JsonSerializer.SerializeToElement("2000-01-01T00:00:00Z"),
    };

    private static string Expected(SavedQueryParameterType type) => type switch
    {
        SavedQueryParameterType.Text => "a string",
        SavedQueryParameterType.WholeNumber => "a whole number",
        SavedQueryParameterType.Number => "a number",
        SavedQueryParameterType.Boolean => "true or false",
        SavedQueryParameterType.Date => "a date such as \"2026-11-01\"",
        _ => "a time such as \"2026-11-01T09:00:00Z\"",
    };

    private static string LoginName(SavedQueryLoginField field) =>
        field == SavedQueryLoginField.Email ? "email address" : "user id";

    private static string Accepted(SavedQueryDefinition definition)
    {
        string[] names = definition.Parameters.Where(p => p.FromLogin is null).Select(p => p.Name).ToArray();
        return names.Length == 0 ? "It takes no arguments." : $"It takes: {string.Join(", ", names)}.";
    }

    /// <summary>A name from the request, cut short so a long one can't flood the error.</summary>
    private static string Shown(string? name) =>
        name is null ? "" : name.Length <= MaxNameLength ? name : name[..MaxNameLength] + "...";

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
