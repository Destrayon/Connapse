using System.Security.Claims;

namespace Connapse.Application;

/// <summary>The surface an operation was called from.</summary>
public enum OperationSurface
{
    Ui,
    Rest,
    Mcp,
}

/// <summary>
/// Who is calling an operation, and from where. The operation owns authorization, preflight and
/// audit, so it reads the caller from here, never from an HTTP context or a Blazor circuit.
/// </summary>
public sealed record OperationContext(ClaimsPrincipal User, OperationSurface Surface);
