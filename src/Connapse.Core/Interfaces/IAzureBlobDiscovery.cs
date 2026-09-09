namespace Connapse.Core.Interfaces;

/// <summary>A storage account Connapse's identity can see, with what the connection form needs.</summary>
/// <param name="Name">The account name.</param>
/// <param name="BlobEndpoint">The primary blob endpoint, e.g. <c>https://name.blob.core.windows.net</c>.</param>
/// <param name="ResourceGroup">The resource group it lives in, for telling same-named accounts apart.</param>
/// <param name="ResourceId">The ARM resource id — the scope a role assignment is written against.</param>
/// <param name="IsHnsEnabled">Whether the account is Data Lake Gen2 (hierarchical namespace), whose
/// per-user permissions are POSIX ACLs rather than RBAC alone.</param>
public record AzureStorageAccountInfo(
    string Name,
    string BlobEndpoint,
    string ResourceGroup,
    string ResourceId,
    bool IsHnsEnabled);

/// <summary>
/// The outcome of asking Azure something, separating the cases that need different advice: it
/// worked, Connapse has no Azure identity configured, or it has one and Azure refused.
/// </summary>
/// <remarks>Mirrors <see cref="AwsProbe{T}"/>; the same three-way split matters for the same reason —
/// "set up the identity" and "grant the identity a role" send the operator to different places.</remarks>
public record AzureProbe<T>(T? Value, AzureProbeOutcome Outcome, string? Detail = null)
{
    public bool Succeeded => Outcome == AzureProbeOutcome.Succeeded;

    public static AzureProbe<T> Ok(T value) => new(value, AzureProbeOutcome.Succeeded);

    public static AzureProbe<T> NotConfigured(string? detail = null) =>
        new(default, AzureProbeOutcome.NotConfigured, detail);

    public static AzureProbe<T> Denied(string? detail = null) =>
        new(default, AzureProbeOutcome.Denied, detail);

    public static AzureProbe<T> Failed(string? detail = null) =>
        new(default, AzureProbeOutcome.Failed, detail);

    public static AzureProbe<T> Unusable(string? detail = null) =>
        new(default, AzureProbeOutcome.Unusable, detail);
}

public enum AzureProbeOutcome
{
    Succeeded = 0,

    /// <summary>No Azure identity is set up — nothing to ask with.</summary>
    NotConfigured = 1,

    /// <summary>The identity works, and Azure refused the call: a missing role assignment.</summary>
    Denied = 2,

    /// <summary>Something else — a timeout, a network fault, an unexpected error.</summary>
    Failed = 3,

    /// <summary>The identity is configured but cannot be used from this host: its certificate file
    /// is missing or unreadable, or the configuration is only partly filled in. Nothing was asked of
    /// Azure; fixing the file or the settings is the whole remedy.</summary>
    Unusable = 4
}

/// <summary>
/// Reads what Connapse's own Azure identity can see, so the connection form can be filled in
/// rather than typed. Every call is read-only.
/// </summary>
public interface IAzureBlobDiscovery
{
    /// <summary>
    /// Asks Entra for a token as Connapse's own identity, proving the credential is accepted:
    /// the certificate is registered on the app and not expired, or the managed identity exists.
    /// </summary>
    /// <remarks>
    /// Authentication only — a token is issued whether or not any role is assigned, so a pass
    /// says "Entra accepts this identity", not "it can read a container". The value is a sentence
    /// saying what was verified. <see cref="AzureProbeOutcome.Denied"/> means Entra refused the
    /// credential itself (an <c>AADSTS</c> error), which only setting access up again can fix;
    /// <see cref="AzureProbeOutcome.Failed"/> means the check could not complete.
    /// </remarks>
    Task<AzureProbe<string>> CheckAccessAsync(CancellationToken ct = default);

    /// <summary>
    /// The same check for settings that are not stored yet — a form about to be saved, or a
    /// replacement certificate about to be promoted — so nothing that works is replaced by
    /// something Entra rejects. Also used for the sign-in application, whose identity has the
    /// same shape. Never answers from a cached token.
    /// </summary>
    Task<AzureProbe<string>> CheckAccessAsync(AzureProviderSettings candidate, CancellationToken ct = default);

    /// <summary>
    /// Every storage account in the configured subscription the identity may read metadata for.
    /// </summary>
    /// <remarks>Needs <c>Microsoft.Storage/storageAccounts/read</c> at subscription scope. A denial
    /// must leave the operator typing an account name, never blocked.</remarks>
    Task<AzureProbe<IReadOnlyList<AzureStorageAccountInfo>>> ListStorageAccountsAsync(CancellationToken ct = default);

    /// <summary>The containers in an account, by its blob endpoint.</summary>
    /// <remarks>Needs <c>Microsoft.Storage/storageAccounts/blobServices/containers/read</c> on the
    /// account — a control-plane action even though the call itself is data-plane.</remarks>
    Task<AzureProbe<IReadOnlyList<string>>> ListContainersAsync(string blobEndpoint, CancellationToken ct = default);
}
