using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Application.Providers;

/// <summary>Why a custom provider action didn't succeed.</summary>
public enum CustomProviderStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "custom-provider-not-found", "Custom provider not found")]
    NotFound,

    [Failure(400, "invalid-custom-provider", "Invalid custom provider")]
    Invalid,

    [Failure(409, "custom-provider-conflict", "Custom provider conflict")]
    Conflict,

    [Failure(409, "custom-provider-in-use", "Custom provider in use")]
    InUse,
}

/// <summary>A custom provider and the connections that check permissions with it.</summary>
public sealed record CustomProviderListing(CustomProvider Provider, IReadOnlyList<CustomProviderUse> Connections);

/// <summary>A connection that uses a custom provider; <paramref name="TokenRefused"/> when its last sync had the provider's token refused.</summary>
public sealed record CustomProviderUse(Guid ConnectionId, string Name, bool TokenRefused);

/// <summary>What to check a custom provider's source server with. A missing address is the saved one; a missing token is the saved one, which is only ever sent to the saved address.</summary>
public sealed record CustomProviderCheckRequest(string? BaseUrl = null, string? Token = null)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"BaseUrl = {BaseUrl}, Token = {(Token is null ? "null" : "***")}");
        return true;
    }
}

/// <summary>A new custom provider. A blank name is the one its source server reports, or its host.</summary>
public sealed record CreateCustomProviderRequest(string BaseUrl, string Token, string? Name = null)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"BaseUrl = {BaseUrl}, Token = ***, Name = {Name}");
        return true;
    }
}

/// <summary>A custom provider's changes; whatever is left out stays as it is. A new address needs its token.</summary>
public sealed record UpdateCustomProviderRequest(string? Name = null, string? BaseUrl = null, string? Token = null)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, BaseUrl = {BaseUrl}, Token = {(Token is null ? "null" : "***")}");
        return true;
    }
}

/// <summary>A custom provider's new link secret: shown once, and never readable again.</summary>
public sealed record CustomProviderLinkSecret(string LinkSecret)
{
    public override string ToString() => "CustomProviderLinkSecret { LinkSecret = *** }";
}

