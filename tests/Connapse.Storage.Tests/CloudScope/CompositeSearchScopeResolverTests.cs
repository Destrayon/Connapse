using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class CompositeSearchScopeResolverTests
{
    // The composite composes two ISearchScopeResolver results; drive it through a tiny fake for each
    // cloud rather than the concrete resolvers (those have their own tests).
    private sealed class FakeResolver(SearchScopes result) : ISearchScopeResolver
    {
        public Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private static SearchScopes S3(params string[] prefixes) =>
        SearchScopes.OfPrefixes(prefixes.Select(p => p).ToList());

    private static async Task<SearchScopes> Combine(SearchScopes aws, SearchScopes azure)
    {
        var c = new CompositeSearchScopeResolver.Combiner();
        return await Task.FromResult(c.Combine(aws, azure));
    }

    [Fact]
    public async Task BothUnrestricted_IsUnrestricted() =>
        (await Combine(SearchScopes.Unrestricted, SearchScopes.Unrestricted)).IsUnrestricted.Should().BeTrue();

    [Fact]
    public async Task AwsGranted_AzureUnrestricted_UnionsPrefixesAndAzureWildcard()
    {
        SearchScopes r = await Combine(SearchScopes.OfPrefixes(["s3://bucket/team/"]), SearchScopes.Unrestricted);
        r.IsUnrestricted.Should().BeFalse();
        r.Matches.Select(m => m.Value).Should().BeEquivalentTo("s3://bucket/team/", "azblob://");
    }

    [Fact]
    public async Task AwsUnrestricted_AzureGranted_UnionsAwsWildcardAndAzurePrefixes()
    {
        SearchScopes r = await Combine(SearchScopes.Unrestricted, SearchScopes.OfPrefixes(["azblob://acct/docs/"]));
        r.Matches.Select(m => m.Value).Should().BeEquivalentTo("s3://", "azblob://acct/docs/");
    }

    [Fact]
    public async Task OneCloudFailed_DoesNotDenyTheOther()
    {
        SearchScopes r = await Combine(SearchScopes.Failed, SearchScopes.OfPrefixes(["azblob://acct/"]));
        r.Matches.Select(m => m.Value).Should().BeEquivalentTo("azblob://acct/"); // AWS failed → no s3 matches
    }

    [Fact]
    public async Task BothDeny_IsEmpty()
    {
        SearchScopes r = await Combine(SearchScopes.Failed, SearchScopes.None);
        r.IsEmpty.Should().BeTrue(); // only non-cloud (resource_uri IS NULL) will survive in SQL
    }

    [Fact]
    public async Task AwsGranted_AzureFailed_KeepsAwsHidesAzure()
    {
        SearchScopes r = await Combine(SearchScopes.OfPrefixes(["s3://b/"]), SearchScopes.Failed);
        r.Matches.Select(m => m.Value).Should().BeEquivalentTo("s3://b/");
    }
}
