using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;

namespace Connapse.Web.Endpoints;

/// <summary>
/// An admin's saved query as sent to PUT /tables/queries/{name} (CON-311): what it answers, read or aggregate, the
/// request with {"param": "name"} slots in its filter values, and the typed parameters that fill them.
/// </summary>
public record SaveTableQueryRequest(
    string? Description,
    string? Kind,
    JsonElement? Request,
    IReadOnlyList<SaveTableQueryParameter>? Parameters);

/// <summary>One parameter: its type, an optional default (JSON), or the login value that fills it.</summary>
public record SaveTableQueryParameter(
    string? Name,
    string? Type,
    string? Description,
    JsonElement? Default,
    [property: JsonPropertyName("from_login")] string? FromLogin);

/// <summary>A saved query as readers see it, with each parameter's type, default and login source.</summary>
public record SavedQueryResponse(
    string Name,
    string Description,
    string Kind,
    JsonElement Request,
    IReadOnlyList<SavedQueryParameterResponse> Parameters,
    string SavedBy,
    DateTime SavedAt)
{
    public static SavedQueryResponse From(SavedQuery query)
    {
        SavedQueryDefinition d = query.Definition;
        return new SavedQueryResponse(d.Name, d.Description, SavedQueryWords.Kind(d.Kind), SavedQueryWords.Json(d.Request),
            d.Parameters.Select(p => new SavedQueryParameterResponse(p.Name, SavedQueryWords.Type(p.Type), p.Description,
                p.Default is null ? null : SavedQueryWords.Json(p.Default),
                p.FromLogin is { } login ? SavedQueryWords.Login(login) : null)).ToList(),
            query.SavedBy, query.SavedAt);
    }
}

public record SavedQueryParameterResponse(
    string Name,
    string Type,
    string? Description,
    JsonElement? Default,
    [property: JsonPropertyName("from_login")] string? FromLogin);

/// <summary>The words the API uses for a saved query's kinds, parameter types and login fields.</summary>
public static class SavedQueryWords
{
    private static readonly Dictionary<string, SavedQueryKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = SavedQueryKind.Read,
        ["aggregate"] = SavedQueryKind.Aggregate,
    };

    private static readonly Dictionary<string, SavedQueryParameterType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = SavedQueryParameterType.Text,
        ["integer"] = SavedQueryParameterType.WholeNumber,
        ["number"] = SavedQueryParameterType.Number,
        ["boolean"] = SavedQueryParameterType.Boolean,
        ["date"] = SavedQueryParameterType.Date,
        ["timestamp"] = SavedQueryParameterType.Timestamp,
    };

    private static readonly Dictionary<string, SavedQueryLoginField> Logins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user_id"] = SavedQueryLoginField.UserId,
        ["email"] = SavedQueryLoginField.Email,
    };

    public static string Kind(SavedQueryKind kind) => Kinds.First(k => k.Value == kind).Key;

    public static string Type(SavedQueryParameterType type) => Types.First(t => t.Value == type).Key;

    public static string Login(SavedQueryLoginField field) => Logins.First(l => l.Value == field).Key;

    public static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>The definition the request describes, or what's wrong with it in words an admin can act on.</summary>
    public static (SavedQueryDefinition? Definition, string? Error) Definition(string name, SaveTableQueryRequest? body)
    {
        if (body is null)
            return (null, "Send the saved query as a JSON object with description, kind, request and parameters.");
        if (body.Kind is null || !Kinds.TryGetValue(body.Kind, out SavedQueryKind kind))
            return (null, "kind must be \"read\" or \"aggregate\".");
        if (body.Request is not { } request || request.ValueKind is not (JsonValueKind.Object or JsonValueKind.String))
            return (null, "request must be the read or aggregate request, as a JSON object.");
        string text = request.ValueKind == JsonValueKind.String ? request.GetString()! : request.GetRawText();

        List<SavedQueryParameter> parameters = [];
        foreach (SaveTableQueryParameter? p in body.Parameters ?? [])
        {
            if (p?.Name is null)
                return (null, "Every parameter needs a name.");
            if (p.Type is null || !Types.TryGetValue(p.Type, out SavedQueryParameterType type))
                return (null, $"Parameter \"{p.Name}\" needs a type: one of {string.Join(", ", Types.Keys)}.");
            SavedQueryLoginField? login = null;
            if (p.FromLogin is not null)
            {
                if (!Logins.TryGetValue(p.FromLogin, out SavedQueryLoginField field))
                    return (null, $"Parameter \"{p.Name}\": from_login must be \"user_id\" or \"email\".");
                login = field;
            }
            string? fallback = p.Default is { ValueKind: not JsonValueKind.Null } d ? d.GetRawText() : null;
            parameters.Add(new SavedQueryParameter(p.Name, type, p.Description, fallback, login));
        }

        return (new SavedQueryDefinition(name, body.Description ?? "", kind, text, parameters), null);
    }
}
