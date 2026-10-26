using Connapse.Application.Ports;
using Connapse.Application.Users;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Authorization;
using Connapse.Identity.Services;

namespace Connapse.Application.Tokens;

/// <summary>Why a personal access token operation didn't succeed.</summary>
public enum PatStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "pat-not-found", "Personal access token not found")]
    NotFound,

    [Failure(400, "invalid-pat", "Invalid personal access token")]
    Invalid,
}

internal static class PatRules
{
    public const string NoPermission =
        "You don't have permission to manage personal access tokens with this sign-in: sign in yourself, not with a token or agent key.";

    public const int MaxNameLength = 256;

    /// <summary>
    /// The person managing their own tokens, signed in themselves: a browser session or the bearer token they
    /// signed in for. Never a token of any kind: a personal access token or an OAuth client's access token could
    /// otherwise mint its owner one with more scopes than it was granted, and an agent key acts as no person.
    /// RequirePerson refuses the first and last but admits an OAuth client's token, so that is refused here.
    /// Null when the caller may not.
    /// </summary>
    public static async Task<Guid?> OwnerAsync(IOperationAuthorizer authorizer, OperationContext context) =>
        await authorizer.AllowsAsync(context.User, Policies.Person) && !TokenScopes.IsToken(context.User)
            ? UserRules.CallerId(context.User)
            : null;
}

/// <summary>Lists the caller's own personal access tokens, newest first, revoked ones included. A token's value is never listed.</summary>
// No token holds pats:manage: only a person signed in themselves manages their tokens, and MCP never does.
[Operation("pats.list", Scope = "pats:manage", Risk = RiskTier.Read)]
public sealed class ListPersonalTokens(IPatService pats, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<IReadOnlyList<PatListItem>, PatStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (await PatRules.OwnerAsync(authorizer, context) is not Guid owner)
            return Attempt<IReadOnlyList<PatListItem>, PatStatus>.Fail(PatStatus.Forbidden, PatRules.NoPermission);
        return Attempt<IReadOnlyList<PatListItem>, PatStatus>.Succeed(await pats.ListAsync(owner, ct));
    }
}

/// <summary>
/// Makes the caller a personal access token. The name is required and at most 256 characters, stored
/// trimmed; the scopes are ones a personal token can hold (<see cref="TokenScopeChoices.PersonalToken"/>),
/// and a token with none can call no scoped route; an expiry, read as UTC when it names no time zone, must
/// be in the future. The token is answered once, in the result, and never stored or audited.
/// </summary>
[Operation("pats.create", Scope = "pats:manage", Risk = RiskTier.Credential)]
public sealed class CreatePersonalToken(IPatService pats, IOperationAuthorizer authorizer, IAuditLogger audit, TimeProvider time)
{
    public const string CreatedEvent = "pat.created";

    public async Task<Attempt<PatCreateResponse, PatStatus>> RunAsync(OperationContext context, PatCreateRequest request, CancellationToken ct = default)
    {
        if (await PatRules.OwnerAsync(authorizer, context) is not Guid owner)
            return Attempt<PatCreateResponse, PatStatus>.Fail(PatStatus.Forbidden, PatRules.NoPermission);

        string name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Attempt<PatCreateResponse, PatStatus>.Fail(PatStatus.Invalid, "Token name is required.", "name");
        if (name.Length > PatRules.MaxNameLength)
            return Attempt<PatCreateResponse, PatStatus>.Fail(PatStatus.Invalid, $"Token name must be at most {PatRules.MaxNameLength} characters.", "name");

        string?[] scopes = (request.Scopes ?? []).Distinct(StringComparer.Ordinal).ToArray();
        // By index, not FirstOrDefault: a null scope would read as "none found" and let the ones after it through.
        int unknown = Array.FindIndex(scopes, s => s is null || !TokenScopeChoices.PersonalToken.Contains(s, StringComparer.Ordinal));
        if (unknown >= 0)
            return Attempt<PatCreateResponse, PatStatus>.Fail(PatStatus.Invalid,
                $"'{scopes[unknown]}' is an invalid personal access token scope. Scopes: {string.Join(", ", TokenScopeChoices.PersonalToken)}.", "scopes");

        DateTime? expiresAt = request.ExpiresAt switch
        {
            null => null,
            { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
            { } other => DateTime.SpecifyKind(other, DateTimeKind.Utc),
        };
        if (expiresAt <= time.GetUtcNow().UtcDateTime)
            return Attempt<PatCreateResponse, PatStatus>.Fail(PatStatus.Invalid, "Token expiry must be in the future.", "expiresAt");

        var token = await pats.CreateAsync(owner, request with { Name = name, Scopes = scopes!, ExpiresAt = expiresAt }, ct);

        // The token's name and scopes, never its value: an audit row is read by every administrator.
        await audit.LogAsync(CreatedEvent, "pat", token.Id.ToString(), new { token.Name, token.Scopes }, ct);
        return Attempt<PatCreateResponse, PatStatus>.Succeed(token);
    }
}

/// <summary>Revokes one of the caller's own personal access tokens, so it stops working. Another person's reads as not found.</summary>
[Operation("pats.revoke", Scope = "pats:manage", Risk = RiskTier.Destructive)]
public sealed class RevokePersonalToken(IPatService pats, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string RevokedEvent = "pat.revoked";

    public async Task<Attempt<Guid, PatStatus>> RunAsync(OperationContext context, Guid tokenId, CancellationToken ct = default)
    {
        if (await PatRules.OwnerAsync(authorizer, context) is not Guid owner)
            return Attempt<Guid, PatStatus>.Fail(PatStatus.Forbidden, PatRules.NoPermission);
        if (!await pats.RevokeAsync(owner, tokenId, ct))
            return Attempt<Guid, PatStatus>.Fail(PatStatus.NotFound, "Token not found or already revoked.");

        await audit.LogAsync(RevokedEvent, "pat", tokenId.ToString(), null, ct);
        return Attempt<Guid, PatStatus>.Succeed(tokenId);
    }
}

/// <summary>Every personal access token operation in one place, for the API Tokens page.</summary>
public sealed class PersonalTokenOperations(ListPersonalTokens list, CreatePersonalToken create, RevokePersonalToken revoke)
{
    public ListPersonalTokens List { get; } = list;

    public CreatePersonalToken Create { get; } = create;

    public RevokePersonalToken Revoke { get; } = revoke;
}
