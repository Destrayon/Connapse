using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Connapse.Web.Tests.Services;

[Trait("Category", "Unit")]
public class DocumentReadGuardTests
{
    private const string Confluence = "atlassian://00000000-0000-0000-0000-000000000001/confluence/page/1";

    private readonly ISearchResultVerifier _verifier = Substitute.For<ISearchResultVerifier>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly Dictionary<string, string?> _uris = [];
    private readonly Document _document = new("doc-1", "c", "a.md", "text/markdown", "/a.md", 1, DateTime.UtcNow, []);

    public DocumentReadGuardTests()
    {
        _documents.GetResourceUrisAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<string, string?>)_uris);
        // Denies everything, so a test that reaches the verifier can tell.
        _verifier.VerifyAsync(Arg.Any<IReadOnlyList<SearchHit>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchHit>());
    }

    private DocumentReadGuard Guard() => new(_verifier, _documents, NullLogger<DocumentReadGuard>.Instance);

    [Fact]
    public async Task CanReadAsync_NullResourceUri_ReadableWithoutInvokingVerifiers()
    {
        _uris["doc-1"] = null;

        (await Guard().CanReadAsync(Guid.NewGuid(), _document, default)).Should().BeTrue();

        await _verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default, default, default);
    }

    [Fact]
    public async Task CanReadAsync_ProviderDocument_AsksTheVerifier()
    {
        _uris["doc-1"] = Confluence;

        (await Guard().CanReadAsync(Guid.NewGuid(), _document, default)).Should().BeFalse();

        await _verifier.ReceivedWithAnyArgs(1).VerifyAsync(default!, default, default, default);
    }

    [Fact]
    public async Task CanReadAsync_DocumentRowGone_Denies()
    {
        (await Guard().CanReadAsync(Guid.NewGuid(), _document, default)).Should().BeFalse();
    }

    [Fact]
    public async Task CanReadAsync_VerifierThrows_DeniesInsteadOfThrowing()
    {
        _uris["doc-1"] = Confluence;
        _verifier.VerifyAsync(Arg.Any<IReadOnlyList<SearchHit>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        (await Guard().CanReadAsync(Guid.NewGuid(), _document, default)).Should().BeFalse();
    }

    [Fact]
    public async Task CanReadAsync_UriLookupThrows_DeniesInsteadOfThrowing()
    {
        _documents.GetResourceUrisAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("db"));

        (await Guard().CanReadAsync(Guid.NewGuid(), _document, default)).Should().BeFalse();
    }

    [Fact]
    public async Task CanReadAsync_Cancelled_Throws()
    {
        _uris["doc-1"] = Confluence;
        _verifier.VerifyAsync(Arg.Any<IReadOnlyList<SearchHit>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await FluentActions.Awaiting(() => Guard().CanReadAsync(Guid.NewGuid(), _document, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
