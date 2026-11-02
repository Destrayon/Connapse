using System.Security.Claims;
using Connapse.Application.Ports;
using Microsoft.AspNetCore.Authorization;

namespace Connapse.Web.Services;

/// <summary>Answers an operation's policy check with the same authorization service the endpoints use.</summary>
public sealed class OperationAuthorizer(IAuthorizationService authorization) : IOperationAuthorizer
{
    public async Task<bool> AllowsAsync(ClaimsPrincipal user, string policy) =>
        (await authorization.AuthorizeAsync(user, policy)).Succeeded;
}
