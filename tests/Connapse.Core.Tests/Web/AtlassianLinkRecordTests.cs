using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>
/// The end of the Atlassian link confirmation, after the link is saved. The audit record is the part
/// that must not be lost; marking the link app verified only changes a status label.
/// </summary>
[Trait("Category", "Unit")]
public class AtlassianLinkRecordTests
{
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IProviderCredentialStore _credentials = Substitute.For<IProviderCredentialStore>();
    private readonly Guid _user = Guid.NewGuid();
    private readonly PendingAtlassianLink _link = new(Guid.Empty, "acc-ada", "Ada", null, DateTime.UtcNow, RevocationGeneration: 0);

    private Task RecordAsync() => CloudIdentityEndpoints.RecordAtlassianLinkAsync(
        _audit, _credentials, NullLogger.Instance, _user, _link, CancellationToken.None);

    [Fact]
    public async Task MarkingTheAppVerifiedThrows_TheAuditIsStillWrittenAndNothingThrows()
    {
        _credentials.MarkAtlassianLinkAppVerifiedAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database is down"));

        await FluentActions.Awaiting(RecordAsync).Should().NotThrowAsync("the link is saved; the redirect must still say so");

        await _audit.Received(1).LogAsync("identity.atlassian.linked", "user", _user.ToString(),
            Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheAuditIsWrittenBeforeTheAppIsMarked()
    {
        await RecordAsync();

        Received.InOrder(() =>
        {
            _audit.LogAsync("identity.atlassian.linked", "user", _user.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
            _credentials.MarkAtlassianLinkAppVerifiedAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        });
    }
}
