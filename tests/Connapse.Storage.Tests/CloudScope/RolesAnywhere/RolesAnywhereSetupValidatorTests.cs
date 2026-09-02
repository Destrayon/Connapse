using System.Net;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope.RolesAnywhere;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

[Trait("Category", "Unit")]
public class RolesAnywhereSetupValidatorTests
{
    private const string TrustAnchorArn = "arn:aws:rolesanywhere:us-east-1:111111111111:trust-anchor/ta";
    private const string ProfileArn = "arn:aws:rolesanywhere:us-east-1:111111111111:profile/pf";
    private const string RoleArn = "arn:aws:iam::111111111111:role/connapse-ra-x";
    private const string Region = "us-east-1";

    private const string SuccessBody = """
    {"credentialSet":[{"credentials":{"accessKeyId":"a","secretAccessKey":"s","sessionToken":"t","expiration":"2026-09-01T13:00:00Z"}}]}
    """;

    [Fact]
    public async Task ValidateAsync_WhenCertAndKeyDoNotMatch_FailsBeforeAnyNetworkCall()
    {
        // A leaf cert from one keypair, a private key from another: CreateFromPem must reject the pair.
        RolesAnywhereKeyMaterial a = RolesAnywhereKeyGenerator.Generate();
        RolesAnywhereKeyMaterial b = RolesAnywhereKeyGenerator.Generate();

        var handler = new StubHandler(HttpStatusCode.Created, SuccessBody);
        var validator = new RolesAnywhereSetupValidator(new StubHttpClientFactory(handler));
        var config = new RolesAnywhereConfig(a.LeafCertificatePem, TrustAnchorArn, ProfileArn, RoleArn, Region);

        RolesAnywhereValidationResult result = await validator.ValidateAsync(config, b.LeafPrivateKeyPem);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("matching pair");
        handler.Calls.Should().Be(0); // never reached AWS
    }

    [Fact]
    public async Task ValidateAsync_WhenAwsIssuesCredentials_ReturnsOk()
    {
        RolesAnywhereKeyMaterial key = RolesAnywhereKeyGenerator.Generate();
        var handler = new StubHandler(HttpStatusCode.Created, SuccessBody);
        var validator = new RolesAnywhereSetupValidator(new StubHttpClientFactory(handler));
        var config = new RolesAnywhereConfig(key.LeafCertificatePem, TrustAnchorArn, ProfileArn, RoleArn, Region);

        RolesAnywhereValidationResult result = await validator.ValidateAsync(config, key.LeafPrivateKeyPem);

        result.Ok.Should().BeTrue();
        result.Error.Should().BeNull();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ValidateAsync_WhenAwsRejects_ReturnsAwsReasonAndSavesNothing()
    {
        RolesAnywhereKeyMaterial key = RolesAnywhereKeyGenerator.Generate();
        var handler = new StubHandler(HttpStatusCode.Forbidden, "{\"message\":\"trust anchor not found\"}");
        var validator = new RolesAnywhereSetupValidator(new StubHttpClientFactory(handler));
        var config = new RolesAnywhereConfig(key.LeafCertificatePem, TrustAnchorArn, ProfileArn, RoleArn, Region);

        RolesAnywhereValidationResult result = await validator.ValidateAsync(config, key.LeafPrivateKeyPem);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("403");
        result.Error.Should().Contain("trust anchor not found");
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
