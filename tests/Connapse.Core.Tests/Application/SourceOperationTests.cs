using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Sources;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

[Trait("Category", "Unit")]
public class SourceOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);

    private readonly ISourceStore _sources = Substitute.For<ISourceStore>();
    private readonly ISourceVisibility _visibility = Substitute.For<ISourceVisibility>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly ISourceScopeRules _scopes = Substitute.For<ISourceScopeRules>();

    public SourceOperationTests()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        _visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => true);
    }

    private static Source Named(string name) => new(Guid.NewGuid(), name, null, null, "{}", DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task List_NotAViewer_IsForbiddenAndReadsNothing()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Viewer).Returns(false);

        var listed = await new ListSources(_sources, _visibility, _authorizer).RunAsync(Caller, new ListSourcesQuery());

        listed.Status.Should().Be(SourceReadStatus.Forbidden);
        await _sources.DidNotReceiveWithAnyArgs().ListAsync();
    }

    [Theory]
    [InlineData(-1, 50, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task List_PageOutOfRange_IsInvalidPage(int skip, int take, string detail)
    {
        var listed = await new ListSources(_sources, _visibility, _authorizer).RunAsync(Caller, new ListSourcesQuery(skip, take));

        listed.Status.Should().Be(SourceReadStatus.InvalidPage);
        listed.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task List_MoreThanAPage_SaysSoAndHidesWhatTheCallerMayNotSee()
    {
        var hidden = Named("hidden");
        _sources.ListAsync(0, 3, Arg.Any<CancellationToken>()).Returns([Named("a"), hidden, Named("c")]);
        _visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => s => s != hidden);

        var listed = await new ListSources(_sources, _visibility, _authorizer).RunAsync(Caller, new ListSourcesQuery(0, 2));

        listed.Result!.HasMore.Should().BeTrue();
        listed.Result.Items.Select(s => s.Name).Should().Equal("a");
    }

    [Fact]
    public async Task Get_SourceTheCallerMayNotSee_ReadsAsNotFound()
    {
        var source = Named("private");
        _sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
        _visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => false);

        var read = await new GetSource(_sources, _visibility, _authorizer).RunAsync(Caller, source.Id);

        read.Status.Should().Be(SourceReadStatus.NotFound);
        read.Detail.Should().Be($"Source {source.Id} not found");
    }

    [Fact]
    public async Task Update_Secret_IsRefusedBeforeAnythingIsSaved()
    {
        var read = await Update().RunAsync(Caller, Guid.NewGuid(), new UpdateSourceRequest(Secret: "token"));

        read.Status.Should().Be(SourceWriteStatus.Invalid);
        read.Field.Should().Be("secret");
        await _sources.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!);
    }

    [Fact]
    public async Task Update_ScopeTheProviderRefuses_IsInvalidWithItsField()
    {
        var source = Named("custom");
        _sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
        _scopes.CheckUpdateAsync(source, "{\"scopeId\":\"other\"}", Arg.Any<CancellationToken>())
            .Returns(ScopeCheck.Refuse("fixed at creation", "scope"));

        var updated = await Update().RunAsync(Caller, source.Id, new UpdateSourceRequest(ScopeJson: "{\"scopeId\":\"other\"}"));

        updated.Status.Should().Be(SourceWriteStatus.Invalid);
        updated.Field.Should().Be("scope");
        await _sources.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!);
    }

    [Fact]
    public async Task Update_TurningOff_RecordsTheEnabledState()
    {
        var source = Named("paused");
        _sources.UpdateAsync(source.Id, Arg.Any<UpdateSourceRequest>(), Arg.Any<CancellationToken>()).Returns(source);

        await Update().RunAsync(Caller, source.Id, new UpdateSourceRequest(Enabled: false));

        await _audit.Received(1).LogAsync("source.updated", "source", source.Id.ToString(),
            Arg.Is<object>(d => d.ToString() == new { Name = "paused", Enabled = false }.ToString()), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_Rename_RecordsOnlyTheName()
    {
        var source = Named("renamed");
        _sources.UpdateAsync(source.Id, Arg.Any<UpdateSourceRequest>(), Arg.Any<CancellationToken>()).Returns(source);

        await Update().RunAsync(Caller, source.Id, new UpdateSourceRequest(Name: "renamed"));

        await _audit.Received(1).LogAsync("source.updated", "source", source.Id.ToString(),
            Arg.Is<object>(d => d.ToString() == new { Name = "renamed" }.ToString()), Arg.Any<CancellationToken>());
    }

    private UpdateSource Update() => new(_sources, _scopes, _audit, _authorizer);
}