/// <summary>
/// The custom providers, each with the connections that use it. Settings administrators only; tokens and link
/// secrets never appear here.
/// </summary>
[Operation("providers.custom.list", Scope = "admin:settings", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListCustomProviders(ICustomProviderStore store, IConnectionStore connections, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<IReadOnlyList<CustomProviderListing>, CustomProviderStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<IReadOnlyList<CustomProviderListing>, CustomProviderStatus>.Fail(CustomProviderStatus.Forbidden, CustomProviderRules.Refused);

        IReadOnlyList<CustomProvider> providers = await store.ListAsync(ct);
        ILookup<Guid, CustomProviderUse> uses = await CustomProviderRules.UsesAsync(connections, ct);
        return Attempt<IReadOnlyList<CustomProviderListing>, CustomProviderStatus>.Succeed(
            [.. providers.Select(p => new CustomProviderListing(p, [.. uses[p.Id]]))]);
    }
}

/// <summary>
/// Checks a source server as a custom provider, without saving anything: whether the address is allowed, the token
/// accepted and a wrong one refused, and a real permission check answered. A check that fails is still a result;
/// its <c>error</c> and <c>field</c> say what to change. Checking a saved provider without a token uses its saved
/// token, which is only sent to its saved address. Without a provider id it checks a new one.
/// </summary>
[Operation("providers.custom.check", Scope = "admin:settings", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class CheckCustomProvider(ICustomProviderStore store, ICustomProviderChecker checker, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<CustomProviderCheck, CustomProviderStatus>> RunAsync(
        OperationContext context, Guid? providerId, CustomProviderCheckRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<CustomProviderCheck, CustomProviderStatus>.Fail(CustomProviderStatus.Forbidden, CustomProviderRules.Refused);

        CustomProvider? existing = null;
        if (providerId is { } id && (existing = await store.GetAsync(id, ct)) is null)
            return Attempt<CustomProviderCheck, CustomProviderStatus>.Fail(CustomProviderStatus.NotFound, CustomProviderRules.Missing);

        string? baseUrl = request.BaseUrl ?? existing?.BaseUrl;
        CustomProviderCheck check;
        if (existing is not null && string.IsNullOrEmpty(request.Token))
        {
            (bool usable, string? saved) = await CustomProviderRules.SavedTokenAsync(store, existing, baseUrl, ct);
            check = usable
                ? await checker.CheckAsync(baseUrl, saved, ct)
                : new CustomProviderCheck(null, false, false, Error: CustomProviderRules.SavedTokenRefusal(existing, baseUrl), Field: "secret");
        }
        else
        {
            check = await checker.CheckAsync(baseUrl, request.Token, ct);
        }

        await audit.LogAsync(check is { EndpointOk: true, PermissionsOk: true } ? "custom_provider.check_passed" : "custom_provider.check_failed",
            "custom_provider", existing?.Id.ToString(),
            new { check.BaseUrl, Step = check.EndpointOk ? "permissions" : check.Field ?? "endpoint" }, ct);
        return Attempt<CustomProviderCheck, CustomProviderStatus>.Succeed(check);
    }
}

/// <summary>
/// Adds a custom provider. Its source server is checked first, and nothing is saved unless every step passes;
/// the failure's <c>field</c> is <c>baseUrl</c> or <c>token</c>. Saves the token encrypted.
/// </summary>
[Operation("providers.custom.create", Scope = "admin:settings", Risk = RiskTier.Credential, Mcp = McpExposure.Generic)]
public sealed class CreateCustomProvider(ICustomProviderStore store, ICustomProviderChecker checker, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<CustomProvider, CustomProviderStatus>> RunAsync(
        OperationContext context, CreateCustomProviderRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Forbidden, CustomProviderRules.Refused);
        if (string.IsNullOrWhiteSpace(request.Token))
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Invalid, "Enter the source server's token.", "token");

        CustomProviderCheck check = await checker.CheckAsync(request.BaseUrl, request.Token, ct);
        if (CustomProviderRules.Failed(check) is { } failed)
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Invalid, failed.Detail, failed.Field);

        string name = CustomProviderRules.NameFor(request.Name, check);
        if (CustomProviderRules.InvalidName(name) is { } invalid)
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Invalid, invalid, "name");
        if (await CustomProviderRules.NameTakenAsync(store, name, null, ct))
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Conflict, CustomProviderRules.Taken(name), "name");

        CustomProvider created;
        try
        {
            created = await store.CreateAsync(CustomProviderRules.Save(name, check, request.Token.Trim()), ct);
        }
        catch (InvalidOperationException)
        {
            // Another request took the name between the check above and the insert.
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Conflict, CustomProviderRules.Taken(name), "name");
        }

        await audit.LogAsync("custom_provider.created", "custom_provider", created.Id.ToString(),
            new { created.Name, created.BaseUrl, TokenReplaced = true }, ct);
        return Attempt<CustomProvider, CustomProviderStatus>.Succeed(created);
    }
}

/// <summary>
/// Changes a custom provider's name, address or token. Its source server is checked again before anything is
/// saved, with the saved token when none is given, which is only sent to the saved address. The address can't
/// change while connections use the provider, since their documents came from the current server. A save clears
/// the refused-token mark on the connections that use it, because the check just passed with the saved token.
/// </summary>
[Operation("providers.custom.update", Scope = "admin:settings", Risk = RiskTier.Credential, Mcp = McpExposure.Generic)]
public sealed class UpdateCustomProvider(
    ICustomProviderStore store, IConnectionStore connections, ICustomProviderChecker checker, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<CustomProvider, CustomProviderStatus>> RunAsync(
        OperationContext context, Guid providerId, UpdateCustomProviderRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Forbidden, CustomProviderRules.Refused);
        if (await store.GetAsync(providerId, ct) is not { } existing)
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.NotFound, CustomProviderRules.Missing);

        string name = request.Name?.Trim() is { Length: > 0 } given ? given : existing.Name;
        if (CustomProviderRules.InvalidName(name) is { } invalid)
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Invalid, invalid, "name");

        bool tokenReplaced = !string.IsNullOrWhiteSpace(request.Token);
        string? baseUrl = request.BaseUrl ?? existing.BaseUrl;
        string? token = request.Token;
        if (!tokenReplaced)
        {
            (bool usable, string? saved) = await CustomProviderRules.SavedTokenAsync(store, existing, baseUrl, ct);
            if (!usable)
                return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Invalid,
                    CustomProviderRules.SavedTokenRefusal(existing, baseUrl), "token");
            token = saved;
        }

        CustomProviderCheck check = await checker.CheckAsync(baseUrl, token, ct);
        if (CustomProviderRules.Failed(check) is { } failed)
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Invalid, failed.Detail, failed.Field);

        if (await CustomProviderRules.NameTakenAsync(store, name, providerId, ct))
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Conflict, CustomProviderRules.Taken(name), "name");
        IReadOnlyList<CustomProviderUse> uses = [.. (await CustomProviderRules.UsesAsync(connections, ct))[providerId]];
        if (uses.Count > 0 && !string.Equals(check.BaseUrl, existing.BaseUrl, StringComparison.Ordinal))
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Conflict, CustomProviderRules.AddressInUse, "baseUrl");

        CustomProvider? updated;
        try
        {
            // Save the token that was checked, even when it is the saved one: a concurrent update could have replaced
            // it since, and keeping that one would store an address and token that never passed a check together.
            updated = await store.UpdateAsync(providerId, CustomProviderRules.Save(name, check, token?.Trim()), ct);
        }
        catch (InvalidOperationException)
        {
            // A connection was added, or the name taken, between the checks above and the save.
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Conflict,
                $"{CustomProviderRules.Taken(name)} Or a connection started using it, so its address can't change.");
        }
        if (updated is null)
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.NotFound, CustomProviderRules.Missing);

        await audit.LogAsync("custom_provider.updated", "custom_provider", updated.Id.ToString(),
            new { updated.Name, updated.BaseUrl, TokenReplaced = tokenReplaced }, ct);

        // The check that just passed used the token now saved, so connections it was refused for read again.
        foreach (CustomProviderUse refused in uses.Where(u => u.TokenRefused))
            await connections.RecordHealthAsync(refused.ConnectionId, new ConnectionHealth(
                DateTime.UtcNow, Succeeded: true, Step: null, "The provider's token was accepted.", Origin: "test"), ct);
        return Attempt<CustomProvider, CustomProviderStatus>.Succeed(updated);
    }
}

