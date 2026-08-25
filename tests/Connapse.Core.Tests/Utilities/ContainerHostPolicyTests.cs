using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The one address whose meaning depends on who is asking. Everything else a container reaches
/// exactly as the operator's machine does, so these tests are as much about what is left alone
/// as about what is changed.
/// </summary>
[Trait("Category", "Unit")]
public class ContainerHostPolicyTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("  localhost  ")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.1.1")]
    [InlineData("127.255.255.254")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("0:0:0:0:0:0:0:1")]
    [InlineData("[0:0:0:0:0:0:0:1]")]
    [InlineData("::ffff:127.0.0.1")]
    // inet_aton shorthand, which resolvers really do accept: `ssh 127.1` reaches localhost, so
    // these are loopback and not merely things that look like it.
    [InlineData("127.0.0")]
    [InlineData("127.1")]
    public void Resolve_LoopbackInAContainer_BecomesTheDockerHostAlias(string host)
    {
        var result = ContainerHostPolicy.Resolve(host, containerised: true);

        result.Host.Should().Be(ContainerHostPolicy.DockerHostAlias);
        result.Rewritten.Should().BeTrue();
        result.Reason.Should().NotBeNullOrWhiteSpace("a silent rewrite is undebuggable later");
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void Resolve_LoopbackOutsideAContainer_IsLeftAlone(string host)
    {
        // Running under `dotnet run` on the host, loopback is exactly right and the alias would
        // not resolve. The rewrite is only correct because of where Connapse happens to be.
        var result = ContainerHostPolicy.Resolve(host, containerised: false);

        result.Host.Should().Be(host);
        result.Rewritten.Should().BeFalse();
    }

    [Theory]
    [InlineData("192.168.1.50")]
    [InlineData("10.0.0.7")]
    [InlineData("172.20.5.5")]
    [InlineData("files.example.com")]
    [InlineData("myserver.local")]
    [InlineData("host.docker.internal")]
    public void Resolve_AnythingElse_IsLeftAlone(string host)
    {
        // A container reaches a LAN address perfectly well — Docker routes outbound traffic
        // through the host — so there is nothing to translate. A hostname cannot be translated
        // at all: it either resolves from the container's DNS or it does not.
        ContainerHostPolicy.Resolve(host, containerised: true)
            .Should().BeEquivalentTo(new { Host = host, Rewritten = false });
    }

    [Theory]
    [InlineData("127.0.0.1.example.com")]
    [InlineData("127.example.com")]
    [InlineData("localhost.example.com")]
    [InlineData("127.0.0.256")]
    public void IsLoopback_ThingsThatMerelyStartLikeLoopback_AreNot(string host)
    {
        // Prefix matching would claim all of these. "127.0.0.1.example.com" is an ordinary
        // hostname somebody can absolutely own, and rewriting it would send Connapse to the
        // wrong machine entirely.
        ContainerHostPolicy.IsLoopback(host).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NothingTyped_StaysNothing(string? host)
    {
        // Validation's job to complain about, not this one's — and rewriting a blank field into
        // a real address would let an empty form save a connection pointing somewhere.
        var result = ContainerHostPolicy.Resolve(host, containerised: true);

        result.Host.Should().BeEmpty();
        result.Rewritten.Should().BeFalse();
    }
}
