using System.Security.Claims;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Sources;

/// <summary>A private GitHub source is listed only to those the permission check lets read its repository.</summary>
[Trait("Category", "Unit")]
public class PrivateSourceVisibilityTests
{
    private static bool IsVisible(Source source, IReadOnlySet<string> granted) =>
        PrivateSourceVisibility.IsVisible(source, granted, new HashSet<Guid>());

    private static Source With(string scope) => new(Guid.NewGuid(), "acme/infra issues", null, Guid.NewGuid(), scope, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void PublicAndNonGitHubSources_AreVisibleToEveryone()
    {
        IsVisible(With("""{"owner":"acme","repo":"docs","repoId":5}"""), new HashSet<string>()).Should().BeTrue();
        IsVisible(With("""{"bucketName":"b"}"""), new HashSet<string>()).Should().BeTrue();
    }

    [Fact]
    public void PrivateSource_IsVisibleOnlyWhenItsRepositoryIsGranted()
    {
        var source = With("""{"owner":"acme","repo":"infra","repoId":99,"private":true}""");

        IsVisible(source, new HashSet<string>()).Should().BeFalse();
        IsVisible(source, new HashSet<string> { "github://999/" }).Should().BeFalse();
        IsVisible(source, new HashSet<string> { "github://99/" }).Should().BeTrue();
    }

    [Fact]
    public void PublicGitHubSourceThatLostAccess_IsHidden()
    {
        var source = With("""{"owner":"acme","repo":"docs","repoId":5}""") with { AccessRevokedAt = DateTime.UtcNow };

        IsVisible(source, new HashSet<string>()).Should().BeFalse(
            "a repository made private must not keep its name and summary listed to everyone");
    }

    [Fact]
    public void NonGitHubSourceThatLostAccess_StaysVisible() =>
        IsVisible(With("""{"bucketName":"b"}""") with { AccessRevokedAt = DateTime.UtcNow }, new HashSet<string>())
            .Should().BeTrue();

    [Fact]
    public void PrivateSourceWithoutARepositoryId_IsHidden() =>
        IsVisible(With("""{"owner":"acme","repo":"infra","private":true}"""), new HashSet<string> { "github://99/" })
            .Should().BeFalse();

    [Fact]
    public async Task AnySourceOnAnAtlassianConnection_IsHiddenFromNonAdmins_WhateverItsKind()
    {
        var atlassian = new Connection(Guid.NewGuid(), "acme.atlassian.net", ConnectionProvider.Atlassian, "{}", null, DateTime.UtcNow, DateTime.UtcNow);
        var s3 = new Connection(Guid.NewGuid(), "bucket", ConnectionProvider.S3, "{}", null, DateTime.UtcNow, DateTime.UtcNow);
        var connections = Substitute.For<IConnectionStore>();
        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([atlassian, s3]);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        var visibility = new PrivateSourceVisibility(Substitute.For<ISearchScopeResolver>(), authorization, connections);

        var visible = await visibility.ForAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        visible(With("""{"kind":"jira-project","projectKey":"OPS"}""") with { ConnectionId = atlassian.Id }).Should().BeFalse();
        visible(With("""{"bucketName":"b"}""") with { ConnectionId = s3.Id }).Should().BeTrue();
        await connections.Received(1).ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SourceOnAnUnknownConnection_IsHiddenWhenConnectionsCouldNotBeRead() =>
        PrivateSourceVisibility.IsVisible(source: With("""{"bucketName":"b"}"""), new HashSet<string>(), atlassianConnections: null)
            .Should().BeFalse();

    [Theory]
    [InlineData("""{"kind":"confluence-space","spaceKey":"ENG"}""")]
    [InlineData("""{"kind":"Confluence-Space","spaceKey":"ENG"}""")]
    public void ConfluenceSpaceSource_IsHiddenFromNonAdmins(string scope) =>
        IsVisible(With(scope), new HashSet<string> { "atlassian://" }).Should().BeFalse();
}
