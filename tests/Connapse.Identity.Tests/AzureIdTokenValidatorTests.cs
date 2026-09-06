using System.Security.Cryptography;
using Connapse.Core;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class AzureIdTokenValidatorTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";

    [Fact]
    public async Task ValidToken_ExtractsOidTid()
    {
        (string token, RsaSecurityKey key) = CreateToken(nonce: "expected-nonce", oid: "oid-1", tid: TenantId, name: "Ada Lovelace");
        AzureIdTokenValidator validator = BuildValidator(key);

        AzureIdTokenResult result = await validator.ValidateAsync(token, "expected-nonce", CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.ObjectId.Should().Be("oid-1");
        result.TenantId.Should().Be(TenantId);
        result.DisplayName.Should().Be("Ada Lovelace");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task WrongNonce_Fails()
    {
        (string token, RsaSecurityKey key) = CreateToken(nonce: "actual-nonce", oid: "oid-1", tid: TenantId, name: "Ada Lovelace");
        AzureIdTokenValidator validator = BuildValidator(key);

        AzureIdTokenResult result = await validator.ValidateAsync(token, "different-nonce", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ObjectId.Should().BeNull();
        result.TenantId.Should().BeNull();
        result.DisplayName.Should().BeNull();
    }

    [Fact]
    public async Task WrongAudience_Fails()
    {
        (string token, RsaSecurityKey key) = CreateToken(
            nonce: "expected-nonce", oid: "oid-1", tid: TenantId, name: "Ada Lovelace", audience: "some-other-client-id");
        AzureIdTokenValidator validator = BuildValidator(key);

        AzureIdTokenResult result = await validator.ValidateAsync(token, "expected-nonce", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ObjectId.Should().BeNull();
    }

    [Fact]
    public async Task Expired_Fails()
    {
        DateTime now = DateTime.UtcNow;
        (string token, RsaSecurityKey key) = CreateToken(
            nonce: "expected-nonce", oid: "oid-1", tid: TenantId, name: "Ada Lovelace",
            notBefore: now.AddMinutes(-20), expires: now.AddMinutes(-10));
        AzureIdTokenValidator validator = BuildValidator(key);

        AzureIdTokenResult result = await validator.ValidateAsync(token, "expected-nonce", CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ObjectId.Should().BeNull();
    }

    private static AzureIdTokenValidator BuildValidator(RsaSecurityKey key)
    {
        var settings = new AzureAdSignInSettings { TenantId = TenantId, ClientId = ClientId };
        IOptionsMonitor<AzureAdSignInSettings> options = Substitute.For<IOptionsMonitor<AzureAdSignInSettings>>();
        options.CurrentValue.Returns(settings);

        var keySource = new StubKeySource(new List<SecurityKey> { key });
        return new AzureIdTokenValidator(options, keySource);
    }

    private static (string Token, RsaSecurityKey Key) CreateToken(
        string nonce,
        string oid,
        string tid,
        string name,
        string? audience = null,
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var signingCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        DateTime now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience ?? ClientId,
            NotBefore = notBefore ?? now.AddMinutes(-5),
            Expires = expires ?? now.AddMinutes(5),
            SigningCredentials = signingCredentials,
            Claims = new Dictionary<string, object>
            {
                ["nonce"] = nonce,
                ["oid"] = oid,
                ["tid"] = tid,
                ["name"] = name,
            },
        };

        var handler = new JsonWebTokenHandler();
        string token = handler.CreateToken(descriptor);
        return (token, key);
    }

    private sealed class StubKeySource(IReadOnlyList<SecurityKey> keys) : IAzureSigningKeySource
    {
        public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult(keys);
    }
}
