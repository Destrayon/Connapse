using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.OAuthClients;
using Connapse.Application.Ports;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>oauthClients.list and oauthClients.revoke over random callers and clients (#881).</summary>
[Trait("Category", "Unit")]
public class OAuthClientOperationRandomInputTests
{
    private static readonly Gen<OAuthClientSummary> AnyClient = Gen.Select(
        Gen.String[Gen.Char["ab:/."], 1, 8], Gen.Bool, Gen.Int[0, 3], Gen.Int[0, 3],
        (id, registered, grants, users) => new OAuthClientSummary(id, id, "native", [], registered, null, null, grants, users, null));

    private static IOperationAuthorizer Authorizer(bool allowed)
    {
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(false);
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.ClientAdmin).Returns(allowed);
        return authorizer;
    }

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(), s));

    [Fact]
    [RandomInputTestOf(typeof(ListOAuthClients))]
    public Task List_AnyClients_ListsThemAllForClientAdminsOnly() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyClient.Array[0, 5]).SampleAsync(async t =>
        {
            var (caller, allowed, stored) = t;
            var registry = Substitute.For<IOAuthClientRegistry>();
            registry.ListAsync(Arg.Any<CancellationToken>()).Returns(stored);

            var listed = await new ListOAuthClients(registry, Authorizer(allowed)).RunAsync(caller);

            if (!allowed)
            {
                listed.Status.Should().Be(OAuthClientStatus.Forbidden);
                registry.ReceivedCalls().Should().BeEmpty();
                return;
            }
            listed.Status.Should().Be(OAuthClientStatus.Success);
            listed.Result.Should().Equal(stored);
        }, iter: 1000);

    [Fact]
    [RandomInputTestOf(typeof(RevokeOAuthClient))]
    public Task Revoke_AnyClient_RevokesAndAuditsOnlyWhatAClientAdminNamed() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.String[Gen.Char["ab:/.%"], 0, 8], Gen.Bool, Gen.Bool, Gen.Int[0, 3], Gen.Int[0, 3])
            .SampleAsync(async t =>
            {
                var (caller, allowed, clientId, exists, removed, tokens, codes) = t;
                var registry = Substitute.For<IOAuthClientRegistry>();
                var revocation = new OAuthClientRevocation(clientId, removed, tokens, codes);
                registry.RevokeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((OAuthClientRevocation?)null);
                if (exists)
                    registry.RevokeAsync(clientId, Arg.Any<CancellationToken>()).Returns(revocation);
                var audit = Substitute.For<IAuditLogger>();

                var revoked = await new RevokeOAuthClient(registry, audit, Authorizer(allowed)).RunAsync(caller, clientId);

                if (!allowed)
                {
                    revoked.Status.Should().Be(OAuthClientStatus.Forbidden);
                    registry.ReceivedCalls().Should().BeEmpty();
                    audit.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                await registry.Received(1).RevokeAsync(clientId, Arg.Any<CancellationToken>());
                if (!exists)
                {
                    revoked.Status.Should().Be(OAuthClientStatus.NotFound);
                    audit.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                revoked.Status.Should().Be(OAuthClientStatus.Success);
                revoked.Result.Should().Be(revocation);
                await audit.Received(1).LogAsync(RevokeOAuthClient.RevokedEvent, "oauth_client", clientId, Arg.Any<object?>(), Arg.Any<CancellationToken>());
                audit.ReceivedCalls().Should().HaveCount(1);
            }, iter: 1000);
}
