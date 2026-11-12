using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Connapse.Eval.Model;
using Connapse.Eval.Runs;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

// CON-312: the reserve re-test reads only the file the manifest pinned, asks it whole, and pauses while memory is short.
// None of these tests opens the sealed reserve file itself.
[Trait("Category", "Unit")]
public sealed class TablesLinkedReserveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "con312-reserve-" + Guid.NewGuid().ToString("N"));

    public TablesLinkedReserveTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Line = """{"id":"q1","kind":"table","question":"How many?","answer_type":"number","answer":3,"document":"linked/docs/a.docx"}""";

    private static TablesLinkedEval.Options Options(bool reserve = false, IReadOnlyList<string>? ids = null, int? limit = null) =>
        new("hybrid", "claude-sonnet-5-5", "medium", limit, ids ?? [], 1, null, Reserve: reserve);

    private static EvalDocument Report(string id) => new(id, DocumentKind.File, id, null, null, new Dictionary<string, string>(), id);

    private string Gzip(string text)
    {
        string path = Path.Combine(_root, "set.jsonl.gz");
        using (var gz = new GZipStream(File.Create(path), CompressionLevel.Optimal))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return path;
    }

    [Fact]
    public void ReserveSha256_MatchesTheManifestPin()
    {
        string root = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(root);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eval", "MANIFEST.json")))
            dir = dir.Parent;
        string manifest = File.ReadAllText(Path.Combine(dir!.FullName, "eval", "MANIFEST.json"));

        manifest.Should().Contain($"\"sha256\": \"{TablesLinkedEval.ReserveSha256}\"");
        manifest.Should().Contain("repo:tables/linked/" + TablesLinkedEval.ReserveFile);
    }

    [Fact]
    public void ReadSealed_PinnedHash_ReadsEveryQuestion()
    {
        string path = Gzip(Line + "\n\n" + Line.Replace("q1", "q2", StringComparison.Ordinal) + "\n");
        string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        LinkedQuestion.ReadSealed(path, sha).Select(q => q.Id).Should().Equal("q1", "q2");
    }

    [Fact]
    public void ReadSealed_OtherHash_RefusesWithoutReading()
    {
        string path = Gzip(Line);

        Action read = () => LinkedQuestion.ReadSealed(path, new string('0', 64));

        read.Should().Throw<InvalidDataException>().WithMessage("*not the pinned*").Which.Message.Should().NotContain("How many");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Questions_ReserveWithIdsOrLimit_IsRefused(bool ids, bool limit)
    {
        Action pick = () => TablesLinkedEval.Questions(_root, Options(true, ids ? ["q1"] : null, limit ? 1 : null), []);

        pick.Should().Throw<ArgumentException>().WithMessage("*--reserve*");
    }

    [Fact]
    public void Questions_DocumentNotAmongTheReports_IsRefused()
    {
        File.WriteAllText(Path.Combine(_root, "questions.jsonl"), Line + "\n");

        TablesLinkedEval.Questions(_root, Options(), [Report("linked/docs/a.docx")]).Should().ContainSingle();
        Action pick = () => TablesLinkedEval.Questions(_root, Options(), [Report("linked/docs/b.docx")]);
        pick.Should().Throw<InvalidDataException>().WithMessage("1 question(s)*");
    }

    [Fact]
    public async Task MemoryGate_LowMemory_WaitsUntilEnoughIsFree()
    {
        var readings = new Queue<long?>([1, 2, 10]);
        var log = new StringWriter();

        await MemoryGate.WaitAsync(10, () => readings.Dequeue(), TimeSpan.Zero, log, CancellationToken.None);

        readings.Should().BeEmpty();
        log.ToString().Should().Contain("pausing").And.Contain("continuing");
    }

    [Fact]
    public async Task MemoryGate_EnoughOrUnknown_DoesntWait()
    {
        var log = new StringWriter();

        await MemoryGate.WaitAsync(10, () => 10, TimeSpan.FromHours(1), log, CancellationToken.None);
        await MemoryGate.WaitAsync(10, () => null, TimeSpan.FromHours(1), log, CancellationToken.None);

        log.ToString().Should().BeEmpty();
    }
}
