using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors;

namespace Connapse.Web.Components.Settings;

/// <summary>
/// The editable shape of a connection, flattened out of the provider-specific JSON it is stored
/// as and back again.
/// <para>
/// A plain record rather than logic inside the Razor component, so the round trip can be tested:
/// this is where a dropped field silently becomes a connection that resolves to the wrong
/// place, and the component itself has no test harness in this repository.
/// </para>
/// </summary>
public sealed record ConnectionForm
{
    public string Name { get; set; } = "";
    public ConnectionProvider Provider { get; set; } = ConnectionProvider.S3;

    // S3
    public string? Region { get; set; }
    public string? RoleArn { get; set; }

    // Azure Blob
    public string? StorageAccountName { get; set; }
    public string? ManagedIdentityClientId { get; set; }

    // Filesystem and SFTP both bound a source with a root, so this is shared.
    public string? AllowedRoot { get; set; }

    // SFTP
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Username { get; set; }

    /// <summary>
    /// The private key, entered once and never read back. <see cref="FromConnection"/> leaves
    /// this null when editing, and a null value means "leave the stored secret alone" — the
    /// same rule the store applies, so opening and saving a connection cannot wipe its key.
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>Only needed when the private key is itself encrypted.</summary>
    public string? Passphrase { get; set; }

    /// <summary>
    /// The pinned host key, shown so it can be checked against <c>ssh-keyscan</c>. Recorded by
    /// the connector on first successful connect, never typed.
    /// </summary>
    public string? HostKeyFingerprint { get; set; }

    /// <summary>
    /// Set by the "forget" control. Clearing the pin re-arms trust on first use, which is how a
    /// server that was legitimately rekeyed is accepted again.
    /// </summary>
    public bool ForgetHostKey { get; set; }

    /// <summary>Newline-separated in the UI; an array in the stored JSON.</summary>
    public string? AllowedLocations { get; set; }

    /// <summary>
    /// Providers whose credential is a cloud identity Connapse never holds, and which are
    /// therefore bounded by <see cref="AllowedLocations"/> rather than a root.
    /// <para>
    /// Named rather than written as "not Filesystem". The old form used the negative, and
    /// adding SFTP to the enum would have silently swept it into the cloud branch — offering a
    /// bucket allowlist for a directory on a server, and no root at all.
    /// </para>
    /// </summary>
    public bool IsCloudProvider =>
        Provider is ConnectionProvider.S3 or ConnectionProvider.AzureBlob;


    /// <summary>
    /// Builds the connection the guided setup creates, from what the host's setup command
    /// reported. The same for a remote server as for the operator's own machine: only the
    /// script differs between them, never the connection it produces.
    /// </summary>
    /// <remarks>
    /// Here rather than in the Razor component for the same reason the rest of this class is:
    /// a dropped or mis-joined field produces a connection that points somewhere other than
    /// intended, and the component has no test harness in this repository.
    /// </remarks>
    /// <param name="allowedRoot">
    /// Any absolute path on the host, not merely somewhere under the home directory. Windows
    /// OpenSSH applies no chroot, so a second drive is reachable as <c>/D:/…</c> and confining
    /// the flow to the profile would rule out where most people actually keep things.
    /// Blank falls back to the reported home directory.
    /// </param>
    public static ConnectionForm ForGuidedSetup(
        SftpHostSetupResult result, string host, string? allowedRoot, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        return new ConnectionForm
        {
            Name = $"{result.Username}@{host.Trim()}",
            Provider = ConnectionProvider.Sftp,
            Host = host.Trim(),
            Port = "22",
            Username = result.Username,
            AllowedRoot = NormaliseRemoteRoot(allowedRoot, result.HomePath),
            HostKeyFingerprint = result.Fingerprint,
            PrivateKey = privateKeyPem,
        };
    }

