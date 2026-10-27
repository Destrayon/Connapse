using Connapse.Application;
using Connapse.Application.Providers;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Provider setup over REST, on the same operations as the providers page. Settings administrators only:
/// a token of theirs needs the admin:settings scope.
/// </summary>
public static class ProvidersEndpoints
{
    public static IEndpointRouteBuilder MapProvidersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/providers")
            .WithTags("Providers")
            .RequireAuthorization("RequireSettingsAdmin")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/v1/providers — each built-in provider and what it still needs
        group.MapGet("/", async (
            [FromServices] GetProviderStatus status,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await status.RunAsync(Rest(http), ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<IReadOnlyList<ProviderSetup>>()
        .WithName("providers.status")
        .WithDescription("List each built-in provider (aws, azure, github, atlassian) with its requirements and their status. "
            + "Checks AWS, Entra and GitHub live, so it can take a few seconds. A provider's overall status is its weakest requirement's.");

        MapAwsProvider(group.MapGroup("/aws"));
        MapAzureProvider(group.MapGroup("/azure"));
        MapCustomProviders(group.MapGroup("/custom"));
        return app;
    }

    private static void MapAzureProvider(RouteGroupBuilder azure)
    {
        // PUT /api/v1/providers/azure/access — check an access identity with Entra, then save it
        azure.MapPut("/access", async ([FromServices] SetAzureAccess access, [FromBody] AzureAccessRequest request, HttpContext http, CancellationToken ct) =>
        {
            var saved = await access.RunAsync(Rest(http), request, ct);
            return saved.Succeeded ? Results.Ok(saved.Result) : OperationProblems.Result(saved);
        })
        .Produces<AzureSettingsSaved<AzureProviderSettings>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithName("providers.azure.access")
        .WithDescription("Set Connapse's Azure access identity: a certificate app (clientId and clientCertificatePath, a file on this host), "
            + "a user-assigned managed identity, or the host's own (useHostManagedIdentity), with its tenantId and the subscriptionId per-user "
            + "filtering reads. Entra is asked for a token first, and nothing is saved unless it gives one. A null clientCertificatePassword "
            + "keeps the stored one for the same certificate; it is never returned. Read the values at GET /api/settings/azure.");

        // DELETE /api/v1/providers/azure/access — clear the access identity
        azure.MapDelete("/access", async ([FromServices] ResetAzureAccess reset, HttpContext http, CancellationToken ct) =>
        {
            var cleared = await reset.RunAsync(Rest(http), ct);
            return cleared.Succeeded ? Results.Ok(cleared.Result) : OperationProblems.Result(cleared);
        })
        .Produces<AzureSettingsSaved<AzureProviderSettings>>()
        .WithName("providers.azure.access.reset")
        .WithDescription("Clear Connapse's Azure access identity. The app registration and its role assignments stay in Azure until removed there.");

        // PUT /api/v1/providers/azure/permissions — save the sign-in application; switches enforcement on for good
        azure.MapPut("/permissions", async ([FromServices] SetAzurePermissions permissions, [FromBody] AzurePermissionsRequest request,
            HttpContext http, CancellationToken ct) =>
        {
            var saved = await permissions.RunAsync(Rest(http), request, ct);
            return saved.Succeeded ? Results.Ok(saved.Result) : OperationProblems.Result(saved);
        })
        .Produces<AzureSettingsSaved<AzureAdSignInSettings>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("providers.azure.permissions")
        .WithDescription("Save the Entra application people sign in through: tenantId, clientId, redirectUri and clientCertificatePath. Once all "
            + "four are set, Entra is asked for a token with it, and per-user Azure permission enforcement is switched on before anything is "
            + "saved; it can't be switched off again. Admin consent needs a browser: while adminConsentPending is true, adminConsentUrl is the "
            + "link to open, and GET /api/v1/providers shows the sign-in requirement as waiting on it. Once consent is granted, save again with "
            + "adminConsentPending false. Read the values at GET /api/settings/azuread.");

        // DELETE /api/v1/providers/azure/permissions — clear the sign-in application; enforcement stays on
        azure.MapDelete("/permissions", async ([FromServices] ResetAzurePermissions reset, HttpContext http, CancellationToken ct) =>
        {
            var cleared = await reset.RunAsync(Rest(http), ct);
            return cleared.Succeeded ? Results.Ok(cleared.Result) : OperationProblems.Result(cleared);
        })
        .Produces<AzureSettingsSaved<AzureAdSignInSettings>>()
        .WithName("providers.azure.permissions.reset")
        .WithDescription("Clear the recorded sign-in application, for one removed in Entra. Per-user enforcement stays on, so searches of Azure "
            + "sources return nothing for everyone until the application is saved again.");
    }

    private static void MapAwsProvider(RouteGroupBuilder aws)
    {
        // PUT /api/v1/providers/aws/access — check an IAM Roles Anywhere credential with AWS, then save it
        aws.MapPut("/access", async ([FromServices] SetAwsAccess access, [FromBody] AwsAccessRequest request, HttpContext http, CancellationToken ct) =>
        {
            var saved = await access.RunAsync(Rest(http), request, ct);
            return saved.Succeeded ? Results.Ok(saved.Result) : OperationProblems.Result(saved);
        })
        .Produces<RolesAnywhereConfig>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("providers.aws.access")
        .WithDescription("Set Connapse's AWS access to an IAM Roles Anywhere credential: certificate, trust anchor, profile and role ARNs, region "
            + "and private key. AWS is asked for a session first, and nothing is saved unless it gives one. A blank privateKeyPem keeps the "
            + "stored key; pass expected (the credential last read) to refuse the save if someone changed it since. A credential someone else "
            + "saves while AWS is checking this one is never overwritten: the save is refused with 409. The key is never returned.");

        // DELETE /api/v1/providers/aws/access — remove the stored credential
        aws.MapDelete("/access", async ([FromServices] ResetAwsAccess reset, HttpContext http, CancellationToken ct) =>
        {
            var removed = await reset.RunAsync(Rest(http), ct);
            return removed.Succeeded ? Results.Ok(removed.Result) : OperationProblems.Result(removed);
        })
        .Produces<AwsAccessReset>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("providers.aws.access.reset")
        .WithDescription("Remove Connapse's stored AWS credential. It stays valid in AWS until cleanupScript (an AWS CloudShell command) deletes "
            + "its resources, or it expires. Without one, Connapse reads S3 with whatever credentials its environment provides. If another "
            + "credential is saved while this runs, nothing is removed and the response is 409.");

        // PUT /api/v1/providers/aws/identity-center — record the IAM Identity Center instance
        aws.MapPut("/identity-center", async ([FromServices] SetIdentityCenter identityCenter, [FromBody] IdentityCenterRequest request,
            HttpContext http, CancellationToken ct) =>
        {
            var saved = await identityCenter.RunAsync(Rest(http), request, ct);
            return saved.Succeeded ? Results.Ok(saved.Result) : OperationProblems.Result(saved);
        })
        .Produces<AwsSettingsSaved<IdentityCenterSettings>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithName("providers.aws.identityCenter")
        .WithDescription("Record the IAM Identity Center instance per-user AWS permissions are resolved against: its region, instance ARN and "
            + "identity store id. applied is false when the settings take effect only after a restart. Read them at GET /api/settings/identitycenter.");

        // DELETE /api/v1/providers/aws/identity-center — clear the recorded instance
        aws.MapDelete("/identity-center", async ([FromServices] ResetIdentityCenter reset, HttpContext http, CancellationToken ct) =>
        {
            var cleared = await reset.RunAsync(Rest(http), ct);
            return cleared.Succeeded ? Results.Ok(cleared.Result) : OperationProblems.Result(cleared);
        })
        .Produces<AwsSettingsSaved<IdentityCenterSettings>>()
        .WithName("providers.aws.identityCenter.reset")
        .WithDescription("Clear the recorded IAM Identity Center instance. Blanks are stored, so values from configuration don't come back.");

        // PUT /api/v1/providers/aws/permissions — save the SAML application; switches enforcement on for good
        aws.MapPut("/permissions", async ([FromServices] SetAwsPermissions permissions, [FromBody] AwsPermissionsRequest request,
            HttpContext http, CancellationToken ct) =>
        {
            var saved = await permissions.RunAsync(Rest(http), request, ct);
            return saved.Succeeded ? Results.Ok(saved.Result) : OperationProblems.Result(saved);
        })
        .Produces<AwsSettingsSaved<SamlSignInSettings>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .WithName("providers.aws.permissions")
        .WithDescription("Save the IAM Identity Center SAML application people sign in through: Connapse's entityId and acsUrl, and Identity "
            + "Center's idpEntityId, idpSingleSignOnUrl and idpSigningCertificate. Once all five are set, per-user AWS permission enforcement "
            + "is switched on first, and it can't be switched off again. Read the values at GET /api/settings/samlsignin.");

        // DELETE /api/v1/providers/aws/permissions — clear the SAML application; enforcement stays on
        aws.MapDelete("/permissions", async ([FromServices] ResetAwsPermissions reset, HttpContext http, CancellationToken ct) =>
        {
            var cleared = await reset.RunAsync(Rest(http), ct);
            return cleared.Succeeded ? Results.Ok(cleared.Result) : OperationProblems.Result(cleared);
        })
        .Produces<AwsSettingsSaved<SamlSignInSettings>>()
        .WithName("providers.aws.permissions.reset")
        .WithDescription("Clear the recorded SAML application, for one removed in AWS. Per-user enforcement stays on, so searches of AWS "
            + "sources return nothing for everyone until the application is saved again.");
    }

    private static void MapCustomProviders(RouteGroupBuilder custom)
    {
        // GET /api/v1/providers/custom — each custom provider and the connections that use it
        custom.MapGet("/", async ([FromServices] ListCustomProviders list, HttpContext http, CancellationToken ct) =>
        {
            var read = await list.RunAsync(Rest(http), ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<IReadOnlyList<CustomProviderListing>>()
        .WithName("providers.custom.list")
        .WithDescription("List the custom providers, each with the connections that check permissions with it. Tokens and link secrets are never returned.");

        // POST /api/v1/providers/custom — check a source server and save it as a custom provider
        custom.MapPost("/", async ([FromServices] CreateCustomProvider create, [FromBody] CreateCustomProviderRequest request, HttpContext http, CancellationToken ct) =>
        {
            var created = await create.RunAsync(Rest(http), request, ct);
            return created.Succeeded ? Results.Created((string?)null, created.Result) : OperationProblems.Result(created);
        })
        .Produces<CustomProvider>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("providers.custom.create")
        .WithDescription("Add a custom provider. Its source server is checked first (address, token, that a wrong token is refused, "
            + "and a real permission check), and nothing is saved unless every step passes. A blank name is the one the server reports.");

        // PATCH /api/v1/providers/custom/{providerId} — rename it, move it or replace its token
        custom.MapPatch("/{providerId:guid}", async ([FromServices] UpdateCustomProvider update, Guid providerId,
            [FromBody] UpdateCustomProviderRequest request, HttpContext http, CancellationToken ct) =>
        {
            var updated = await update.RunAsync(Rest(http), providerId, request, ct);
            return updated.Succeeded ? Results.Ok(updated.Result) : OperationProblems.Result(updated);
        })
        .Produces<CustomProvider>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("providers.custom.update")
        .WithDescription("Change a custom provider's name, address or token; what is left out stays. The source server is checked again first. "
            + "A new address needs its token: the saved one is only sent to the saved address. The address can't change while connections use it.");

        // POST /api/v1/providers/custom/{providerId}/check — check its source server, saving nothing
        custom.MapPost("/{providerId:guid}/check", async ([FromServices] CheckCustomProvider check, Guid providerId,
            [FromBody] CustomProviderCheckRequest? request, HttpContext http, CancellationToken ct) =>
        {
            var checkedServer = await check.RunAsync(Rest(http), providerId, request ?? new(), ct);
            return checkedServer.Succeeded ? Results.Ok(checkedServer.Result) : OperationProblems.Result(checkedServer);
        })
        .Produces<CustomProviderCheck>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("providers.custom.check")
        .WithDescription("Check a custom provider's source server, with its saved address and token or the ones given, without saving anything. "
            + "A failed check is still a 200: endpointOk, permissionsOk, error and field say what passed and what to change.");

        // PUT /api/v1/providers/custom/{providerId}/link-secret — a new link secret, shown once
        custom.MapPut("/{providerId:guid}/link-secret", async ([FromServices] ReplaceCustomProviderLinkSecret linkSecret, Guid providerId,
            HttpContext http, CancellationToken ct) =>
        {
            var replaced = await linkSecret.RunAsync(Rest(http), providerId, ct);
            return replaced.Succeeded ? Results.Ok(replaced.Result) : OperationProblems.Result(replaced);
        })
        .Produces<CustomProviderLinkSecret>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("providers.custom.linkSecret")
        .WithDescription("Generate a new link secret for a custom provider, so its users can link their accounts. This response is the only time "
            + "it is shown, and any earlier secret stops working at once.");

        // DELETE /api/v1/providers/custom/{providerId}
        custom.MapDelete("/{providerId:guid}", async ([FromServices] DeleteCustomProvider delete, Guid providerId, HttpContext http, CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), providerId, ct);
            return deleted.Succeeded ? Results.NoContent() : OperationProblems.Result(deleted);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("providers.custom.delete")
        .WithDescription("Delete a custom provider with its token and link secret. Refused while connections use it, since their sources would lose their permission checks.");
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}
