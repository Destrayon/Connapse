using System.Net;
using System.Security.Cryptography;
using System.Text;
using Connapse.Eval.Datasets;
using FluentAssertions;

namespace Connapse.Eval.Tests.Datasets;

[Trait("Category", "Unit")]
public class DatasetCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eval-cache-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _payload = Encoding.UTF8.GetBytes("hello dataset");
    private readonly CountingHandler _handler;
    private readonly DatasetCache _cache;

    public DatasetCacheTests()
    {
        _handler = new CountingHandler(_payload);
        _cache = new DatasetCache(_root, new HttpClient(_handler));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string PayloadHash => Convert.ToHexStringLower(SHA256.HashData(_payload));

    private static DatasetEntry Entry(string? sha) =>
        new("beir-jsonl", "rev1", ["domain:test"], [new DatasetFile("corpus.jsonl", "https://example.test/corpus.jsonl", sha)]);

    [Fact]
    public async Task EnsureAsync_PinnedFile_DownloadsOnceAndVerifies()
    {
        await _cache.EnsureAsync("ds", Entry(PayloadHash), allowUnpinned: false, CancellationToken.None);
        IReadOnlyDictionary<string, string> hashes =
            await _cache.EnsureAsync("ds", Entry(PayloadHash), allowUnpinned: false, CancellationToken.None);

        _handler.Calls.Should().Be(1);
        hashes["corpus.jsonl"].Should().Be(PayloadHash);
    }

    [Fact]
    public async Task EnsureAsync_WrongHash_ThrowsNamingTheFile()
    {
        Func<Task> act = () => _cache.EnsureAsync("ds", Entry(new string('0', 64)), allowUnpinned: false, CancellationToken.None);

        await act.Should().ThrowAsync<ChecksumMismatchException>().WithMessage("*corpus.jsonl*");
    }

    [Fact]
    public async Task EnsureAsync_UnpinnedWithoutPinMode_Throws()
    {
        Func<Task> act = () => _cache.EnsureAsync("ds", Entry(null), allowUnpinned: false, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*datasets pin*");
    }

    [Fact]
    public async Task EnsureAsync_UnpinnedInPinMode_ReturnsHash()
    {
        IReadOnlyDictionary<string, string> hashes =
            await _cache.EnsureAsync("ds", Entry(null), allowUnpinned: true, CancellationToken.None);

        hashes["corpus.jsonl"].Should().Be(PayloadHash);
    }

    [Fact]
    public void ResolveSuite_UnknownSuite_ThrowsListingKnownSuites()
    {
        EvalManifest manifest = new(
            new Dictionary<string, IReadOnlyList<string>> { ["v1"] = ["ds"] },
            new Dictionary<string, DatasetEntry> { ["ds"] = Entry(null) });

        Action act = () => manifest.ResolveSuite("nightly", null);

        act.Should().Throw<ArgumentException>().WithMessage("*nightly*v1*");
    }

    [Fact]
    public void ResolveSuite_OnlyFilter_KeepsSuiteOrderAndRejectsStrangers()
    {
        EvalManifest manifest = new(
            new Dictionary<string, IReadOnlyList<string>> { ["v1"] = ["a", "b", "c"] },
            new Dictionary<string, DatasetEntry> { ["a"] = Entry(null), ["b"] = Entry(null), ["c"] = Entry(null) });

        manifest.ResolveSuite("v1", ["c", "a"]).Should().Equal("a", "c");
        Action act = () => manifest.ResolveSuite("v1", ["zzz"]);
        act.Should().Throw<ArgumentException>().WithMessage("*zzz*");
    }

    private sealed class CountingHandler(byte[] payload) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}