    /// <summary>
    /// Cleans an operator-entered remote path into the form SFTP expects, falling back to
    /// <paramref name="fallback"/> when nothing was entered.
    /// </summary>
    /// <remarks>
    /// Never uses <see cref="System.IO.Path"/>: that applies the rules of whichever machine
    /// Connapse runs on, which is a Linux container, to a path describing somebody else's
    /// Windows box. SFTP paths are always '/'-separated, and Windows OpenSSH presents drives
    /// as <c>/C:/Users/me</c> — a leading slash before the drive letter.
    /// <para>
    /// A Windows path pasted from Explorer is accepted and converted, because that is what an
    /// operator will have on their clipboard.
    /// </para>
    /// </remarks>
    public static string NormaliseRemoteRoot(string? entered, string fallback)
    {
        if (string.IsNullOrWhiteSpace(entered))
            return fallback.TrimEnd('/');

        string path = entered.Trim().Replace('\\', '/');

        // "D:/Projects" pasted from Explorer needs the leading slash SFTP presents.
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            path = "/" + path;

        if (!path.StartsWith('/'))
            path = "/" + path;

        // A drive root trims to "/D:" rather than "", which is still the drive and not the
        // filesystem root — so only trim when something remains beneath it.
        string trimmed = path.TrimEnd('/');

        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>
    /// True when a root is broad enough to be worth a second look — a drive root, or the
    /// filesystem root. Permitted, since an administrator may genuinely mean it, but the UI
    /// should say what it implies.
    /// </summary>
    public static bool IsBroadRemoteRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;

        string trimmed = root.Trim().TrimEnd('/');

        // "/" and "/D:" — nothing beneath the drive or the filesystem root.
        return trimmed.Length == 0
            || (trimmed.Length == 3 && trimmed[0] == '/' && char.IsLetter(trimmed[1]) && trimmed[2] == ':');
    }

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
        form.Host = Str(node, "host");
        form.Username = Str(node, "username");
        form.HostKeyFingerprint = Str(node, "hostKeyFingerprint");

        if (node["port"] is JsonValue port && port.TryGetValue<int>(out int portNumber))
            form.Port = portNumber.ToString();

        // PrivateKey and Passphrase are deliberately not populated. The store never returns a
        // secret to a read model, and leaving them blank is what makes "save without retyping
        // the key" work.

        if (node["allowedLocations"] is JsonArray arr)
        {
            // TryGetValue, not GetValue: a stored array holding a number or an object would
            // otherwise throw out here, past the parse guard above, and take the whole tab down
            // over one malformed entry. Non-strings are skipped instead.
            var values = new List<string>();
            foreach (var element in arr)
            {
                if (element is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text);
                }
            }

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

            case ConnectionProvider.Sftp:
                node["host"] = Host?.Trim() ?? "";
                node["port"] = ParsePort(Port);
                node["username"] = Username?.Trim() ?? "";
                node["allowedRoot"] = AllowedRoot?.Trim() ?? "";

                // Carried forward rather than rewritten. The connector owns this value — it
                // records it on first connect and compares against it thereafter — so an
                // ordinary save must not disturb it. Dropping it here would silently re-arm
                // trust on first use on the next sync, which is the one thing pinning exists
                // to prevent.
                if (!ForgetHostKey && !Blank(HostKeyFingerprint))
                    node["hostKeyFingerprint"] = HostKeyFingerprint!.Trim();
                break;
        }

        // A root plus a subpath check is how the on-disk providers are bounded; allowedLocations
        // is the cloud equivalent and does not apply to them.
        if (IsCloudProvider)
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

    /// <summary>
    /// The credential to store, or null to leave any existing one untouched.
    /// </summary>
    /// <remarks>
    /// SFTP only, and that is the whole point. #371 removed the secret field from this form
    /// because Connapse does not accept pasted cloud keys; an SSH key for a server you run is a
    /// different thing from an AWS access key, but only if the distinction is enforced rather
    /// than described. Returning null for every other provider is where it is enforced.
    /// </remarks>
    public string? ToSecretJson()
    {
        if (Provider != ConnectionProvider.Sftp || Blank(PrivateKey))
            return null;

        return new SftpCredential
        {
            PrivateKey = PrivateKey!.Trim(),
            Passphrase = Blank(Passphrase) ? null : Passphrase
        }.ToSecretJson();
    }

    /// <summary>Returns the first problem with the form, or null when it is ready to save.</summary>
    /// <param name="isNew">
    /// A new SFTP connection must carry a key; an existing one may be saved without retyping it.
    /// </param>
    public string? Validate(bool isNew = true)
    {
        if (Blank(Name)) return "A name is required.";

        return Provider switch
        {
            ConnectionProvider.Filesystem when Blank(AllowedRoot) => "Choose an allowed root.",
            ConnectionProvider.AzureBlob when Blank(StorageAccountName) => "A storage account is required.",

            ConnectionProvider.Sftp when Blank(Host) => "A host is required.",
            ConnectionProvider.Sftp when Blank(Username) => "A username is required.",
            ConnectionProvider.Sftp when Blank(AllowedRoot) => "An allowed root is required.",
            ConnectionProvider.Sftp when !Blank(Port) && ParsePort(Port) is < 1 or > 65535 =>
                "The port must be between 1 and 65535.",
            ConnectionProvider.Sftp when isNew && Blank(PrivateKey) => "A private key is required.",

            _ => null
        };
    }

    /// <summary>
    /// The configured port, or 22. Anything unparseable becomes 0, which
    /// <see cref="Validate"/> then refuses — rather than silently falling back to 22 and
    /// connecting somewhere the operator did not ask for.
    /// </summary>
    private static int ParsePort(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? 22
        : int.TryParse(raw.Trim(), out int port) ? port
        : 0;

    private static string? Str(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}


