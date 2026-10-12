using System.Net;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>
/// The linking app's lifecycle as the Providers page and the REST API share it: nothing is stored
/// until Atlassian accepts the client ID and secret, removing needs the client ID typed, and every
/// step is audited without the secret.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AtlassianLinkAppServiceTests : IDisposable
{
    private const string Callback = "https://connapse.example/api/v1/auth/cloud/atlassian/callback";
    private const string Secret = "link-secret-value-123";

    private readonly FakeAtlassianApi _api = new();
    private readonly IProviderCredentialStore _credentials = Substitute.For<IProviderCredentialStore>();
    private readonly IAtlassianIdentityLinkReader _links = Substitute.For<IAtlassianIdentityLinkReader>();
    private readonly List<(string Action, string Details)> _events = [];
    private readonly AtlassianLinkAppService _service;

    public AtlassianLinkAppServiceTests()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => _api.CreateClient());

        var audit = Substitute.For<IAuditLogger>();
        audit.LogAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => _events.Add((c.ArgAt<string>(0), JsonSerializer.Serialize(c.ArgAt<object?>(3)))));

        _service = new AtlassianLinkAppService(_credentials, new AtlassianUserSignIn(http, _credentials), _links, audit,
            NullLogger<AtlassianLinkAppService>.Instance);
    }

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task SaveAsync_Accepted_StoresTheAppAndAuditsTheCheckAndTheSave()
    {
        // Atlassian accepted the client and refused only the made-up code.
        _api.FailTokenWith(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var outcome = await _service.SaveAsync(" link-client ", Secret, Callback, userId: null);

        outcome.Should().Be(AtlassianLinkAppSaveOutcome.Saved);
        await _credentials.Received(1).SaveAtlassianLinkAppAsync("link-client", Secret, null, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
        _events.Select(e => e.Action).Should().Equal("provider.credential.checked", "provider.credential.saved");
        _events.Should().OnlyContain(e => !e.Details.Contains(Secret));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"access_denied"}""", AtlassianLinkAppSaveOutcome.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, null, AtlassianLinkAppSaveOutcome.Unreachable)]
    public async Task SaveAsync_NotAccepted_StoresNothing(HttpStatusCode status, string? body, AtlassianLinkAppSaveOutcome expected)
    {
        _api.FailTokenWith(status, body);

        (await _service.SaveAsync("link-client", Secret, Callback, null)).Should().Be(expected);

        await _credentials.DidNotReceiveWithAnyArgs().SaveAtlassianLinkAppAsync(default!, default!, default, default, default);
        _events.Select(e => e.Action).Should().Equal("provider.credential.checked");
    }

    [Fact]
    public async Task SaveAsync_MissingSecret_AsksAtlassianNothing()
    {
        (await _service.SaveAsync("link-client", " ", Callback, null)).Should().Be(AtlassianLinkAppSaveOutcome.MissingFields);
        _api.TokenRequests.Should().Be(0);
    }

    [Fact]
    public async Task SetSignInEnabledAsync_TurnsItOffAndAudits_OrSaysNothingIsStored()
    {
        _credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(new AtlassianLinkAppRegistration("link-client"), (AtlassianLinkAppRegistration?)null);
        _credentials.SetAtlassianSignInEnabledAsync(false, Arg.Any<CancellationToken>()).Returns(true);

        (await _service.SetSignInEnabledAsync(false)).Should().BeTrue();
        (await _service.SetSignInEnabledAsync(false)).Should().BeFalse();

        _events.Should().ContainSingle(e => e.Action == "provider.sign_in.disabled");
    }

    [Fact]
    public async Task RemoveAsync_NeedsTheStoredClientId()
    {
        _credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(new AtlassianLinkAppRegistration("link-client"));

        (await _service.RemoveAsync("other-client")).Should().Be(AtlassianLinkAppRemoveOutcome.ConfirmMismatch);
        (await _service.RemoveAsync(null)).Should().Be(AtlassianLinkAppRemoveOutcome.ConfirmMismatch);
        await _credentials.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);

        (await _service.RemoveAsync("link-client")).Should().Be(AtlassianLinkAppRemoveOutcome.Removed);
        await _credentials.Received(1).DeleteAsync("atlassian", Arg.Any<CancellationToken>());
        _events.Should().ContainSingle(e => e.Action == "provider.credential.deleted").Which.Details.Should().Contain("link-client");
    }

    [Fact]
    public async Task RemoveAsync_NothingStored_SaysSo()
    {
        (await _service.RemoveAsync("link-client")).Should().Be(AtlassianLinkAppRemoveOutcome.NotStored);
    }

    [Fact]
    public async Task CheckAndSaveAsync_Rejected_KeepsAtlassiansCodeAndAuditsIt()
    {
        _api.FailTokenWith(HttpStatusCode.Unauthorized, """{"error":"access_denied"}""");

        var result = await _service.CheckAndSaveAsync("link-client", Secret, Callback, null);

        result.Should().Be(new AtlassianLinkAppSaveResult(AtlassianLinkAppSaveOutcome.Rejected, "HTTP 401, access_denied"));
        _events.Should().ContainSingle().Which.Details.Should().Contain("access_denied").And.NotContain(Secret);
    }

    [Fact]
    public async Task SaveAsync_Accepted_RemembersTheCallbackUrl()
    {
        _api.FailTokenWith(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        await _service.SaveAsync("link-client", Secret, Callback, null);

        await _credentials.Received(1).RememberAtlassianCallbackUrlAsync("link-client", Callback, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecheckWithDetailAsync_Answered_RecordsItAndRemembersTheCallbackUrl()
    {
        StoreApp(new AtlassianLinkAppRegistration("link-client"));
        _api.FailTokenWith(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");

        var result = await _service.RecheckWithDetailAsync(Callback);

        result.Should().Be(new AtlassianLinkAppCheckResult(AtlassianLinkAppCheck.Rejected, "HTTP 401, invalid_client"));
        await _credentials.Received(1).RecordAtlassianLinkAppCheckAsync("link-client", false, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _credentials.Received(1).RememberAtlassianCallbackUrlAsync("link-client", Callback, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecheckWithDetailAsync_NoAnswer_RemembersNothing()
    {
        StoreApp(new AtlassianLinkAppRegistration("link-client"));
        _api.FailTokenWith(HttpStatusCode.InternalServerError, null);

        (await _service.RecheckWithDetailAsync(Callback))!.Result.Should().Be(AtlassianLinkAppCheck.Unknown);

        await _credentials.DidNotReceiveWithAnyArgs().RememberAtlassianCallbackUrlAsync(default!, default!, default);
        await _credentials.DidNotReceiveWithAnyArgs().RecordAtlassianLinkAppCheckAsync(default!, default, default, default);
    }

    [Fact]
    public async Task ScheduledRecheckAsync_ChecksWithTheRememberedCallbackUrl()
    {
        StoreApp(new AtlassianLinkAppRegistration("link-client", CallbackUrl: Callback));
        _api.FailTokenWith(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var result = await _service.ScheduledRecheckAsync();

        result!.Result.Should().Be(AtlassianLinkAppCheck.Accepted);
        _api.TokenRequests.Should().Be(1);
        await _credentials.Received(1).RecordAtlassianLinkAppCheckAsync("link-client", true, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        _events.Should().ContainSingle(e => e.Action == "provider.credential.checked");
    }

    [Theory]
    [InlineData(false, true, "no app stored")]
    [InlineData(true, false, "sign-in is off")]
    [InlineData(true, true, "no callback URL known yet")]
    public async Task ScheduledRecheckAsync_SkipsWhenThereIsNothingToCheck(bool stored, bool signInEnabled, string because)
    {
        if (stored)
            StoreApp(new AtlassianLinkAppRegistration("link-client", SignInEnabled: signInEnabled,
                CallbackUrl: signInEnabled ? null : Callback));

        (await _service.ScheduledRecheckAsync()).Should().BeNull(because);

        _api.TokenRequests.Should().Be(0);
        await _credentials.DidNotReceiveWithAnyArgs().RecordAtlassianLinkAppCheckAsync(default!, default, default, default);
    }

    [Fact]
    public async Task CheckJob_RunsTheScheduledRecheck()
    {
        StoreApp(new AtlassianLinkAppRegistration("link-client", CallbackUrl: Callback));
        _api.FailTokenWith(HttpStatusCode.Forbidden, """{"error":"access_denied"}""");

        await new AtlassianLinkAppCheckJob(_service, NullLogger<AtlassianLinkAppCheckJob>.Instance).RunAsync(CancellationToken.None);

        await _credentials.Received(1).RecordAtlassianLinkAppCheckAsync("link-client", false, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckJob_NothingStored_DoesNothing()
    {
        await new AtlassianLinkAppCheckJob(_service, NullLogger<AtlassianLinkAppCheckJob>.Instance).RunAsync(CancellationToken.None);

        _api.TokenRequests.Should().Be(0);
        _events.Should().BeEmpty();
    }

    private void StoreApp(AtlassianLinkAppRegistration app)
    {
        _credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(app);
        _credentials.GetAtlassianLinkAppSecretAsync(Arg.Any<CancellationToken>()).Returns(Secret);
    }

    [Fact]
    public async Task CountLinkedPeopleAsync_WhenCountingFails_IsNull()
    {
        _links.CountLinksAsync(Arg.Any<CancellationToken>()).Returns<int>(_ => throw new InvalidOperationException("db down"));

        (await _service.CountLinkedPeopleAsync()).Should().BeNull();
    }
}
