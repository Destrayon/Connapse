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
        var store = Substitute.For<IProviderCredentialStore>();
        store.GetRolesAnywhereAsync("aws").Returns(new RolesAnywhereConfig(
            certPem,
            "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
            "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
            "arn:aws:iam::111:role/connapse",
            "us-east-1"));
        store.GetRolesAnywherePrivateKeyAsync("aws").Returns(keyPem);

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
    public void GetCredentials_WithStoredAccessKeyAndNoRolesAnywhere_ReturnsTheAccessKey()
    {
        var store = Substitute.For<IProviderCredentialStore>();
        store.GetRolesAnywhereAsync("aws").Returns((RolesAnywhereConfig?)null);
        store.GetAsync("aws").Returns(new ProviderCredentialInfo("aws", "AKIAEXAMPLE", "connapse-reader", DateTime.UtcNow));
        store.GetSecretAsync("aws").Returns("static-secret");
        var factory = HttpClientFactoryReturning(HttpStatusCode.Created, "{}"); // never called

        var credentials = BuildCredentials(store, factory);

        ImmutableCredentials resolved = credentials.GetCredentials();
        resolved.AccessKey.Should().Be("AKIAEXAMPLE");
        resolved.SecretKey.Should().Be("static-secret");
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
