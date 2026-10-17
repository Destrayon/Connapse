namespace Connapse.Web.Components.Providers;

public enum ProviderFieldKind { Text, Multiline, Secret, MultilineSecret, Choice }

/// <summary>
/// One configuration field, described once: its label, group, whether it is a secret, whether it
/// is required, its help text, and where the setup guide explains it. <see cref="ProviderField"/>
/// renders it, a group's list gives its fields' order and its heading, and a test checks the guide
/// covers every field, so the page and the guide cannot drift.
/// </summary>
/// <param name="Id">The input's id.</param>
/// <param name="Label">The label, in the provider's own words.</param>
/// <param name="Kind">Plain text, a multiline box, or a secret (always <c>SecretTextArea</c>).</param>
/// <param name="Required">Required fields carry no marker; optional ones say "(optional)".</param>
/// <param name="Help">One or two sentences: what it is and where to find it.</param>
/// <param name="Placeholder">A format example, never a stand-in for the label.</param>
/// <param name="KeptPlaceholder">For a secret with a value stored: shown instead of <paramref name="Placeholder"/>.</param>
/// <param name="InputMode">The input's <c>inputmode</c>, such as <c>numeric</c>.</param>
/// <param name="Rows">Rows for a multiline field.</param>
/// <param name="MaxWidth">A CSS max-width for short values.</param>
/// <param name="Group">The heading the field sits under.</param>
/// <param name="GuideUrl">The setup guide's section on this field, linked beside its help.</param>
/// <param name="GuideLinkText">The words of that link: what the reader will find there.</param>
public sealed record ProviderFieldSpec(
    string Id,
    string Label,
    ProviderFieldKind Kind,
    bool Required,
    string Help,
    string Group,
    string GuideUrl,
    string GuideLinkText,
    string? Placeholder = null,
    string? KeptPlaceholder = null,
    string? InputMode = null,
    int Rows = 3,
    string? MaxWidth = null)
{
    public bool IsSecret => Kind is ProviderFieldKind.Secret or ProviderFieldKind.MultilineSecret;
}

/// <summary>The GitHub provider's fields, in the order an administrator gathers them.</summary>
public static class GitHubFieldSpecs
{
    private const string Guide = "https://github.com/Destrayon/Connapse/blob/main/docs/github-setup.md";
    private const string ManualValues = "Manual values";
    private const string NewSource = "New source";

    public static readonly ProviderFieldSpec AppId = new(
        "github-app-id", "App ID", ProviderFieldKind.Text, Required: true,
        "The number shown at the top of the App's page on GitHub.", ManualValues, Guide + "#manual-values", "Where to find this",
        Placeholder: "123456", InputMode: "numeric", MaxWidth: "16rem");

    public static readonly ProviderFieldSpec PrivateKey = new(
        "github-app-key", "Private key (.pem file)", ProviderFieldKind.MultilineSecret, Required: true,
        "On the App's page, under Private keys, choose Generate a private key and paste the whole file. Stored encrypted and never shown again.",
        ManualValues, Guide + "#manual-values", "Where to find this",
        Placeholder: "-----BEGIN RSA PRIVATE KEY-----", KeptPlaceholder: "Kept unless you paste a new key", Rows: 5);

    public static readonly ProviderFieldSpec ClientSecret = new(
        "github-app-secret", "Client secret", ProviderFieldKind.Secret, Required: false,
        "Lets people link their GitHub accounts, which private repositories need. On the App's page, under Client secrets, choose Generate a new client secret.",
        ManualValues, Guide + "#manual-values", "Where to find this",
        KeptPlaceholder: "Kept unless you paste a new one", MaxWidth: "28rem");

    public static readonly ProviderFieldSpec Repository = new(
        "github-repo", "Repository", ProviderFieldKind.Text, Required: true,
        "owner/repo or its github.com address.", NewSource, Guide + "#source", "Adding a repository",
        Placeholder: "owner/repo");

    public static readonly ProviderFieldSpec DocPatterns = new(
        "github-patterns", "File patterns", ProviderFieldKind.Multiline, Required: false,
        "File names to index, one per line, with * as a wildcard (for example *.md or CHANGELOG*). Leave blank for *.md and *.markdown.",
        NewSource, Guide + "#source", "How patterns work",
        Placeholder: "README.md", Rows: 2);

    /// <summary>The manual App values, in the order they are rendered.</summary>
    public static IReadOnlyList<ProviderFieldSpec> ManualApp { get; } = [AppId, PrivateKey, ClientSecret];

    /// <summary>Every field, for the check that the setup guide documents each one.</summary>
    public static IReadOnlyList<ProviderFieldSpec> All { get; } = [AppId, PrivateKey, ClientSecret, Repository, DocPatterns];
}

/// <summary>The Atlassian linking app's fields, in the order an administrator copies them from the developer console.</summary>
public static class AtlassianLinkAppFieldSpecs
{
    public const string Guide = "https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md";
    public const string GuideSection = Guide + "#1-linking-app";
    public const string TroubleshootingRejected = Guide + "#linking-app-rejected";
    // The same heading GitHub's hand-entered values sit under.
    private const string ManualValues = "Manual values";