/// <summary>
/// Generates a new link secret for a custom provider, so its users can link their accounts, and returns it: the only
/// time it is shown. Any earlier secret stops working at once.
/// </summary>
[Operation("providers.custom.linkSecret", Scope = "admin:settings", Risk = RiskTier.Credential, Mcp = McpExposure.Generic)]
public sealed class ReplaceCustomProviderLinkSecret(ICustomProviderStore store, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<CustomProviderLinkSecret, CustomProviderStatus>> RunAsync(
        OperationContext context, Guid providerId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<CustomProviderLinkSecret, CustomProviderStatus>.Fail(CustomProviderStatus.Forbidden, CustomProviderRules.Refused);
        if (await store.GetAsync(providerId, ct) is not { } existing || await store.GenerateLinkSecretAsync(providerId, ct) is not { } secret)
            return Attempt<CustomProviderLinkSecret, CustomProviderStatus>.Fail(CustomProviderStatus.NotFound, CustomProviderRules.Missing);

        await audit.LogAsync("custom_provider.link_secret_generated", "custom_provider", providerId.ToString(), new { existing.Name }, ct);
        return Attempt<CustomProviderLinkSecret, CustomProviderStatus>.Succeed(new CustomProviderLinkSecret(secret));
    }
}

/// <summary>
/// Deletes a custom provider, its token and its link secret. Refused while connections use it: their sources would
/// lose their permission checks. Delete those connections first.
/// </summary>
[Operation("providers.custom.delete", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class DeleteCustomProvider(ICustomProviderStore store, IConnectionStore connections, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<CustomProvider, CustomProviderStatus>> RunAsync(OperationContext context, Guid providerId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.Forbidden, CustomProviderRules.Refused);
        if (await store.GetAsync(providerId, ct) is not { } existing)
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.NotFound, CustomProviderRules.Missing);

        IReadOnlyList<CustomProviderUse> uses = [.. (await CustomProviderRules.UsesAsync(connections, ct))[providerId]];
        if (uses.Count > 0)
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.InUse, CustomProviderRules.InUse(uses));

        bool deleted;
        try
        {
            deleted = await store.DeleteAsync(providerId, ct);
        }
        catch (InvalidOperationException)
        {
            // A connection started using it after the check above.
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.InUse,
                CustomProviderRules.InUse([.. (await CustomProviderRules.UsesAsync(connections, ct))[providerId]]));
        }
        if (!deleted)
            return Attempt<CustomProvider, CustomProviderStatus>.Fail(CustomProviderStatus.NotFound, CustomProviderRules.Missing);

        await audit.LogAsync("custom_provider.deleted", "custom_provider", providerId.ToString(), new { existing.Name }, ct);
        return Attempt<CustomProvider, CustomProviderStatus>.Succeed(existing);
    }
}

