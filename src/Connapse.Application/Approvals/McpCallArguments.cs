using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// What a secret field's name ends with, once lowercased with '_' and '-' dropped: a secret word, optionally
    /// followed by a word naming its form ("clientSecret", "SecretString", "PrivateKeyPem", "x-api-key"), or "key"
    /// with a secret qualifier anywhere before it ("secretAccessKey", "aws_secret_access_key", "AccountKey").
    /// "secretName", "TokenKey" and "keyId" name a secret without holding one, so they aren't matched. The legacy
    /// scrub in McpApprovalStore runs this same pattern in SQL, so change both together.
    /// </summary>
    public const string SecretNamePattern =
        """(?:(?:secret|password|passwd|pwd|passphrase|token|credentials?|apikey|privatekey|connectionstring|authorization|cookie)(?:value|string|text|data|base64|pem)?|(?:secret|private|access|account|signing|session|shared|master|encryption)[^"]*key)""";

    private static readonly Regex SecretName = new(SecretNamePattern + "$", RegexOptions.CultureInvariant);

    /// <summary>Whether a field with this name holds a secret.</summary>
    public static bool IsSecret(string name)
    {
        string normalized = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return SecretName.IsMatch(normalized);
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
