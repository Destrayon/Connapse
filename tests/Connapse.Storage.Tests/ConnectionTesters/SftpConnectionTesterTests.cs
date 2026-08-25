using System.Net.Sockets;
using Connapse.Core.Utilities;
using Connapse.Storage.ConnectionTesters;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.ConnectionTesters;

[Trait("Category", "Unit")]
public class SftpConnectionTesterTests
{
    [Fact]
    public void FindHostNotFound_WrappedByTheSshLibrary_IsStillFound()
    {
        // SSH.NET wraps the socket failure, so the exception actually caught never says what
        // went wrong. Checking only the outermost one would miss every real occurrence.
        var wrapped = new InvalidOperationException(
            "Connection failed",
            new AggregateException(
                new SocketException((int)SocketError.HostNotFound)));

        SftpConnectionTester.FindHostNotFound(wrapped).Should().NotBeNull();
    }

    [Fact]
    public void FindHostNotFound_ADifferentSocketFailure_IsNotMistakenForOne()
    {
        // A refused connection means the name resolved and nothing was listening — the opposite
        // diagnosis, and telling that operator about DNS suffixes would send them the wrong way.
        var refused = new SocketException((int)SocketError.ConnectionRefused);

        SftpConnectionTester.FindHostNotFound(refused).Should().BeNull();
    }

    [Fact]
    public void FindHostNotFound_NothingToFind_ReturnsNull()
    {
        SftpConnectionTester.FindHostNotFound(new TimeoutException()).Should().BeNull();
        SftpConnectionTester.FindHostNotFound(null).Should().BeNull();
    }

    [Fact]
    public async Task TestConnectionAsync_AHostThatCannotResolve_ExplainsWhyItWorksElsewhere()
    {
        // The failure whose symptom contradicts the operator's own experience: the name works
        // from their desktop, so a bare "no such host" reads as Connapse being wrong.
        //
        // A real key, because the key is parsed before anything is dialled — a placeholder fails
        // with "Invalid private key file" and never reaches the lookup this test is about.
        var keyPair = SshKeyPairGenerator.Generate("test");

        var result = await new SftpConnectionTester().TestConnectionAsync(
            new SftpConnectionTestSettings
            {
                // .invalid is reserved by RFC 2606 and guaranteed never to resolve, so this
                // cannot start passing because somebody registered a domain.
                Host = "connapse-no-such-host.invalid",
                Username = "nobody",
                AllowedRoot = "/",
                PrivateKey = keyPair.PrivateKeyPem,
            },
            TimeSpan.FromSeconds(10));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("did not resolve");
        result.Message.Should().Contain("the address", "the remedy belongs with the diagnosis");
    }
}
