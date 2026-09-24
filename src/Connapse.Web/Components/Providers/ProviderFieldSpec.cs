namespace Connapse.Web.Components.Providers;

public enum ProviderFieldKind { Text, Multiline, Secret, MultilineSecret }

/// <summary>
/// One configuration field, described once: its label, whether it is a secret, whether it is
/// required, its help text, and where the setup guide explains it. <see cref="ProviderField"/>
/// renders it, and a test checks the guide covers every field, so the page and the guide cannot drift.
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
public sealed record ProviderFieldSpec(
    string Id,
    string Label,
    ProviderFieldKind Kind,
    bool Required,
    string Help,
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
    public static readonly ProviderFieldSpec AppId = new(
        "github-app-id", "App ID", ProviderFieldKind.Text, Required: true,
        "The number shown at the top of the App's page on GitHub.",
        Placeholder: "123456", InputMode: "numeric", MaxWidth: "16rem");

    public static readonly ProviderFieldSpec PrivateKey = new(
        "github-app-key", "Private key (.pem file)", ProviderFieldKind.MultilineSecret, Required: true,
        "On the App's page, under Private keys, choose Generate a private key and paste the whole file. Stored encrypted and never shown again.",
        Placeholder: "-----BEGIN RSA PRIVATE KEY-----", KeptPlaceholder: "Kept unless you paste a new key", Rows: 5);

    public static readonly ProviderFieldSpec ClientSecret = new(
        "github-app-secret", "Client secret", ProviderFieldKind.Secret, Required: false,
        "Lets people link their GitHub accounts, which private repositories need. On the App's page, under Client secrets, choose Generate a new client secret.",
        KeptPlaceholder: "Kept unless you paste a new one", MaxWidth: "28rem");

    public static readonly ProviderFieldSpec Repository = new(
        "github-repo", "Repository", ProviderFieldKind.Text, Required: true,
        "owner/repo or its github.com address.",
        Placeholder: "owner/repo");

    public static readonly ProviderFieldSpec DocPatterns = new(
        "github-patterns", "File patterns", ProviderFieldKind.Multiline, Required: false,
        "File names to index, one per line, with * as a wildcard (for example *.md or CHANGELOG*). Leave blank for *.md and *.markdown.",
        Placeholder: "README.md", Rows: 2);

    /// <summary>Every field, for the check that the setup guide documents each one.</summary>
    public static IReadOnlyList<ProviderFieldSpec> All { get; } = [AppId, PrivateKey, ClientSecret, Repository, DocPatterns];
}