    // Atlassian publishes no fixed format or length for these, so the shape is described loosely.
    public static readonly ProviderFieldSpec ClientId = new(
        "atlassian-app-client-id", "Client ID", ProviderFieldKind.Text, Required: true,
        "Under Settings on the app in the Atlassian developer console: one string of letters and digits, with no spaces.",
        ManualValues, GuideSection, "Where to find this", MaxWidth: "28rem");

    public static readonly ProviderFieldSpec ClientSecret = new(
        "atlassian-app-client-secret", "Client secret", ProviderFieldKind.Secret, Required: true,
        "Labelled Secret, below the client ID: one long string of characters. Stored encrypted and never shown again.",
        ManualValues, GuideSection, "Where to find this", MaxWidth: "28rem");

    /// <summary>Every field, in the order they are rendered, for the check that the setup guide documents each one.</summary>
    public static IReadOnlyList<ProviderFieldSpec> All { get; } = [ClientId, ClientSecret];
}

/// <summary>
/// The custom provider's and custom connection's fields: the values an administrator copies from the middle man
/// they run. One guide covers all of them, in the order they are set up.
/// </summary>
public static class CustomSourceFieldSpecs
{
    public const string Guide = "https://github.com/Destrayon/Connapse/blob/main/docs/custom-source-setup.md";
    public const string Prerequisites = Guide + "#before-you-start";
    public const string Troubleshooting = Guide + "#troubleshooting";
    private const string MiddleMan = "Middle man";
    private const string Connection = "Custom connection";
    private const string Source = "Custom source";

    public static readonly ProviderFieldSpec ProviderName = new(
        "custom-provider-name", "Name", ProviderFieldKind.Text, Required: true,
        "What the Providers and Connections pages call this middle man. Filled in from the name your middle man reports.",
        MiddleMan, Guide + "#1-custom-provider", "Setting up a provider", Placeholder: "Acme bridge", MaxWidth: "28rem");

    public static readonly ProviderFieldSpec ProviderBaseUrl = new(
        "custom-provider-url", "Base URL", ProviderFieldKind.Text, Required: true,
        "The address of your middle man. It must use HTTPS unless an administrator allowed plain HTTP for its host.",
        MiddleMan, Guide + "#1-custom-provider", "Where to find this", Placeholder: "https://bridge.example.com");

    public static readonly ProviderFieldSpec ProviderToken = new(
        "custom-provider-token", "Token", ProviderFieldKind.Secret, Required: true,
        "The bearer token you configured on your middle man. Stored encrypted and never shown again.",
        MiddleMan, Guide + "#1-custom-provider", "Where to find this", MaxWidth: "28rem");

    public static readonly ProviderFieldSpec ConnectionProvider = new(
        "custom-provider", "Custom provider", ProviderFieldKind.Choice, Required: true,
        "Pick a custom provider for permission checks, or No provider (content only) to enter a middle man here.",
        Connection, Guide + "#2-custom-connection", "Which to choose", MaxWidth: "32rem");

    public static readonly ProviderFieldSpec ConnectionBaseUrl = new(
        "custom-connection-url", "Base URL", ProviderFieldKind.Text, Required: true,
        "The address of the middle man this connection's sources read. It must use HTTPS unless an administrator allowed plain HTTP for its host.",
        Connection, Guide + "#2-custom-connection", "Adding a connection", Placeholder: "https://bridge.example.com");

    public static readonly ProviderFieldSpec ConnectionToken = new(
        "custom-connection-token", "Token", ProviderFieldKind.Secret, Required: true,
        "The bearer token you configured on your middle man. Connapse checks it before saving, stores it encrypted and never shows it again.",
        Connection, Guide + "#2-custom-connection", "Where to find this", MaxWidth: "28rem");

    public static readonly ProviderFieldSpec SourceScope = new(
        "custom-source-scope", "Scope", ProviderFieldKind.Choice, Required: true,
        "Each scope your middle man offers becomes its own source.",
        Source, Guide + "#3-custom-source", "Adding a custom source");

    public static readonly ProviderFieldSpec SourceName = new(
        "custom-source-name", "Name", ProviderFieldKind.Text, Required: true,
        "What Connapse calls this source. Filled in from the scope's name.",
        Source, Guide + "#3-custom-source", "Adding a custom source");

    public static readonly ProviderFieldSpec SourceDescription = new(
        "custom-source-description", "Description", ProviderFieldKind.Text, Required: false,
        "Shown with the source in lists and to agents choosing where to search.",
        Source, Guide + "#3-custom-source", "Adding a custom source");

    public static readonly ProviderFieldSpec SourceInterval = new(
        "custom-source-interval", "Sync interval in seconds", ProviderFieldKind.Text, Required: false,
        "At least 60. Leave empty to use the configured default; the middle man may ask for a longer one.",
        Source, Guide + "#3-custom-source", "Adding a custom source", Placeholder: "300", InputMode: "numeric", MaxWidth: "12rem");

    /// <summary>Every field, for the check that the setup guide documents each one.</summary>
    public static IReadOnlyList<ProviderFieldSpec> All { get; } =
        [ProviderName, ProviderBaseUrl, ProviderToken, ConnectionProvider, ConnectionBaseUrl, ConnectionToken,
            SourceScope, SourceName, SourceDescription, SourceInterval];
}
