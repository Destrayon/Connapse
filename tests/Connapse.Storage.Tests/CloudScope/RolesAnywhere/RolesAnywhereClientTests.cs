using System.Net;
using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime;
using Connapse.Storage.CloudScope.RolesAnywhere;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

[Trait("Category", "Unit")]
public class RolesAnywhereClientTests
{
    private static readonly RolesAnywhereParameters Params = new(
        "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
        "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
        "arn:aws:iam::111:role/connapse",
        "us-east-1");

    [Fact]
    public async Task CreateSessionAsync_On201_ReturnsTemporaryCredentials()
    {
        const string json = """
        {
          "credentialSet": [
            {
              "credentials": {
                "accessKeyId": "ASIA_TEMP",
                "secretAccessKey": "temp-secret",
                "sessionToken": "temp-token",
                "expiration": "2026-09-01T13:00:00Z"
              }
            }
          ]
        }
        """;
        var handler = new StubHandler(HttpStatusCode.Created, json);
        var client = new RolesAnywhereClient(new HttpClient(handler));
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();

        RolesAnywhereSession session = await client.CreateSessionAsync(
            cert, Params, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));

        ImmutableCredentials creds = session.Credentials;
        creds.AccessKey.Should().Be("ASIA_TEMP");
        creds.SecretKey.Should().Be("temp-secret");
        creds.Token.Should().Be("temp-token");
        session.Expiration.Should().Be(DateTimeOffset.Parse("2026-09-01T13:00:00Z"));
    }

    [Fact]
    public async Task CreateSessionAsync_SendsSignedHeadersAndNoCharsetContentType()
    {
        var handler = new StubHandler(HttpStatusCode.Created, MinimalBody);
        var client = new RolesAnywhereClient(new HttpClient(handler));
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();

        await client.CreateSessionAsync(cert, Params, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://rolesanywhere.us-east-1.amazonaws.com/sessions");
        handler.LastRequest.Headers.Contains("X-Amz-X509").Should().BeTrue();
        handler.LastRequest.Headers.Authorization.Should().BeNull(); // sent raw, not parsed as scheme
        handler.LastRequest.Headers.TryGetValues("Authorization", out _).Should().BeTrue();
        handler.LastRequest.Content!.Headers.ContentType!.ToString().Should().Be("application/json");
    }

    [Fact]
    public async Task CreateSessionAsync_OnNon201_ThrowsWithStatusAndBody()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "{\"message\":\"denied\"}");
        var client = new RolesAnywhereClient(new HttpClient(handler));
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();

        Func<Task> act = () => client.CreateSessionAsync(cert, Params, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));

        (await act.Should().ThrowAsync<RolesAnywhereException>())
            .Which.StatusCode.Should().Be(403);
    }

    private const string MinimalBody = """
    {"credentialSet":[{"credentials":{"accessKeyId":"a","secretAccessKey":"s","sessionToken":"t","expiration":"2026-09-01T13:00:00Z"}}]}
    """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
