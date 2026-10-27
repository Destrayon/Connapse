using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Parsers.Code;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class CodeChunkerTests
{
    private readonly CodeChunker _chunker = new(new TiktokenTokenCounter());

    private static async Task<ParsedDocument> ParseAsync(string source, string fileName)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        return await new CodeParser().ParseAsync(stream, fileName);
    }

    private async Task<IReadOnlyList<ChunkInfo>> ChunkAsync(string source, string fileName, int maxChunkSize = 512) =>
        await _chunker.ChunkAsync(await ParseAsync(source, fileName), new ChunkingSettings { MaxChunkSize = maxChunkSize });

    private const string Ledger = """
        using System;

        namespace Acme.Billing;

        /// <summary>Posts entries.</summary>
        public class Ledger : ILedger
        {
            private readonly List<Entry> _entries = [];

            /// <summary>Adds an entry after checking it.</summary>
            public void Post(Entry entry)
            {
                Validate(entry);
                _entries.Add(entry);
                Audit.Write("post", entry.Id, entry.Amount, entry.Account, entry.Description);
                Audit.Write("balance", entry.Id, Balance(), entry.Account, entry.Description);
            }

            public decimal Balance()
            {
                decimal total = 0;
                foreach (var entry in _entries)
                    total += entry.Amount;
                return total;
            }

            private static void Validate(Entry entry)
            {
                if (entry.Amount == 0)
                    throw new ArgumentException("An entry must move money.", nameof(entry));
                if (string.IsNullOrWhiteSpace(entry.Account))
                    throw new ArgumentException("An entry needs an account.", nameof(entry));
            }
        }
        """;

    [Fact]
    public async Task ChunkAsync_TypeOverBudget_GetsAnOutlinePassageAndOneForEachMember()
    {
        var chunks = await ChunkAsync(Ledger, "Ledger.cs", maxChunkSize: 120);

        chunks.Select(c => c.Metadata.GetValueOrDefault(CodeChunker.MetadataKeySymbol)).Should().Equal(
            null, "Acme.Billing.Ledger", "Acme.Billing.Ledger.Post", "Acme.Billing.Ledger.Balance", "Acme.Billing.Ledger.Validate");

        var outline = chunks[1];
        outline.Content.Should().StartWith("class Acme.Billing.Ledger\n/// <summary>Posts entries.</summary>");
        outline.Content.Should().Contain("private readonly List<Entry> _entries = [];");
        outline.Content.Should().Contain("public void Post(Entry entry)\n    {\n        …\n    }");
        outline.Content.Should().NotContain("_entries.Add(entry)");
        outline.Metadata["OffsetEstimated"].Should().Be("true");

        var post = chunks[2];
        post.Content.Should().StartWith("method Acme.Billing.Ledger.Post\n    /// <summary>Adds an entry after checking it.</summary>");
        post.Content.Should().Contain("_entries.Add(entry);");
        (post.Metadata[CodeChunker.MetadataKeyStartLine], post.Metadata[CodeChunker.MetadataKeyEndLine]).Should().Be(("10", "17"));
        Ledger[post.StartOffset..post.EndOffset].TrimEnd().Should().StartWith("    /// <summary>Adds").And.EndWith("}");

        chunks[0].Content.Should().Be("module\nusing System;\n\nnamespace Acme.Billing;\n\n…", "the imports and namespace line are a passage of their own");
        chunks.Should().OnlyContain(c => c.TokenCount <= 120);
        chunks.Should().OnlyContain(c => !c.Metadata.ContainsKey(CodeOutline.ParsedDocumentMetadataKey));
        chunks.Select(c => c.ChunkIndex).Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public async Task ChunkAsync_TypeThatFits_IsOnePassageWithItsMembers()
    {
        var chunks = await ChunkAsync(Ledger, "Ledger.cs", maxChunkSize: 1000);

        chunks.Should().HaveCount(2);
        chunks[1].Metadata[CodeChunker.MetadataKeySymbol].Should().Be("Acme.Billing.Ledger");
        chunks[1].Content.Should().Contain("_entries.Add(entry);").And.Contain("throw new ArgumentException");
    }

    [Fact]
    public async Task ChunkAsync_SmallNeighbouringMembers_ShareAPassage()
    {
        string source = "namespace N;\npublic class Point\n{\n" +
            string.Concat(Enumerable.Range(0, 12).Select(i => $"    public int P{i} {{ get; set; }}\n")) +
            "    public string Describe()\n    {\n" +
            string.Concat(Enumerable.Range(0, 12).Select(i => $"        Console.WriteLine(\"P{i} is \" + P{i} + \" and that is the value of P{i}\");\n")) +
            "        return \"\";\n    }\n}\n";

        var chunks = await ChunkAsync(source, "Point.cs", maxChunkSize: 150);

        var members = chunks.Where(c => c.Metadata[CodeChunker.MetadataKeyKind] == "members").ToList();
        members.Should().NotBeEmpty();
        members[0].Content.Should().StartWith("members N.Point: P0, P1,");
        members.Sum(m => m.Content.Split('\n').Count(l => l.Contains("{ get; set; }"))).Should().Be(12);
        chunks.Should().Contain(c => c.Metadata.GetValueOrDefault(CodeChunker.MetadataKeySymbol) == "N.Point.Describe", "a large member stays out of the merged passages");
    }

    [Fact]
    public async Task ChunkAsync_MemberOverBudget_IsSplitAtLinesWithItsHeaderOnEachPart()
    {
        string source = "def long_function():\n" +
            string.Concat(Enumerable.Range(0, 80).Select(i => $"    value_{i} = compute_something(value_{i}, 'argument number {i}')\n"));

        var chunks = await ChunkAsync(source, "long.py", maxChunkSize: 200);

        chunks.Count.Should().BeGreaterThan(2);
        chunks.Should().OnlyContain(c => c.TokenCount <= 200);
        chunks[0].Content.Should().StartWith($"function long_function (part 1 of {chunks.Count})\ndef long_function():");
        chunks[1].Content.Should().StartWith($"function long_function (part 2 of {chunks.Count})\n    value_");
        string.Join("\n", chunks.Select(c => c.Content[(c.Content.IndexOf('\n') + 1)..]))
            .Should().Be(source.TrimEnd('\n'), "every line is in exactly one part");
    }

    [Fact]
    public async Task ChunkAsync_PythonModule_KeepsImportsAndTopLevelCode()
    {
        const string source = """
            import os

            class Cli:
                def run(self):
                    return os.getcwd()

            if __name__ == "__main__":
                Cli().run()
            """;

        var chunks = await ChunkAsync(source, "cli.py");

        chunks.Select(c => c.Metadata[CodeChunker.MetadataKeyKind]).Should().Equal("module", "class");
        chunks[0].Content.Should().Be("module\nimport os\n\n…\n\nif __name__ == \"__main__\":\n    Cli().run()");
    }

    [Fact]
    public async Task ChunkAsync_NoOutline_SplitsTheFileAtLines()
    {
        string source = string.Concat(Enumerable.Range(0, 200).Select(i => $"let v{i} = f({i});\n"));
        var parsed = new ParsedDocument(source, new Dictionary<string, string> { ["FileType"] = "Code" }, []);

        var chunks = await _chunker.ChunkAsync(parsed, new ChunkingSettings { MaxChunkSize = 100 });

        chunks.Count.Should().BeGreaterThan(5);
        chunks.Should().OnlyContain(c => c.TokenCount <= 100 && c.Metadata[CodeChunker.MetadataKeyKind] == "file");
        chunks[0].Content.Should().StartWith("let v0 = f(0);");
    }

    [Fact]
    public async Task ChunkAsync_EmptyContent_ReturnsNothing()
    {
        var chunks = await _chunker.ChunkAsync(new ParsedDocument(" \n", [], []), new ChunkingSettings());

        chunks.Should().BeEmpty();
    }
}
