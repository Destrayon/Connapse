using Connapse.Background.Jobs;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Background.Tests.Jobs;

/// <summary>
/// Unit tests for the doc-set hash function used by <see cref="SummaryJobs.RollupContainerAsync"/>
/// to short-circuit no-op rollups. Verifies the post-HERCULES formula
/// <c>(docId, content_hash)</c> behaves deterministically and is content-sensitive.
/// </summary>
[Trait("Category", "Unit")]
public class SummaryJobsHashTests
{
    private static Document MakeDoc(string id, string contentHash) => new(
        Id: id,
        ContainerId: Guid.NewGuid().ToString(),
        FileName: "f.txt",
        ContentType: "text/plain",
        Path: "/f.txt",
        SizeBytes: 1,
        CreatedAt: DateTime.UtcNow,
        Metadata: new Dictionary<string, string> { ["ContentHash"] = contentHash });

    [Fact]
    public void ComputeDocSetHash_SameContent_OrderIndependent_ProducesSameHash()
    {
        var docsA = new[]
        {
            MakeDoc("11111111-1111-1111-1111-111111111111", "hash-a"),
            MakeDoc("22222222-2222-2222-2222-222222222222", "hash-b"),
        };
        var docsB = new[]
        {
            MakeDoc("22222222-2222-2222-2222-222222222222", "hash-b"),
            MakeDoc("11111111-1111-1111-1111-111111111111", "hash-a"),
        };

        SummaryJobs.ComputeDocSetHash(docsA).Should().Be(SummaryJobs.ComputeDocSetHash(docsB));
    }

    [Fact]
    public void ComputeDocSetHash_DifferentContentHash_ProducesDifferentHash()
    {
        string hashA = SummaryJobs.ComputeDocSetHash(new[] { MakeDoc("11111111-1111-1111-1111-111111111111", "hash-a") });
        string hashB = SummaryJobs.ComputeDocSetHash(new[] { MakeDoc("11111111-1111-1111-1111-111111111111", "hash-b") });

        hashA.Should().NotBe(hashB);
    }

    [Fact]
    public void ComputeDocSetHash_DifferentDocIds_ProducesDifferentHash()
    {
        string hashA = SummaryJobs.ComputeDocSetHash(new[] { MakeDoc("11111111-1111-1111-1111-111111111111", "hash-a") });
        string hashB = SummaryJobs.ComputeDocSetHash(new[] { MakeDoc("22222222-2222-2222-2222-222222222222", "hash-a") });

        hashA.Should().NotBe(hashB);
    }

    [Fact]
    public void ComputeDocSetHash_MissingContentHashKey_TreatsAsEmpty_NoThrow()
    {
        var docs = new[]
        {
            new Document(
                Id: Guid.NewGuid().ToString(),
                ContainerId: Guid.NewGuid().ToString(),
                FileName: "f.txt",
                ContentType: "text/plain",
                Path: "/f.txt",
                SizeBytes: 1,
                CreatedAt: DateTime.UtcNow,
                Metadata: new Dictionary<string, string>()) // no ContentHash key
        };

        Action act = () => SummaryJobs.ComputeDocSetHash(docs);
        act.Should().NotThrow();
    }

    [Fact]
    public void ComputeDocSetHash_EmptyInput_ReturnsStableHashOfEmptyString()
    {
        string hash = SummaryJobs.ComputeDocSetHash(Array.Empty<Document>());
        hash.Should().NotBeNullOrEmpty();
        // Same input should always produce the same hash; two empty runs must match.
        hash.Should().Be(SummaryJobs.ComputeDocSetHash(Array.Empty<Document>()));
    }
}
