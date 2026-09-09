using System.Collections.Concurrent;
using Connapse.Identity.Services;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Integration.Tests;

/// <summary>
/// Test doubles for the Entra OIDC user-identity-link flow (<see cref="CloudIdentityEndpointTests"/>),
/// registered into the shared integration-test host in place of the real
/// <see cref="AzureOidcTokenExchanger"/> and Entra JWKS lookup. Neither makes a network call, so
/// the callback endpoint can be exercised end to end without a real Entra tenant.
/// </summary>
/// <remarks>
/// Both are singletons in a host shared across the whole integration-test collection, so state is
/// keyed by the authorization <c>code</c> a test makes up (a fresh GUID per test) rather than held
/// as a single mutable field — concurrent tests must not be able to see or clobber each other's
/// fake token.
/// </remarks>
public sealed class FakeOidcTokenExchanger : IOidcTokenExchanger
{
    private readonly ConcurrentDictionary<string, string> _tokensByCode = new();

    /// <summary>Makes <paramref name="code"/> exchange for <paramref name="idToken"/>.</summary>
    public void SetToken(string code, string idToken) => _tokensByCode[code] = idToken;

    public Task<string> ExchangeAsync(string code, string codeVerifier, CancellationToken ct)
    {
        if (_tokensByCode.TryRemove(code, out string? token))
            return Task.FromResult(token);

        throw new InvalidOperationException(
            $"FakeOidcTokenExchanger has no id_token registered for code '{code}'. " +
            "The test must call SetToken before driving the callback.");
    }
}

/// <summary>
/// Hands back a fixed signing key set so tokens signed with <see cref="SigningKey"/> in a test
/// validate as if they came from Entra's real JWKS.
/// </summary>
public sealed class FakeAzureSigningKeySource : IAzureSigningKeySource
{
    /// <summary>The one key this test double ever reports — generated once per test host.</summary>
    public static readonly RsaSecurityKey SigningKey = CreateKey();

    private static RsaSecurityKey CreateKey() =>
        new(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "integration-test-key" };

    public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(string tenantId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SecurityKey>>(new[] { (SecurityKey)SigningKey });
}
