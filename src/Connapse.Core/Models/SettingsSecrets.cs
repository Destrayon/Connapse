using System.Reflection;

namespace Connapse.Core;

/// <summary>
/// Which properties of a settings type hold secrets, and which say where a secret is sent. Both are by name,
/// so a key or address added to a settings type later is covered without a list to keep up to date.
/// </summary>
public static class SettingsSecrets
{
    /// <summary>The string properties holding secrets: an API key or a password.</summary>
    public static IReadOnlyList<PropertyInfo> Of(Type type) => Strings(type, "ApiKey", "Password");

    /// <summary>The string properties saying where a secret is sent: the provider and every address (a URL or an endpoint).</summary>
    public static IReadOnlyList<PropertyInfo> DestinationsOf(Type type) => Strings(type, "Provider", "Url", "Endpoint");

    private static List<PropertyInfo> Strings(Type type, params string[] suffixes) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite
                && suffixes.Any(s => p.Name.EndsWith(s, StringComparison.Ordinal)))
            .ToList();
}
