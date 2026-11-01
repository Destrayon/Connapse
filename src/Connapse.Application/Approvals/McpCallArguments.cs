using System.Text;
using System.Text.Json;

namespace Connapse.Application.Approvals;

/// <summary>
/// An MCP call's arguments as an approval request identifies and shows them (CON-185): <see cref="Canonical"/>,
/// with object keys sorted and whitespace dropped so an identical call reads the same, and <see cref="Redacted"/>,
/// the same with every secret field's value replaced by <see cref="Hidden"/>. Only the redacted form is ever
/// stored readable or shown; the canonical one carries the real token or passphrase.
/// </summary>
public sealed record McpCallArguments(string Canonical, string Redacted)
{
    /// <summary>What a secret field's value reads as once redacted.</summary>
    public const string Hidden = "[hidden]";

    // Matched against the end of a field's name, lowercased with '_' and '-' dropped, so "Secret", "clientSecret",
    // "client_secret", "PrivateKeyPem", "x-api-key" and "Authorization" are all caught at any depth.
    private static readonly string[] SecretSuffixes =
    [
        "secret", "password", "passphrase", "token", "apikey", "privatekey", "privatekeypem", "connectionstring",
        "authorization", "credential", "credentials", "cookie",
    ];

    /// <summary>Whether a field with this name holds a secret.</summary>
    public static bool IsSecret(string name)
    {
        string normalized = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return SecretSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>Whether redacting hid anything, which is when the real arguments must be sealed to be matched later.</summary>
    public bool HasSecrets => !string.Equals(Canonical, Redacted, StringComparison.Ordinal);

    public static McpCallArguments From(IDictionary<string, JsonElement>? arguments) =>
        new(Write(arguments, redact: false), Write(arguments, redact: true));

    private static string Write(IDictionary<string, JsonElement>? arguments, bool redact)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in (arguments ?? new Dictionary<string, JsonElement>()).OrderBy(a => a.Key, StringComparer.Ordinal))
                WriteProperty(writer, key, value, redact);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteProperty(Utf8JsonWriter writer, string name, JsonElement value, bool redact)
    {
        writer.WritePropertyName(name);
        // A null secret is shown as null: it says the call leaves that secret unset, and reveals nothing.
        if (redact && value.ValueKind != JsonValueKind.Null && IsSecret(name))
            writer.WriteStringValue(Hidden);
        else
            WriteValue(writer, value, redact);
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value, bool redact)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    WriteProperty(writer, property.Name, property.Value, redact);
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteValue(writer, item, redact);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
