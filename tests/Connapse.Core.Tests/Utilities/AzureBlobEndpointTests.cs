using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The endpoint a connection reads from must be the account its documents are labelled as. A
/// mismatch is not a configuration nuance: every permission decision is made against the label,
/// and Connapse's storage token would go to whatever host the endpoint named.
/// </summary>
[Trait("Category", "Unit")]
public class AzureBlobEndpointTests
{
    [Fact]
    public void NoOverride_IsTheAccountsPublicEndpoint()
    {
        AzureBlobEndpoint.Validate("myacct", null, out Uri? endpoint).Should().BeNull();
        endpoint.Should().Be(new Uri("https://myacct.blob.core.windows.net"));
    }

    [Theory]
    [InlineData("MyAcct")]
    [InlineData("  myacct ")]
    public void AccountName_IsNormalisedToAzuresSpelling(string typed)
    {
        AzureBlobEndpoint.NormaliseAccount(typed).Should().Be("myacct");
        AzureBlobEndpoint.Validate(typed, null, out Uri? endpoint).Should().BeNull();
        endpoint!.Host.Should().Be("myacct.blob.core.windows.net");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("has-dash")]
    [InlineData("waytoolongforanazurestorageaccountname")]
    public void AccountName_OutsideAzuresRule_IsRefused(string typed) =>
        AzureBlobEndpoint.Validate(typed, null, out _).Should().Contain("3 to 24 lowercase");

    [Theory]
    [InlineData("https://myacct.blob.core.windows.net")]
    [InlineData("https://MYACCT.blob.core.windows.net/")]
    [InlineData("https://myacct.blob.core.usgovcloudapi.net")]
    [InlineData("https://myacct.blob.core.chinacloudapi.cn")]
    public void Override_OnAnAzureCloud_MustBeTheAccountItself(string endpoint) =>
        AzureBlobEndpoint.Validate("myacct", endpoint, out _).Should().BeNull();

    [Theory]
    [InlineData("https://otheracct.blob.core.windows.net")]
    [InlineData("https://myacct.blob.attacker.example")]
    [InlineData("https://attacker.example/myacct")]
    [InlineData("https://myacct.blob.core.windows.net.attacker.example")]
    public void Override_ForAnotherHost_IsRefused(string endpoint)
    {
        // The content would be read from one place and labelled — and authorised — as another,
        // and the token would be sent to a host Connapse has no business talking to.
        AzureBlobEndpoint.Validate("myacct", endpoint, out Uri? resolved).Should().Contain("is not storage account 'myacct'");
        resolved.Should().BeNull();
    }

    [Theory]
    [InlineData("http://127.0.0.1:10000/myacct")]
    [InlineData("http://localhost:10000/myacct/")]
    [InlineData("https://[::1]:10000/MYACCT")]
    public void Override_OnALoopbackEmulator_MustServeTheAccount(string endpoint) =>
        AzureBlobEndpoint.Validate("myacct", endpoint, out _).Should().BeNull();

    [Fact]
    public void Override_OnALoopbackEmulator_ForAnotherAccount_IsRefused() =>
        AzureBlobEndpoint.Validate("myacct", "http://127.0.0.1:10000/devstoreaccount1", out _)
            .Should().Contain("expected http://127.0.0.1:10000/myacct");

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://myacct.blob.core.windows.net")]
    public void Override_ThatIsNotAnHttpAddress_IsRefused(string endpoint) =>
        AzureBlobEndpoint.Validate("myacct", endpoint, out _).Should().Contain("full https:// address");

    [Fact]
    public void Resolve_ThrowsOnAMismatch_SoAConnectorCannotBeBuiltForTheWrongHost()
    {
        var act = () => AzureBlobEndpoint.Resolve("myacct", "https://otheracct.blob.core.windows.net");
        act.Should().Throw<InvalidOperationException>().WithMessage("*is not storage account 'myacct'*");
    }
}
