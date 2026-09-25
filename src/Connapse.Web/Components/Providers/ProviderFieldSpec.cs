namespace Connapse.Web.Components.Providers;

public enum ProviderFieldKind { Text, Multiline, Secret, MultilineSecret }

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
