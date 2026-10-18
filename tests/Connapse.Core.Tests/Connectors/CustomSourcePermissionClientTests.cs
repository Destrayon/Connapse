using Connapse.Storage.Connectors.Custom;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>The protocol client's two permission calls: the user's access keys, and the batched check.</summary>
[Trait("Category", "Unit")]
public class CustomSourcePermissionClientTests
{
    private const string Alice = "connapse-alice";
    private readonly FakeMiddleMan _remote = new();

    private CustomSourceClient Client() => new(new HttpClient(_remote), FakeMiddleMan.BaseUrl, FakeMiddleMan.Token);

    public CustomSourcePermissionClientTests()
    {
        _remote.Put("a", "README.md", "a");
        _remote.Put("b", "design/b.md", "b");
        _remote.Put("c", "secret/c.md", "c");
        _remote.Grants[Alice] = ["folder:", "folder:design"];
        _remote.Denies[Alice] = ["b"];
    }

    [Fact]
    public async Task Discovery_ReadsThePermissionCapabilities()
    {
        _remote.MaxCheckBatch = 7;

        CustomDiscovery discovery = await Client().GetDiscoveryAsync(CancellationToken.None);

        discovery.Capabilities!.Permissions!.Check!.MaxBatch.Should().Be(7);
        discovery.Capabilities.Permissions.AccessKeys.Should().NotBeNull();
    }

    [Fact]
    public async Task SearchAccessKeys_ReturnsEveryKeyAcrossPages()
    {
        _remote.PageSize = 1;

        CustomAccessKeys keys = await Client().SearchAccessKeysAsync(FakeMiddleMan.Scope, Alice, maxKeys: 100, CancellationToken.None);

        keys.Keys.Should().Equal("folder:", "folder:design");
        keys.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task SearchAccessKeys_PastTheLimit_StopsAndSaysSo()
    {
        _remote.PageSize = 1;

        CustomAccessKeys keys = await Client().SearchAccessKeysAsync(FakeMiddleMan.Scope, Alice, maxKeys: 1, CancellationToken.None);

        keys.Keys.Should().Equal("folder:");
        keys.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task SearchAccessKeys_RepeatedPageToken_Throws()
    {
        _remote.RepeatPageToken = true;

        Func<Task> act = () => Client().SearchAccessKeysAsync(FakeMiddleMan.Scope, Alice, 100, CancellationToken.None);

        await act.Should().ThrowAsync<CustomSourceException>().WithMessage("*repeated*");
    }

    [Fact]
    public async Task Evaluate_ReturnsOneDecisionPerDocumentInOrder()
    {
        IReadOnlyList<bool> decisions = await Client().EvaluateAsync(
            Alice, FakeMiddleMan.Scope, ["a", "b", "c", "missing"], maxBatch: 100, CancellationToken.None);

        decisions.Should().Equal(true, false, false, false);
    }

    [Fact]
    public async Task Evaluate_SplitsIntoBatchesNoLargerThanTheMiddleMansLimit()
    {
        _remote.MaxCheckBatch = 2;

        IReadOnlyList<bool> decisions = await Client().EvaluateAsync(
            Alice, FakeMiddleMan.Scope, ["a", "b", "c", "a", "missing"], maxBatch: 2, CancellationToken.None);

        decisions.Should().Equal(true, false, false, true, false);
        _remote.EvaluationCalls.Should().Be(3);
    }

    [Fact]
    public async Task Evaluate_ErrorAlongsideTrue_IsNoAccess()
    {
        _remote.Quirk = EvaluationQuirk.ErrorAlongsideTrue;

        IReadOnlyList<bool> decisions = await Client().EvaluateAsync(Alice, FakeMiddleMan.Scope, ["a"], 100, CancellationToken.None);

        decisions.Should().Equal(false);
    }

    [Theory]
    [InlineData(EvaluationQuirk.WrongCount)]
    [InlineData(EvaluationQuirk.ServerError)]
    public async Task Evaluate_AnswerThatCannotBeTrusted_Throws(EvaluationQuirk quirk)
    {
        _remote.Quirk = quirk;

        Func<Task> act = () => Client().EvaluateAsync(Alice, FakeMiddleMan.Scope, ["a", "b"], 100, CancellationToken.None);

        await act.Should().ThrowAsync<CustomSourceException>();
    }
}
