using Connapse.Core.Interfaces;
using Connapse.Search.Links;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Connapse.Core.Tests.Search;

/// <summary>A file's links are denied, not read under an empty scope, when the caller's permissions can't be resolved.</summary>
[Trait("Category", "Unit")]
public class DocumentLinkReaderScopeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadAsync_ScopeResolutionFails_DeniesWithoutReadingLinks(bool throws)
    {
        var resolver = Substitute.For<ISearchScopeResolver>();
        if (throws)
            resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("resolver down"));
        else
            resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(SearchScopes.Failed);
        var contexts = Substitute.For<IDbContextFactory<KnowledgeDbContext>>();
        var index = Substitute.For<ILinkIndex>();
        var reader = new DocumentLinkReader(contexts, index, resolver, Substitute.For<ISearchResultVerifier>(), NullLogger<DocumentLinkReader>.Instance);

        DocumentLinkSet result = await reader.ReadAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString());

        result.Should().BeSameAs(DocumentLinkSet.DeniedResult);
        await contexts.DidNotReceive().CreateDbContextAsync(Arg.Any<CancellationToken>());
        await index.DidNotReceive().NodesOfAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
