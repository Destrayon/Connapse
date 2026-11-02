using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search;
using Connapse.Search.Code;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Connapse.Integration.Tests.Code;

/// <summary>The code tools (CON-37) over code ingested end to end, and never past a file the caller cannot read.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CodeToolsIntegrationTests(SharedWebAppFixture fixture)
{
    private const string AuditLog = """
        namespace Acme.Audit;

        public static class AuditLog
        {
            public static void Write(object entry) { }
        }
        """;

    private const string Ledger = """
        using Acme.Audit;

        namespace Acme.Billing;

        public class Ledger
        {
            public void Post(object entry)
            {
                AuditLog.Write(entry);
            }
        }
        """;

    private const string Invoices = """
        namespace Acme.Billing;

        public class Invoices
        {
            private readonly Ledger _ledger = new();

            public void Close(object invoice)
            {
                _ledger.Post(invoice);
            }
        }
        """;

    private const string LedgerTests = """
        using Acme.Billing;

        namespace Acme.Tests;

        public class LedgerTests
        {
            public void Post_Writes()
            {
                Ledger ledger = new Ledger();
                ledger.Post(1);
            }
        }
        """;

    private const string Notes = """
        namespace Acme.Docs;

        // Post is what the ledger calls when an invoice closes.
        public class Notes { }
        """;

    private static async Task<Guid> SeedContainerAsync(IServiceProvider sp)
    {
        await using KnowledgeDbContext db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"code-{Guid.NewGuid():N}" };
        db.Containers.Add(container);
        await db.SaveChangesAsync();
        return container.Id;
    }

    private static async Task<string> IngestAsync(IServiceProvider sp, Guid container, string path, string text)
    {
        var result = await sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(text)),
            new IngestionOptions(FileName: Path.GetFileName(path), ContentType: "text/plain", Path: path)
            {
                Owner = OwnerRef.ForContainer(container),
            });
        return result.DocumentId;
    }

    private static async Task<(Guid Container, Dictionary<string, string> Docs)> SeedCSharpAsync(IServiceProvider sp)
    {
        Guid container = await SeedContainerAsync(sp);
        Dictionary<string, string> docs = new()
        {
            ["AuditLog"] = await IngestAsync(sp, container, "/acme/AuditLog.cs", AuditLog),
            ["Ledger"] = await IngestAsync(sp, container, "/acme/Ledger.cs", Ledger),
            ["Invoices"] = await IngestAsync(sp, container, "/acme/Invoices.cs", Invoices),
            ["LedgerTests"] = await IngestAsync(sp, container, "/tests/LedgerTests.cs", LedgerTests),
            ["Notes"] = await IngestAsync(sp, container, "/acme/Notes.cs", Notes),
        };
        return (container, docs);
    }

    private sealed class RejectSynced : ISearchResultVerifier
    {
        public int CandidateMultiplier => 1;

        public Task<IReadOnlyList<SearchHit>> VerifyAsync(
            IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);
    }

    private sealed class ThrowingVerifier : ISearchResultVerifier
    {
        public int CandidateMultiplier => 1;

        public Task<IReadOnlyList<SearchHit>> VerifyAsync(
            IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
            throw new InvalidOperationException("the permission check is down");
    }

    /// <summary>
    /// Tools whose only gate on a synced file is <paramref name="verifier"/>: the scope resolver lets every file
    /// through, so a file shown or hidden is the verifier's decision as the tools applied it.
    /// </summary>
    private static CodeTools Tools(IServiceProvider sp, ISearchResultVerifier verifier) => new(
        sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>(),
        new UnrestrictedScopeResolver(),
        verifier,
        sp.GetRequiredService<IKnowledgeSearch>(),
        NullLogger<CodeTools>.Instance);

    /// <summary>Marks a document as synced from a source, so the verifier decides whether it is shown.</summary>
    private static async Task HideAsync(IServiceProvider sp, string document)
    {
        await using KnowledgeDbContext db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        await db.Documents.Where(d => d.Id == Guid.Parse(document))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ResourceUri, "azblob://acct/secret/" + document));
    }

    [Fact]
    public async Task FindSymbol_PartlyQualifiedName_FindsTheDefinitionWithItsLines()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        (Guid container, Dictionary<string, string> docs) = await SeedCSharpAsync(sp);

        CodeSymbolResult found = await sp.GetRequiredService<ICodeTools>()
            .FindSymbolAsync(new CodeScope(container, null), "Ledger.Post", CodeMatch.Exact, follow: false, limit: 10);

        CodeDefinition post = found.Definitions.Should().ContainSingle().Subject;
        post.Symbol.Should().Be("Acme.Billing.Ledger.Post");
        post.Kind.Should().Be("method");
        post.Language.Should().Be("csharp");
        post.Path.Should().Be("/acme/Ledger.cs");
        post.StartLine.Should().Be(7);
        post.EndLine.Should().BeGreaterThanOrEqualTo(10);
        post.DocumentId.Should().Be(docs["Ledger"]);
        found.Files.Select(f => f.Path).Should().Equal("/acme/Ledger.cs");
    }

    [Fact]
    public async Task FindSymbol_Follow_AddsTheFileDefiningWhatItCalls()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        (Guid container, _) = await SeedCSharpAsync(sp);

        CodeSymbolResult found = await sp.GetRequiredService<ICodeTools>()
            .FindSymbolAsync(new CodeScope(container, null), "Ledger.Post", CodeMatch.Exact, follow: true, limit: 10);

        found.Uses.Should().ContainSingle(u => u.Symbol == "Acme.Audit.AuditLog.Write" && u.Type == "calls");
        found.Files.Select(f => f.Path).Should().StartWith(["/acme/Ledger.cs", "/acme/AuditLog.cs"]);
    }

    [Fact]
    public async Task FindSymbol_Words_MatchesPartsOfTheQualifiedName()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        (Guid container, _) = await SeedCSharpAsync(sp);

        CodeSymbolResult found = await sp.GetRequiredService<ICodeTools>()
            .FindSymbolAsync(new CodeScope(container, null), "audit write", CodeMatch.Words, follow: false, limit: 10);

        found.Definitions.Select(d => d.Symbol).Should().Contain("Acme.Audit.AuditLog.Write");
    }

    [Fact]
    public async Task References_Calls_ListsCallersFirstThenFilesThatOnlyNameIt()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        (Guid container, _) = await SeedCSharpAsync(sp);

        CodeReferencesResult references = await sp.GetRequiredService<ICodeTools>()
            .ReferencesAsync(new CodeScope(container, null), "Ledger.Post", CodeReferenceKind.Calls, limit: 20);

        List<string> paths = [.. references.Files.Select(f => f.Path)];
        paths.Should().Contain(["/acme/Invoices.cs", "/tests/LedgerTests.cs", "/acme/Notes.cs"]);
        paths.IndexOf("/acme/Notes.cs").Should().BeGreaterThan(paths.IndexOf("/acme/Invoices.cs"), "a mention ranks below a call");
        references.Files.Single(f => f.Path == "/acme/Invoices.cs").Lines.Should().Contain(l => l.Line == 9);
    }

    [Fact]
    public async Task Impact_Method_ReachesCallersTransitivelyAndFlagsTests()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        (Guid container, _) = await SeedCSharpAsync(sp);

        CodeImpactResult impact = await sp.GetRequiredService<ICodeTools>()
            .ImpactAsync(new CodeScope(container, null), "AuditLog.Write", depth: 3, limit: 50);

        impact.Symbols.Should().Contain(s => s.Symbol == "Acme.Billing.Ledger.Post" && s.Hops == 1);
        impact.Symbols.Should().Contain(s => s.Symbol == "Acme.Billing.Invoices.Close" && s.Hops == 2);
        impact.Symbols.Should().Contain(s => s.Symbol == "Acme.Tests.LedgerTests.Post_Writes" && s.IsTest);
        impact.Tests.Should().Contain("/tests/LedgerTests.cs");
    }

    [Fact]
    public async Task RepoMap_RanksTheMostReferredToFileFirst()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        (Guid container, _) = await SeedCSharpAsync(sp);

        CodeRepoMap map = await sp.GetRequiredService<ICodeTools>().RepoMapAsync(new CodeScope(container, null), "acme", maxFiles: 10);

        map.Files.Should().NotContain(f => f.Path.StartsWith("/tests/"));
        map.Files[0].Path.Should().Be("/acme/Ledger.cs");
        map.Files[0].Symbols.Should().Contain(s => s.Symbol == "Acme.Billing.Ledger.Post" && s.ReferencedBy == 2);
    }

    [Fact]
    public async Task FindSymbol_PythonAndTypeScript_KeyedByModule()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        Guid container = await SeedContainerAsync(sp);
        await IngestAsync(sp, container, "/src/shop/util.py", "def resolve(name):\n    return name.upper()\n");
        await IngestAsync(sp, container, "/src/shop/core.py",
            "from shop.util import resolve\n\n\nclass Parameter:\n    def value_from_envvar(self, ctx):\n        return resolve(ctx)\n");
        await IngestAsync(sp, container, "/source/utils/merge.ts", "export function deepMerge(a: object, b: object) {\n  return { ...a, ...b };\n}\n");
        await IngestAsync(sp, container, "/source/core/Ky.ts",
            "import { deepMerge } from '../utils/merge';\n\nexport class Ky {\n  create(options: object) {\n    return deepMerge({}, options);\n  }\n}\n");
        ICodeTools tools = sp.GetRequiredService<ICodeTools>();

        CodeSymbolResult python = await tools.FindSymbolAsync(new CodeScope(container, null), "Parameter.value_from_envvar", CodeMatch.Exact, true, 10);
        CodeSymbolResult typescript = await tools.FindSymbolAsync(new CodeScope(container, null), "Ky.create", CodeMatch.Exact, true, 10);

        python.Definitions.Should().ContainSingle().Which.Symbol.Should().Be("shop.core.Parameter.value_from_envvar");
        python.Files.Select(f => f.Path).Should().Contain("/src/shop/util.py");
        typescript.Definitions.Should().ContainSingle().Which.Symbol.Should().Be("source/core/Ky#Ky.create");
        typescript.Files.Select(f => f.Path).Should().Contain("/source/utils/merge.ts");
    }

    [Fact]
    public async Task FindSymbol_FieldsConstantsAndTypeAliases_FindTheFileThatDeclaresThem()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        Guid container = await SeedContainerAsync(sp);
        await IngestAsync(sp, container, "/src/Converter.cs",
            "namespace Acme;\n\npublic class Converter\n{\n    static readonly HashSet<Type> BuiltInScalarTypes = new();\n\n    public bool IsScalar(Type t) => BuiltInScalarTypes.Contains(t);\n}\n");
        await IngestAsync(sp, container, "/src/Other.cs",
            "namespace Acme;\n\npublic class Other\n{\n    public int BuiltInScalarTypes { get; set; }\n}\n");
        await IngestAsync(sp, container, "/src/click/_compat.py", "import sys\n\nWIN = sys.platform.startswith(\"win\")\n");
        await IngestAsync(sp, container, "/src/click/utils.py", "from ._compat import WIN\n\n\ndef echo(message):\n    if WIN:\n        return message\n");
        await IngestAsync(sp, container, "/source/types/retry.ts", "export type RetryOptions = {\n  limit?: number;\n};\n");
        await IngestAsync(sp, container, "/source/utils/normalize.ts",
            "import type { RetryOptions } from '../types/retry';\n\nexport function normalize(options: RetryOptions) {\n  return options;\n}\n");
        ICodeTools tools = sp.GetRequiredService<ICodeTools>();
        var where = new CodeScope(container, null);

        CodeSymbolResult field = await tools.FindSymbolAsync(where, "Converter.BuiltInScalarTypes", CodeMatch.Exact, false, 10);
        CodeSymbolResult constant = await tools.FindSymbolAsync(where, "WIN", CodeMatch.Exact, false, 10);
        CodeSymbolResult alias = await tools.FindSymbolAsync(where, "RetryOptions", CodeMatch.Exact, false, 10);
        CodeReferencesResult usages = await tools.ReferencesAsync(where, "RetryOptions", CodeReferenceKind.All, 10);

        field.Definitions.Should().BeEmpty();
        field.Files.Select(f => f.Path).Should().Equal(["/src/Converter.cs"], "another type's member of the same name is not it");
        constant.Files.Select(f => f.Path).Should().Equal("/src/click/_compat.py");
        alias.Files.Select(f => f.Path).Should().Equal("/source/types/retry.ts");
        usages.Files.Select(f => f.Path).Should().Equal("/source/types/retry.ts", "/source/utils/normalize.ts");
    }

    [Fact]
    public async Task EveryTool_FileTheCallerCannotRead_IsNeverShown()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        (Guid container, Dictionary<string, string> docs) = await SeedCSharpAsync(sp);
        await HideAsync(sp, docs["Invoices"]);
        await HideAsync(sp, docs["AuditLog"]);
        CodeTools tools = Tools(sp, new RejectSynced());
        var where = new CodeScope(container, null);

        CodeSymbolResult hiddenDefinition = await tools.FindSymbolAsync(where, "AuditLog.Write", CodeMatch.Exact, true, 10);
        CodeSymbolResult followed = await tools.FindSymbolAsync(where, "Ledger.Post", CodeMatch.Exact, true, 10);
        CodeReferencesResult references = await tools.ReferencesAsync(where, "Ledger.Post", CodeReferenceKind.All, 50);
        CodeImpactResult impact = await tools.ImpactAsync(where, "Ledger.Post", 3, 50);
        CodeRepoMap map = await tools.RepoMapAsync(where, null, 50);

        string[] hidden = [docs["Invoices"], docs["AuditLog"]];
        hiddenDefinition.Definitions.Should().BeEmpty();
        hiddenDefinition.Files.Select(f => f.DocumentId).Should().NotIntersectWith(hidden);
        followed.Uses.Should().BeEmpty();
        followed.Files.Select(f => f.DocumentId).Should().NotIntersectWith(hidden);
        references.Files.Select(f => f.DocumentId).Should().NotIntersectWith(hidden).And.Contain(docs["LedgerTests"]);
        impact.Files.Select(f => f.DocumentId).Should().NotIntersectWith(hidden);
        impact.Symbols.Should().NotContain(s => s.Symbol.Contains("Invoices"));
        map.Files.Select(f => f.DocumentId).Should().NotIntersectWith(hidden);
        map.Files.SelectMany(f => f.Symbols).Single(s => s.Symbol == "Acme.Billing.Ledger.Post").ReferencedBy
            .Should().Be(1, "only the readable test calls it");
    }

    [Fact]
    public async Task EveryTool_AccessCheckFails_ReturnsDenied()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        (Guid container, Dictionary<string, string> docs) = await SeedCSharpAsync(sp);
        await HideAsync(sp, docs["Ledger"]);
        CodeTools tools = Tools(sp, new ThrowingVerifier());
        var where = new CodeScope(container, null);

        (await tools.FindSymbolAsync(where, "Ledger.Post", CodeMatch.Exact, true, 10)).Should().Be(CodeSymbolResult.DeniedResult);
        (await tools.ReferencesAsync(where, "Ledger.Post", CodeReferenceKind.All, 10)).Should().Be(CodeReferencesResult.DeniedResult);
        (await tools.ImpactAsync(where, "Ledger.Post", 2, 10)).Should().Be(CodeImpactResult.DeniedResult);
        (await tools.RepoMapAsync(where, null, 10)).Should().Be(CodeRepoMap.DeniedResult);
    }

    [Fact]
    public async Task FindSymbol_AnotherContainersCode_IsNotFound()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;
        await SeedCSharpAsync(sp);
        Guid empty = await SeedContainerAsync(sp);

        CodeSymbolResult found = await sp.GetRequiredService<ICodeTools>()
            .FindSymbolAsync(new CodeScope(empty, null), "Ledger.Post", CodeMatch.Exact, true, 10);

        found.Definitions.Should().BeEmpty();
        found.Denied.Should().BeFalse();
    }
}
