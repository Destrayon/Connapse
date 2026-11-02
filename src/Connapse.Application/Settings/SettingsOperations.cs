using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Vectors;

namespace Connapse.Application.Settings;

/// <summary>Why reading or changing settings didn't succeed.</summary>
public enum SettingsStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "settings-category-not-found", "Settings category not found")]
    NotFound,

    [Failure(400, "invalid-settings", "Invalid settings")]
    Invalid,

    [Failure(409, "provider-owned-settings", "Changed through provider setup")]
    ProviderOwned,

    [Failure(412, "settings-changed", "Settings changed since they were read")]
    Changed,
}

/// <summary>
/// A settings category: the name the API and the page use, the key it is stored under, and its type. A
/// secret (an API key or a password) is never read back out, and a change that leaves one out
/// keeps the stored value, so reading a category and writing it back can't erase a key it didn't show.
/// </summary>
public abstract class SettingsCategory(string name, string key)
{
    public string Name { get; } = name;

    public string Key { get; } = key;

    public abstract Type Type { get; }

    /// <summary>The string properties holding secrets: an API key or a password, by name.</summary>
    public IReadOnlyList<PropertyInfo> Secrets => SettingsSecrets.Of(Type);

    /// <summary>
    /// The string properties saying where a secret is sent: the provider and every address (a URL or an
    /// endpoint), by name. Each provider falls back on the others' keys and addresses, so any of them can
    /// decide where any key goes.
    /// </summary>
    public IReadOnlyList<PropertyInfo> Destinations => SettingsSecrets.DestinationsOf(Type);

    /// <summary>
    /// The settings.update body that saves <paramref name="edited"/> from a page that opened with
    /// <paramref name="opened"/>: a secret the page didn't change is sent as null, which keeps the one in effect,
    /// so a key changed elsewhere since the page opened isn't written back over; one the page emptied is sent
    /// empty, which clears it; one it changed is sent.
    /// </summary>
    public JsonElement BodyFrom(object edited, object opened)
    {
        var body = JsonSerializer.SerializeToNode(edited, Type)!.AsObject();
        foreach (var secret in Secrets)
        {
            var value = (string?)secret.GetValue(edited);
            body[secret.Name] = Equals(value, secret.GetValue(opened)) ? null : value ?? "";
        }
        return JsonSerializer.SerializeToElement(body);
    }

