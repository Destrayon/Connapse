using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Services;
using FluentAssertions;

namespace Connapse.Core.Tests.Web;

/// <summary>Only a refused client ID or secret marks a site Needs new credentials after a failed sync.</summary>
[Trait("Category", "Unit")]
public class SourceSyncAtlassianHealthTests
{
    [Fact]
    public void RefusedClientIdOrSecret_NeedsNewCredentials()
    {
        var health = SourceSyncService.AtlassianSignInHealth(
            new AtlassianAuthException(AtlassianSyncErrors.CredentialRefused, tokenRefused: true), "ENG");

        health.CredentialRejected.Should().BeTrue();
        health.Succeeded.Should().BeFalse();
        health.Step.Should().Be("token");
        health.Origin.Should().Be("sync");
    }

    [Theory]
    [InlineData(AtlassianSyncErrors.TokenRedirected, true, "token")]
    [InlineData(AtlassianSyncErrors.NoAccessToken, true, "token")]
    [InlineData(AtlassianSyncErrors.FreshTokenRefused, false, "identity")]
    public void OtherSignInProblems_AreAFailedCheckWithTheirOwnMessage_NotNewCredentials(string message, bool tokenRefused, string step)
    {
        var health = SourceSyncService.AtlassianSignInHealth(new AtlassianAuthException(message, tokenRefused), "ENG");

        health.CredentialRejected.Should().BeFalse();
        health.Succeeded.Should().BeFalse();
        health.Step.Should().Be(step);
        health.Message.Should().Contain(message).And.Contain("'ENG'");
    }

    [Fact]
    public void CredentialRefusedText_WithoutATokenRefusal_IsNotTreatedAsRefused()
    {
        SourceSyncService.AtlassianSignInHealth(new AtlassianAuthException(AtlassianSyncErrors.CredentialRefused), "ENG")
            .CredentialRejected.Should().BeFalse();
    }
}
