using Connapse.Core;

namespace Connapse.Application.Ports;

/// <summary>
/// Asks a custom provider's source server, before it is saved, whether it can be one: the address rules, the
/// token, that a wrong token is refused, and that it answers a real permission check.
/// </summary>
public interface ICustomProviderChecker
{
    Task<CustomProviderCheck> CheckAsync(string? baseUrl, string? token, CancellationToken ct);
}
