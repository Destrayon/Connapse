using System.Diagnostics;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Parsers.Code;
using Connapse.Ingestion.Utilities;
using FluentAssertions;
using Xunit.Abstractions;

namespace Connapse.Ingestion.Tests.Parsers;

/// <summary>
/// The code engine's speed gate (CON-110): a changed file is parsed, cut into passages and linked, as ingestion does,
/// without any model, in under a second, and a whole repository in under two minutes. Embedding is outside the gate:
/// it is the same for every file type. Each grammar is loaded before timing starts, so a cold start is not charged
/// to the first file.
/// <para>
/// Timings are only as good as the CPU they get. Beside other test projects on a shared CI runner the same parse took
/// three to four times as long, so these tests are their own category, run in a CI step of their own and never beside
/// other tests in this assembly, and each file's time is the best of three reads.
/// </para>
/// </summary>
[Trait("Category", "Gate")]
[Collection(nameof(CodeGateTests))]
public class CodeGateTests(ITestOutputHelper output)
{
    private static readonly string[] Extensions = [".cs", ".ts", ".tsx", ".mts", ".cts", ".py", ".pyi"];

    private readonly CodeParser _parser = new();
    private readonly CodeChunker _chunker = new(new TiktokenTokenCounter());
    private readonly OwnerRef _owner = OwnerRef.ForContainer(Guid.NewGuid());

    private sealed record Read(bool Outline, int Passages, int Links, TimeSpan Elapsed);

    /// <summary>The fastest of three reads: the others measure what else the machine was doing.</summary>
    private async Task<Read> BestOfThreeAsync(string text, string path)
    {
        Read best = await ReadAsync(text, path);
        for (int i = 0; i < 2; i++)
        {
            Read again = await ReadAsync(text, path);
            if (again.Elapsed < best.Elapsed)
                best = again;
        }
        return best;
    }

    private async Task<Read> ReadAsync(string text, string path)
    {
        var timer = Stopwatch.StartNew();
        ParsedDocument parsed = await _parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), path);
        int passages = (await _chunker.ChunkAsync(parsed, new ChunkingSettings())).Count;
        CodeOutline? outline = CodeOutline.From(parsed.Metadata);
        int links = outline is null ? 0 : CodeLinks.Build(path, _owner, path, parsed.Content, outline).Links.Count;
        return new Read(outline is not null, passages, links, timer.Elapsed);
    }

    private async Task WarmUpAsync()
    {
        foreach (string path in new[] { "w.cs", "w.ts", "w.tsx", "w.py" })
            await ReadAsync("class W { }\n", path);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Connapse.slnx not found above the test output.");
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or "node_modules" or ".git" or ".claude");

    [Fact]
    public async Task EveryCodeFileInThisRepository_IsReadWellInsideTheGate()
    {
        string root = RepositoryRoot();
        List<string> files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f)) && !IsBuildOutput(Path.GetRelativePath(root, f)))
            .ToList();
        await WarmUpAsync();

        var reads = new List<(string File, Read Read)>();
        var total = Stopwatch.StartNew();
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(root, file);
            reads.Add((relative, await ReadAsync(await File.ReadAllTextAsync(file), relative)));
        }
        total.Stop();

        // A file over a tenth of the limit is read twice more, so one stall on a busy runner is not taken for its speed.
        for (int i = 0; i < reads.Count; i++)
        {
            if (reads[i].Read.Elapsed > TimeSpan.FromMilliseconds(100))
            {
                Read best = await BestOfThreeAsync(await File.ReadAllTextAsync(Path.Combine(root, reads[i].File)), reads[i].File);
                if (best.Elapsed < reads[i].Read.Elapsed)
                    reads[i] = (reads[i].File, best);
            }
        }

        var slowest = reads.MaxBy(r => r.Read.Elapsed);
        output.WriteLine($"{files.Count:N0} files ({reads.Count(r => r.Read.Outline):N0} with an outline), " +
                         $"{reads.Sum(r => r.Read.Passages):N0} passages, {reads.Sum(r => r.Read.Links):N0} links " +
                         $"in {total.Elapsed.TotalSeconds:N1} s; slowest {slowest.Read.Elapsed.TotalMilliseconds:N0} ms ({slowest.File})");

        files.Should().HaveCountGreaterThan(500, "the gate is meant to run over the whole repository");
        // The work must have been done, not skipped: nearly every file is read for definitions and linked.
        reads.Count(r => r.Read.Outline).Should().BeGreaterThan(files.Count * 95 / 100);
        reads.Sum(r => r.Read.Passages).Should().BeGreaterThan(files.Count * 3);
        reads.Sum(r => r.Read.Links).Should().BeGreaterThan(files.Count * 20);
        slowest.Read.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        total.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// A large file in each language, generated, since this repository has little TypeScript or Python: 300 classes
    /// of 10 methods, each calling across the file, about 6,000 lines.
    /// </summary>
    [Theory]
    [InlineData("big.ts")]
    [InlineData("big.tsx")]
    [InlineData("big.py")]
    [InlineData("Big.cs")]
    public async Task LargeFileInEachLanguage_IsReadInUnderASecond(string path)
    {
        string source = Path.GetExtension(path) switch
        {
            ".py" => string.Concat(Enumerable.Range(0, 300).Select(c =>
                $"class C{c}:\n" + string.Concat(Enumerable.Range(0, 10).Select(m =>
                    $"    def m{m}(self, other: C{(c + 1) % 300}) -> int:\n        x = other.m{(m + 1) % 10}(self)\n        return helper_{c}(x)\n\n")))),
            ".cs" => "namespace Gate;\n" + string.Concat(Enumerable.Range(0, 300).Select(c =>
                $"public class C{c}\n{{\n" + string.Concat(Enumerable.Range(0, 10).Select(m =>
                    $"    public int M{m}(C{(c + 1) % 300} other)\n    {{\n        var x = other.M{(m + 1) % 10}(this);\n        return Helper.Run(x);\n    }}\n")) + "}\n")),
            _ => string.Concat(Enumerable.Range(0, 300).Select(c =>
                $"export class C{c} {{\n" + string.Concat(Enumerable.Range(0, 10).Select(m =>
                    $"  m{m}(other: C{(c + 1) % 300}): number {{\n    const x = other.m{(m + 1) % 10}(this);\n    return helper{c}(x);\n  }}\n")) + "}\n")),
        };
        await WarmUpAsync();

        Read read = await BestOfThreeAsync(source, path);

        output.WriteLine($"{path}: {source.Split('\n').Length:N0} lines, {read.Passages:N0} passages, {read.Links:N0} links in {read.Elapsed.TotalMilliseconds:N0} ms");
        read.Outline.Should().BeTrue();
        read.Passages.Should().BeGreaterThanOrEqualTo(300);
        read.Links.Should().BeGreaterThan(3000);
        read.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}

/// <summary>Keeps the speed gates from running beside any other test class in this assembly.</summary>
[CollectionDefinition(nameof(CodeGateTests), DisableParallelization = true)]
public class CodeGateCollection;
