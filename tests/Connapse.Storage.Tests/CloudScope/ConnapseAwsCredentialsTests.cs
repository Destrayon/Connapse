// tests/Connapse.Storage.Tests/CloudScope/ConnapseAwsCredentialsTests.cs
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class ConnapseAwsCredentialsTests
{
    [Fact]
    public void GetCredentials_WithStoredRolesAnywhereConfig_ReturnsTemporaryCredentialsFromCreateSession()
    {
        (string certPem, string keyPem) = NewCertAndKeyPem();
        var config = new RolesAnywhereConfig(
            certPem,
            "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
            "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
            "arn:aws:iam::111:role/connapse",
            "us-east-1");
        var store = Substitute.For<IProviderCredentialStore>();
        store.GetRolesAnywhereAsync("aws").Returns(config);
        store.GetRolesAnywhereMaterialAsync("aws").Returns(new RolesAnywhereCredentialMaterial(config, keyPem));

        const string sessionJson = """
        {"credentialSet":[{"credentials":{"accessKeyId":"ASIA_RA","secretAccessKey":"ra-secret","sessionToken":"ra-token","expiration":"2999-01-01T00:00:00Z"}}]}
        """;
        var factory = HttpClientFactoryReturning(HttpStatusCode.Created, sessionJson);

        var credentials = BuildCredentials(store, factory);

        ImmutableCredentials resolved = credentials.GetCredentials();
        resolved.AccessKey.Should().Be("ASIA_RA");
        resolved.Token.Should().Be("ra-token");
    }

    [Fact]
    public void GetCredentials_WithRolesAnywhereConfigAndFailingCreateSession_ThrowsAndDoesNotFallBackToAmbient()
    {
        (string certPem, string keyPem) = NewCertAndKeyPem();
        var config = new RolesAnywhereConfig(
            certPem,
            "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
            "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
            "arn:aws:iam::111:role/connapse",
            "us-east-1");
        var store = Substitute.For<IProviderCredentialStore>();
        store.GetRolesAnywhereAsync("aws").Returns(config);
        store.GetRolesAnywhereMaterialAsync("aws").Returns(new RolesAnywhereCredentialMaterial(config, keyPem));

        // CreateSession fails (e.g. IAM Roles Anywhere returns a server error).
        var factory = HttpClientFactoryReturning(HttpStatusCode.InternalServerError, "server error");

        var credentials = BuildCredentials(store, factory);

        Action act = () => credentials.GetCredentials();

        // Fail closed: a configured Roles Anywhere identity must never silently downgrade to the
        // ambient (instance-role) chain when it cannot be produced.
        act.Should().Throw<RolesAnywhereCredentialException>()
            .Which.Provider.Should().Be("aws");
    }

    [Fact]
    public void GetCredentials_WithRolesAnywhereConfigButMissingPrivateKey_ThrowsAndDoesNotFallBackToAmbient()
    {
        var config = new RolesAnywhereConfig(
            "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----",
            "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
            "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
            "arn:aws:iam::111:role/connapse",
            "us-east-1");
        var store = Substitute.For<IProviderCredentialStore>();
        // The config exists but its key material is blank — a defensive belt-and-suspenders case:
        // the real store's own contract never returns this (it throws instead, covered by
        // GetCredentials_WhenMaterialReadThrowsProviderCredentialUnavailable... below), but nothing
        // in IProviderCredentialStore stops a caller from returning it.
        store.GetRolesAnywhereMaterialAsync("aws").Returns(new RolesAnywhereCredentialMaterial(config, string.Empty));

        var factory = HttpClientFactoryReturning(HttpStatusCode.Created, "{}"); // never called

        var credentials = BuildCredentials(store, factory);

        Action act = () => credentials.GetCredentials();

        act.Should().Throw<RolesAnywhereCredentialException>()
            .Which.Provider.Should().Be("aws");
    }

    [Fact]
    public void GetCredentials_WhenMaterialReadThrowsProviderCredentialUnavailable_ThrowsRolesAnywhereCredentialExceptionAndDoesNotFallBackToAmbient()
    {
        var store = Substitute.For<IProviderCredentialStore>();
        // The real store throws this when a configured Roles Anywhere row's key is empty or
        // undecryptable (lost DataProtection key ring). ReadStoredAsync's try/catch must convert it
        // into the fail-closed RA exception rather than let it surface raw or fall back to ambient.
        Func<NSubstitute.Core.CallInfo, RolesAnywhereCredentialMaterial?> throwUnavailable =
            _ => throw new ProviderCredentialUnavailableException("aws", new Exception("boom"));
        store.GetRolesAnywhereMaterialAsync("aws").Returns(throwUnavailable);

        var factory = HttpClientFactoryReturning(HttpStatusCode.Created, "{}"); // never called

        var credentials = BuildCredentials(store, factory);

        Action act = () => credentials.GetCredentials();

        act.Should().Throw<RolesAnywhereCredentialException>()
            .Which.Provider.Should().Be("aws");
    }

    [Fact]
    public void ClampRefreshExpiry_SessionExpiresBeforeWindow_ReturnsSessionExpiry()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime session = now.AddMinutes(1);
        ConnapseAwsCredentials.ClampRefreshExpiry(now, TimeSpan.FromMinutes(5), session).Should().Be(session);
    }

    [Fact]
    public void ClampRefreshExpiry_SessionExpiresAfterWindow_ReturnsWindowExpiry()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime session = now.AddHours(1);
        ConnapseAwsCredentials.ClampRefreshExpiry(now, TimeSpan.FromMinutes(5), session).Should().Be(now.AddMinutes(5));
    }

    [Fact]
    public void ClampRefreshExpiry_NoSessionExpiry_ReturnsWindowExpiry()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        ConnapseAwsCredentials.ClampRefreshExpiry(now, TimeSpan.FromMinutes(5), null).Should().Be(now.AddMinutes(5));
    }

    private static ConnapseAwsCredentials BuildCredentials(IProviderCredentialStore store, IHttpClientFactory factory)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        services.AddSingleton(factory);
        ServiceProvider provider = services.BuildServiceProvider();
        return new ConnapseAwsCredentials(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConnapseAwsCredentials>.Instance);
    }

    private static IHttpClientFactory HttpClientFactoryReturning(HttpStatusCode status, string body)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHandler(status, body)));
        return factory;
    }

    private static (string CertPem, string KeyPem) NewCertAndKeyPem()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=connapse-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return (cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