    /// <summary>A copy of <paramref name="settings"/> with every secret left out, so the original is never changed.</summary>
    public object WithoutSecrets(object settings)
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(settings, Type), Type)!;
        foreach (var secret in Secrets)
            secret.SetValue(copy, null);
        return copy;
    }

    /// <summary>
    /// The version of <paramref name="settings"/>: a SHA-256 of the store's <paramref name="revision"/> (null when
    /// they are configured, not stored) and of the settings without their secrets. The revision changes on every
    /// write, so a change to a secret alone changes the version too; the secrets themselves are never hashed, so
    /// the version carries nothing derived from a key.
    /// </summary>
    public string VersionOf(object settings, string? revision) =>
        Convert.ToHexStringLower(SHA256.HashData(
            [.. Encoding.UTF8.GetBytes(revision is null ? "configured\n" : $"stored:{revision}\n"),
             .. JsonSerializer.SerializeToUtf8Bytes(WithoutSecrets(settings), Type)]));

    /// <summary>
    /// <paramref name="tested"/> with each secret it leaves out filled from the settings in effect, but only
    /// when every destination is the same as theirs: a saved key goes only to the provider and addresses it
    /// was saved for. Anything else needs its key typed again.
    /// </summary>
    internal async Task<object> WithSavedSecretsAsync(ISettingsStore store, IConfiguredSettings configured, object tested, CancellationToken ct)
    {
        var inEffect = await InEffectAsync(store, configured, ct);
        // Exactly equal: providers read an empty override as set and a missing one as falling back, so null
        // and "" can be different addresses.
        if (Destinations.Any(d => !string.Equals((string?)d.GetValue(tested), (string?)d.GetValue(inEffect), StringComparison.Ordinal)))
            return tested;
        foreach (var secret in Secrets)
            if (secret.GetValue(tested) is null)
                secret.SetValue(tested, secret.GetValue(inEffect));
        return tested;
    }

    /// <summary>The settings in effect, secrets included: stored, or configured when nothing is.</summary>
    internal abstract Task<object> InEffectAsync(ISettingsStore store, IConfiguredSettings configured, CancellationToken ct);

    /// <summary>The settings in effect without their secrets, and their version.</summary>
    internal abstract Task<(object Settings, string Version)> ReadAsync(ISettingsStore store, IConfiguredSettings configured, CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="settings"/> and returns them with their new version. With <paramref name="expectedVersions"/>,
    /// only while the settings in effect still have one of those versions, checked under the store's lock so no other
    /// save lands in between; otherwise it throws <see cref="SettingsChangedException"/> and stores nothing.
    /// </summary>
    internal abstract Task<(object Saved, string Version)> WriteAsync(
        ISettingsStore store, IConfiguredSettings configured, object settings, UnsentSecrets unsent,
        IReadOnlyCollection<string>? expectedVersions, CancellationToken ct);

    internal abstract object? Parse(JsonElement body);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        // A property the category doesn't have is refused, so another category's settings, which would
        // otherwise read as all defaults, can't overwrite this one's.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>A category of settings of type <typeparamref name="T"/>.</summary>
public sealed class SettingsCategory<T>(string name, string key) : SettingsCategory(name, key) where T : class, new()
{
    public override Type Type => typeof(T);

    internal override async Task<object> InEffectAsync(ISettingsStore store, IConfiguredSettings configured, CancellationToken ct) =>
        await store.GetAsync<T>(Key, ct) ?? configured.Current<T>();

    // A copy, so the configured value other readers share is never changed.
    internal override async Task<(object Settings, string Version)> ReadAsync(ISettingsStore store, IConfiguredSettings configured, CancellationToken ct)
    {
        var (stored, revision) = await store.GetWithRevisionAsync<T>(Key, ct);
        var settings = WithoutSecrets(stored ?? configured.Current<T>());
        return (settings, VersionOf(settings, stored is null ? null : revision));
    }

    internal override async Task<(object Saved, string Version)> WriteAsync(
        ISettingsStore store, IConfiguredSettings configured, object settings, UnsentSecrets unsent,
        IReadOnlyCollection<string>? expectedVersions, CancellationToken ct)
    {
        var (saved, revision) = await store.UpdateWithRevisionAsync<T>(Key, (stored, storedRevision) =>
        {
            // A secret left out keeps the one in effect: stored, or configured when nothing is stored yet, as a
            // first save of a value read from GET would otherwise erase a key that came from configuration.
            var current = stored ?? configured.Current<T>();
            if (expectedVersions is not null && !expectedVersions.Contains(VersionOf(current, stored is null ? null : storedRevision)))
                throw new SettingsChangedException();
            var incoming = (T)settings;
            foreach (var secret in Secrets)
            {
                if (secret.GetValue(incoming) is null && unsent == UnsentSecrets.Keep)
                    secret.SetValue(incoming, secret.GetValue(current));
                else if (secret.GetValue(incoming) is "")
                    secret.SetValue(incoming, null);
            }
            return incoming;
        }, ct);
        return (saved, VersionOf(saved, revision));
    }

    internal override object? Parse(JsonElement body) => JsonSerializer.Deserialize<T>(body.GetRawText(), Json);
}

/// <summary>
/// What a change means by a secret it leaves null. REST callers read secrets back as null, so for them it keeps
/// the one in effect, and an empty string clears it; the page holds every value, so for it null clears it.
/// </summary>
public enum UnsentSecrets
{
    Keep,
    Clear,
}

/// <summary>Every settings category, as the API names it.</summary>
public static class SettingsCategories
{
    public const string Embedding = "embedding";

    /// <summary>
    /// Each is stored under its own name. The provider sign-in categories (azure, azuread, samlsignin,
    /// identitycenter) are not here: they change who can sign in, and provider setup owns them.
    /// </summary>
    public static readonly IReadOnlyList<SettingsCategory> All =
    [
        new SettingsCategory<EmbeddingSettings>(Embedding, Embedding),
        new SettingsCategory<ChunkingSettings>("chunking", "chunking"),
        new SettingsCategory<SearchSettings>("search", "search"),
        new SettingsCategory<LlmSettings>("llm", "llm"),
        new SettingsCategory<UploadSettings>("upload", "upload"),
    ];

    /// <summary>
    /// The provider sign-in categories that can be read here, each with the provider route that changes it. Saving one
    /// can switch per-user enforcement on, which only provider setup does, so settings.update and settings.reset refuse them.
    /// </summary>
    public static readonly IReadOnlyList<ProviderOwnedCategory> ProviderOwned =
    [
        new(new SettingsCategory<SamlSignInSettings>("samlsignin", "samlsignin"), "/api/v1/providers/aws/permissions"),
        new(new SettingsCategory<IdentityCenterSettings>("identitycenter", "identitycenter"), "/api/v1/providers/aws/identity-center"),
        new(new SettingsCategory<AzureProviderSettings>("azure", "azure"), "/api/v1/providers/azure/access"),
        new(new SettingsCategory<AzureAdSignInSettings>("azuread", "azuread"), "/api/v1/providers/azure/permissions"),
    ];

    /// <summary>The provider-owned category with this name in any case, or null.</summary>
    public static ProviderOwnedCategory? FindProviderOwned(string? name) =>
        ProviderOwned.FirstOrDefault(c => string.Equals(c.Category.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The category with this name in any case, or null.</summary>
    public static SettingsCategory? Find(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The category whose settings are of this type, or null.</summary>
    public static SettingsCategory? Of(Type type) => All.FirstOrDefault(c => c.Type == type);
}

/// <summary>A provider sign-in category that settings.get reads, and the provider route that changes it.</summary>
public sealed record ProviderOwnedCategory(SettingsCategory Category, string ChangedAt)
{
    /// <summary>The refusal for changing it through the settings operations.</summary>
    public string Refusal => $"The {Category.Name} settings are changed through provider setup: PUT or DELETE {ChangedAt}.";
}

/// <summary>
/// A category's settings as stored, or as configured when nothing is; secrets are left out. <see cref="Version"/>
/// identifies them (<see cref="SettingsCategory.VersionOf"/>), so a change can be made only while they are unchanged.
/// </summary>
public sealed record SettingsView(string Category, object Settings, string Version);

/// <summary>
/// A saved change and the <see cref="Version"/> the settings now have. <see cref="LegacyModels"/> lists the embedding
/// models other than the one now configured that still have vectors, when the embedding settings changed and there are any.
/// </summary>
public sealed record SettingsChange(string Category, IReadOnlyList<EmbeddingModelInfo> LegacyModels, string Version);

/// <summary>A change made from settings that are no longer the ones in effect; nothing was stored.</summary>
internal sealed class SettingsChangedException : Exception;

/// <summary>The token scope the settings operations need.</summary>
public static class SettingsScope
{
    public const string Name = "admin:settings";
}

/// <summary>Reads one settings category. Administrators only, since the settings say how Connapse reaches its providers.</summary>
[Operation("settings.get", Scope = SettingsScope.Name, Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetSettings(ISettingsStore store, IConfiguredSettings configured, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<SettingsView, SettingsStatus>> RunAsync(OperationContext context, string category, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<SettingsView, SettingsStatus>.Fail(SettingsStatus.Forbidden, "Only administrators can read settings.");
        if ((SettingsCategories.Find(category) ?? SettingsCategories.FindProviderOwned(category)?.Category) is not { } found)
            return Attempt<SettingsView, SettingsStatus>.Fail(SettingsStatus.NotFound, $"Unknown settings category: {category}");

        var (settings, version) = await found.ReadAsync(store, configured, ct);
        return Attempt<SettingsView, SettingsStatus>.Succeed(new SettingsView(found.Name, settings, version));
    }
}

/// <summary>
/// Replaces one settings category. A secret left out keeps the one in effect unless the caller says it clears
/// (see <see cref="UnsentSecrets"/>), and an empty one clears it. Changing the embedding settings
/// schedules the new model's vector index and reports the other models that still have vectors.
/// Given the versions the caller read, the change is refused if the settings changed since, so two
/// administrators saving close together can't overwrite each other.
/// </summary>
[Operation("settings.update", Scope = SettingsScope.Name, Risk = RiskTier.Credential, Mcp = McpExposure.Generic)]
public sealed class UpdateSettings(
    ISettingsStore store, IConfiguredSettings configured, IVectorIndexReconciler indexes, VectorModelDiscovery models,
    IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public const string UpdatedEvent = "settings.updated";

    /// <param name="expectedVersions">
    /// Null saves whatever is in effect now. Otherwise the save goes ahead only while the settings in effect have one
    /// of these versions (from settings.get), and an empty list never does.
    /// </param>
    public async Task<Attempt<SettingsChange, SettingsStatus>> RunAsync(
        OperationContext context, string category, JsonElement body, UnsentSecrets unsent = UnsentSecrets.Keep,
        IReadOnlyCollection<string>? expectedVersions = null, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<SettingsChange, SettingsStatus>.Fail(SettingsStatus.Forbidden, "Only administrators can change settings.");
        if (SettingsCategories.FindProviderOwned(category) is { } owned)
            return Attempt<SettingsChange, SettingsStatus>.Fail(SettingsStatus.ProviderOwned, owned.Refusal);
        if (SettingsCategories.Find(category) is not { } found)
            return Attempt<SettingsChange, SettingsStatus>.Fail(SettingsStatus.NotFound, $"Unknown settings category: {category}");

        object? settings;
        try
        {
            settings = body.ValueKind is JsonValueKind.Object ? found.Parse(body) : null;
        }
        catch (JsonException ex)
        {
            return Attempt<SettingsChange, SettingsStatus>.Fail(SettingsStatus.Invalid, $"Invalid JSON: {ex.Message}");
        }
        if (settings is null)
            return Attempt<SettingsChange, SettingsStatus>.Fail(SettingsStatus.Invalid, $"The {found.Name} settings must be a JSON object.");
        // The category's own rules: its data annotations, and IValidatableObject where it has one.
        var problems = new List<ValidationResult>();
        if (!Validator.TryValidateObject(settings, new ValidationContext(settings), problems, validateAllProperties: true))
            return Attempt<SettingsChange, SettingsStatus>.Fail(SettingsStatus.Invalid,
                string.Join(" ", problems.Select(p => p.ErrorMessage)), FieldOf(problems[0]));

        object saved;
        string version;
        try
        {
            (saved, version) = await found.WriteAsync(store, configured, settings, unsent, expectedVersions, ct);
        }
        catch (SettingsChangedException)
        {
            return Attempt<SettingsChange, SettingsStatus>.Fail(SettingsStatus.Changed,
                $"The {found.Name} settings were changed after you read them. Read them again and make your change again.");
        }
        // The values are not recorded: they hold provider keys.
        await audit.LogAsync(UpdatedEvent, "settings", found.Name, null, ct);

        if (saved is not EmbeddingSettings embedding)
            return Attempt<SettingsChange, SettingsStatus>.Succeed(new SettingsChange(found.Name, [], version));

        indexes.Schedule();
        string current = EmbeddingIdentity.For(embedding);
        var legacy = (await models.GetModelsAsync(containerId: null, ct))
            .Where(m => !string.Equals(m.ModelId, current, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Attempt<SettingsChange, SettingsStatus>.Succeed(new SettingsChange(found.Name, legacy, version));
    }

    /// <summary>The property a validation problem names, as JSON writes it (camel case), or null.</summary>
    private static string? FieldOf(ValidationResult problem) =>
        problem.MemberNames.FirstOrDefault() is { Length: > 0 } member ? char.ToLowerInvariant(member[0]) + member[1..] : null;
}

/// <summary>
/// Resets one settings category to what configuration gives it, by removing what is stored. Resetting the
/// embedding settings schedules the configured model's vector index, as changing them does.
/// </summary>
[Operation("settings.reset", Scope = SettingsScope.Name, Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class ResetSettings(ISettingsStore store, IVectorIndexReconciler indexes, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public const string ResetEvent = "settings.reset";

    public async Task<Attempt<string, SettingsStatus>> RunAsync(OperationContext context, string category, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<string, SettingsStatus>.Fail(SettingsStatus.Forbidden, "Only administrators can reset settings.");
        if (SettingsCategories.FindProviderOwned(category) is { } owned)
            return Attempt<string, SettingsStatus>.Fail(SettingsStatus.ProviderOwned, owned.Refusal);
        if (SettingsCategories.Find(category) is not { } found)
            return Attempt<string, SettingsStatus>.Fail(SettingsStatus.NotFound, $"Unknown settings category: {category}");

        await store.ResetAsync(found.Key, ct);
        await audit.LogAsync(ResetEvent, "settings", found.Name, null, ct);
        if (found.Name == SettingsCategories.Embedding)
            indexes.Schedule();
        return Attempt<string, SettingsStatus>.Succeed(found.Name);
    }
}
