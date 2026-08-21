using System.Text.Json.Nodes;
using Connapse.Core;

namespace Connapse.Web.Components.Settings;

/// <summary>
/// The editable shape of a connection, flattened out of the provider-specific JSON it is stored
/// as and back again.
/// <para>
/// A plain class rather than logic inside the Razor component, so the round trip can be tested:
/// this is where a dropped field silently becomes a connection that resolves to the wrong
/// place, and the component itself has no test harness in this repository.
/// </para>
/// </summary>
public sealed class ConnectionForm
{
    public string Name { get; set; } = "";
    public ConnectionProvider Provider { get; set; } = ConnectionProvider.S3;

    // S3
    public string? Region { get; set; }
    public string? RoleArn { get; set; }

    // Azure Blob
    public string? StorageAccountName { get; set; }
    public string? ManagedIdentityClientId { get; set; }

    // Filesystem
    public string? AllowedRoot { get; set; }

    /// <summary>Newline-separated in the UI; an array in the stored JSON.</summary>
    public string? AllowedLocations { get; set; }

    /// <summary>
    /// Never populated from storage. A connection's stored secret has no read path outside the
    /// sync engine, so this only ever carries a replacement the operator just typed.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Reads a stored connection into an editable form. A config that cannot be parsed yields an
    /// empty form rather than throwing — the tab stays usable and saving rewrites it.
    /// </summary>
    public static ConnectionForm FromConnection(Connection connection)
    {
        var form = new ConnectionForm { Name = connection.Name, Provider = connection.Provider };

        if (string.IsNullOrWhiteSpace(connection.ConfigJson))
            return form;

        JsonObject? node;
        try
        {
            node = JsonNode.Parse(connection.ConfigJson)?.AsObject();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return form;
        }

        if (node is null) return form;

        form.Region = Str(node, "region");
        form.RoleArn = Str(node, "roleArn");
        form.StorageAccountName = Str(node, "storageAccountName");
        form.ManagedIdentityClientId = Str(node, "managedIdentityClientId");
        form.AllowedRoot = Str(node, "allowedRoot");

        if (node["allowedLocations"] is JsonArray arr)
        {
            var values = arr
                .Select(x => x?.GetValue<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (values.Count > 0)
                form.AllowedLocations = string.Join("\n", values);
        }

        return form;
    }

    /// <summary>
    /// Serializes the form to the config JSON the connector factory reads. Only fields belonging
    /// to the selected provider are written, so switching provider cannot leave a stale key
    /// behind that the factory would then honour.
    /// </summary>
    public string ToConfigJson()
    {
        var node = new JsonObject();

        switch (Provider)
        {
            case ConnectionProvider.S3:
                node["region"] = Blank(Region) ? "us-east-1" : Region!.Trim();
                if (!Blank(RoleArn)) node["roleArn"] = RoleArn!.Trim();
                break;

            case ConnectionProvider.AzureBlob:
                node["storageAccountName"] = StorageAccountName?.Trim() ?? "";
                if (!Blank(ManagedIdentityClientId))
                    node["managedIdentityClientId"] = ManagedIdentityClientId!.Trim();
                break;

            case ConnectionProvider.Filesystem:
                node["allowedRoot"] = AllowedRoot?.Trim() ?? "";
                break;
        }

        // Filesystem confinement is the allowed root plus the subpath check; allowedLocations is
        // the cloud equivalent and does not apply to it.
        if (Provider != ConnectionProvider.Filesystem)
        {
            var locations = ParseLocations(AllowedLocations);
            if (locations.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var location in locations) arr.Add(location);
                node["allowedLocations"] = arr;
            }
        }

        return node.ToJsonString();
    }

    /// <summary>Splits the newline-separated editor value, dropping blank lines.</summary>
    public static List<string> ParseLocations(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .ToList();

    /// <summary>
    /// The first declared location, offered as the default probe target when testing — by
    /// definition somewhere this connection is meant to be able to reach.
    /// </summary>
    public string? FirstAllowedLocation() => ParseLocations(AllowedLocations).FirstOrDefault();

    /// <summary>
    /// Splits a location into its container and optional prefix. The testers take those
    /// separately, while an allowed location carries both as one string.
    /// </summary>
    public static (string Container, string? Prefix) SplitLocation(string location)
    {
        string trimmed = location.Trim().Trim('/');
        int slash = trimmed.IndexOf('/');

        return slash < 0
            ? (trimmed, null)
            : (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    /// <summary>Returns the first problem with the form, or null when it is ready to save.</summary>
    public string? Validate()
    {
        if (Blank(Name)) return "A name is required.";

        return Provider switch
        {
            ConnectionProvider.Filesystem when Blank(AllowedRoot) => "Choose an allowed root.",
            ConnectionProvider.AzureBlob when Blank(StorageAccountName) => "A storage account is required.",
            _ => null
        };
    }

    private static string? Str(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