/// <summary>Every custom provider operation in one place, for the pages.</summary>
public sealed class CustomProviderOperations(
    ListCustomProviders list, CheckCustomProvider check, CreateCustomProvider create, UpdateCustomProvider update,
    ReplaceCustomProviderLinkSecret linkSecret, DeleteCustomProvider delete)
{
    public ListCustomProviders List { get; } = list;

    public CheckCustomProvider Check { get; } = check;

    public CreateCustomProvider Create { get; } = create;

    public UpdateCustomProvider Update { get; } = update;

    public ReplaceCustomProviderLinkSecret LinkSecret { get; } = linkSecret;

    public DeleteCustomProvider Delete { get; } = delete;
}

/// <summary>The rules the custom provider operations share.</summary>
internal static class CustomProviderRules
{
    public const string Refused = "You don't have permission to manage custom providers.";

    public const string Missing = "No custom provider has that id.";

    public const string AddressInUse = "A custom provider's address can't change while connections use it: their sources' documents came "
        + "from the current source server. Add a new provider for the new address instead.";

    private const int MaxNameLength = 128;

    public static string Taken(string name) => $"A custom provider named '{name}' already exists.";

    public static string InUse(IReadOnlyList<CustomProviderUse> uses) =>
        $"Connections still use this custom provider ({string.Join(", ", uses.Select(u => u.Name))}). Delete them first: "
        + "their sources would otherwise lose their permission checks.";

    public static string? InvalidName(string name) =>
        name.Length is 0 or > MaxNameLength ? $"A custom provider's name must be 1-{MaxNameLength} characters." : null;

    /// <summary>The connections on each custom provider, by provider id.</summary>
    public static async Task<ILookup<Guid, CustomProviderUse>> UsesAsync(IConnectionStore connections, CancellationToken ct) =>
        (await connections.ListAsync(take: int.MaxValue, ct: ct))
            .Where(c => c.Provider == ConnectionProvider.Custom)
            .Select(c => (ProviderId: CustomConnectionConfig.ProviderIdOf(c.ConfigJson), Use: new CustomProviderUse(c.Id, c.Name, c.Health is { CredentialRejected: true })))
            .Where(u => u.ProviderId is not null)
            .ToLookup(u => u.ProviderId!.Value, u => u.Use);

    public static async Task<bool> NameTakenAsync(ICustomProviderStore store, string name, Guid? except, CancellationToken ct) =>
        (await store.ListAsync(ct)).Any(p => p.Id != except && string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The saved token, when it may be sent to <paramref name="baseUrl"/>: only the saved address gets it, or anyone
    /// who can edit the provider could have it sent to a server of their choosing. Not usable when it may not, or can't be read.
    /// </summary>
    public static async Task<(bool Usable, string? Token)> SavedTokenAsync(ICustomProviderStore store, CustomProvider existing, string? baseUrl, CancellationToken ct)
    {
        if (!CustomSourceEndpoint.SameAddress(baseUrl, existing.BaseUrl))
            return (false, null);
        try
        {
            return (true, await store.GetTokenAsync(existing.Id, ct));
        }
        catch (InvalidOperationException)
        {
            // The stored token can't be decrypted, most often after the data protection keys changed.
            return (false, null);
        }
    }

    public static string SavedTokenRefusal(CustomProvider existing, string? baseUrl) =>
        CustomSourceEndpoint.SameAddress(baseUrl, existing.BaseUrl)
            ? "The saved token can't be read. Enter it again."
            : $"Enter the token for this address. The saved token is only sent to {existing.BaseUrl}.";

    /// <summary>What a failed check says, with the field the operations name: <c>baseUrl</c> or <c>token</c>.</summary>
    public static (string Detail, string Field)? Failed(CustomProviderCheck check) =>
        check is { EndpointOk: true, PermissionsOk: true, BaseUrl: not null }
            ? null
            : (check.Error ?? "The source server didn't pass the check.", check.Field == "secret" ? "token" : "baseUrl");

    public static string NameFor(string? given, CustomProviderCheck check) =>
        given?.Trim() is { Length: > 0 } name ? name
        : check.Name?.Trim() is { Length: > 0 } reported ? reported
        : new Uri(check.BaseUrl!).Host;

    public static SaveCustomProviderRequest Save(string name, CustomProviderCheck check, string? token) =>
        new(name, check.BaseUrl!, token, check.MaxCheckBatch, check.SupportsAccessKeys, check.LinkUrl);
}
