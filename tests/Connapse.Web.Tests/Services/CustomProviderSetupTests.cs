using Connapse.Core;
using Connapse.Core.Tests.Connectors;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Web.Tests.Services;

/// <summary>What a custom provider must prove before it is saved.</summary>
[Trait("Category", "Unit")]
public class CustomProviderSetupTests
{
    private readonly FakeMiddleMan _remote = new();

    private CustomProviderSetup Setup()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_remote, disposeHandler: false));
        var settings = Substitute.For<IOptionsMonitor<CustomSourceSettings>>();
        settings.CurrentValue.Returns(new CustomSourceSettings());
        return new CustomProviderSetup(http, settings);
    }

    [Fact]
    public async Task Check_WorkingMiddleMan_PassesBothStepsAndReadsItsCapabilities()
    {
        _remote.MaxCheckBatch = 25;

        CustomProviderCheck check = await Setup().CheckAsync(FakeMiddleMan.BaseUrl + "/", FakeMiddleMan.Token, CancellationToken.None);

        check.Error.Should().BeNull();
        check.EndpointOk.Should().BeTrue();
        check.PermissionsOk.Should().BeTrue();
        check.BaseUrl.Should().Be(FakeMiddleMan.BaseUrl);
        check.MaxCheckBatch.Should().Be(25);
        check.SupportsAccessKeys.Should().BeTrue();
    }

    [Fact]
    public async Task Check_WrongToken_FailsAtTheToken()
    {
        CustomProviderCheck check = await Setup().CheckAsync(FakeMiddleMan.BaseUrl, "wrong", CancellationToken.None);

        check.EndpointOk.Should().BeFalse();
        check.Field.Should().Be("secret");
    }

    [Fact]
    public async Task Check_MiddleManThatAcceptsAnyToken_IsRefused()
    {
        _remote.AcceptAnyToken = true;

        CustomProviderCheck check = await Setup().CheckAsync(FakeMiddleMan.BaseUrl, FakeMiddleMan.Token, CancellationToken.None);

        check.EndpointOk.Should().BeFalse();
        check.Error.Should().Contain("wrong token");
    }

    [Fact]
    public async Task Check_MiddleManWithoutPermissionChecks_FailsTheSecondStep()
    {
        _remote.SupportsPermissions = false;

        CustomProviderCheck check = await Setup().CheckAsync(FakeMiddleMan.BaseUrl, FakeMiddleMan.Token, CancellationToken.None);

        check.EndpointOk.Should().BeTrue();
        check.PermissionsOk.Should().BeFalse();
        check.Error.Should().Contain("permission");
    }

    [Fact]
    public async Task Check_CheckCallThatFails_FailsTheSecondStep()
    {
        _remote.Quirk = EvaluationQuirk.ServerError;

        CustomProviderCheck check = await Setup().CheckAsync(FakeMiddleMan.BaseUrl, FakeMiddleMan.Token, CancellationToken.None);

        check.EndpointOk.Should().BeTrue();
        check.PermissionsOk.Should().BeFalse();
    }
}
