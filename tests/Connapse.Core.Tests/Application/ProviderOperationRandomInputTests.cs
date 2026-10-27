using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Providers;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>The provider setup operations' rules over random callers (#783 Phase 4.4): who is refused, and that a refusal asks no provider.</summary>
[Trait("Category", "Unit")]
public class ProviderOperationRandomInputTests
{
    [Fact]
    [RandomInputTestOf(typeof(GetProviderStatus))]
    public Task Status_AnyCaller_ReadsProvidersOnlyForSettingsAdmins() =>
        Gen.Select(Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Int[0, 4]).SampleAsync(async t =>
        {
            var (allowed, surface, count) = t;
            var reader = Substitute.For<IProviderSetupReader>();
            IReadOnlyList<ProviderSetup> setups = [.. Enumerable.Range(0, count).Select(i => new ProviderSetup($"p{i}", $"P{i}", []))];
            reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(setups);
            var authorizer = Substitute.For<IOperationAuthorizer>();
            authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(false);
            authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.SettingsAdmin).Returns(allowed);

            var read = await new GetProviderStatus(reader, authorizer).RunAsync(new OperationContext(new ClaimsPrincipal(), surface));

            if (!allowed)
            {
                read.Status.Should().Be(ProviderStatus.Forbidden);
                reader.ReceivedCalls().Should().BeEmpty("a refused caller must not make Connapse call AWS, Entra or GitHub");
                return;
            }
            read.Status.Should().Be(ProviderStatus.Success);
            read.Result.Should().BeSameAs(setups);
        }, iter: 200);
}
