using System.Text;
using Connapse.Ingestion.Parsers.Code;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class CodeParserTests
{
    private readonly CodeParser _parser = new();

    private async Task<(Connapse.Core.Interfaces.ParsedDocument Parsed, CodeOutline? Outline)> ParseAsync(string source, string fileName)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var parsed = await _parser.ParseAsync(stream, fileName);
        return (parsed, CodeOutline.From(parsed.Metadata));
    }

    private static CodeSymbol Symbol(CodeOutline outline, string qualifiedName) =>
        outline.Symbols.Single(s => s.QualifiedName == qualifiedName);

    [Fact]
    public async Task ParseAsync_CSharp_FindsTypesMembersAndTheirLines()
    {
        const string source = """
            using System.Text;
            using Store = Connapse.Storage.Store;

            namespace Acme.Billing;

            /// <summary>Bills.</summary>
            [Serializable]
            public sealed class Invoice(IClock clock) : EntityBase, IPayable
            {
                private readonly ITaxTable _taxes = new TaxTable();

                public decimal Total { get; set; }

                public decimal Pay(Ledger ledger)
                {
                    var receipt = new Receipt();
                    ledger.Post(receipt);
                    _taxes.Apply(this);
                    Validate();
                    return clock.Now().Ticks;
                }

                private void Validate() => _ = Total;

                public class Line { }
            }
            """;

        var (parsed, outline) = await ParseAsync(source, "Invoice.cs");

        parsed.Content.Should().Be(source);
        parsed.Warnings.Should().BeEmpty();
        outline.Should().NotBeNull();
        outline!.Language.Should().Be("csharp");
        outline.Imports.Select(i => (i.Target, i.Alias)).Should().Equal(("System.Text", null), ("Connapse.Storage.Store", "Store"));

        var invoice = Symbol(outline, "Acme.Billing.Invoice");
        invoice.Kind.Should().Be(CodeKinds.Class);
        invoice.StartLine.Should().Be(6, "the doc comment above the class is part of it");
        invoice.EndLine.Should().Be(26);
        invoice.Bases.Should().Equal("EntityBase", "IPayable");
        invoice.Variables.Should().Contain([new CodeVariable("clock", "IClock"), new CodeVariable("_taxes", "ITaxTable"), new CodeVariable("Total", "decimal")]);

        var pay = Symbol(outline, "Acme.Billing.Invoice.Pay");
        pay.Kind.Should().Be(CodeKinds.Method);
        (pay.StartLine, pay.EndLine).Should().Be((14, 21));
        (pay.ElideStart, pay.ElideEnd).Should().Be((16, 20), "the body's inside, between its braces");
        pay.Signature.Should().Be("public decimal Pay(Ledger ledger)");
        pay.Variables.Should().Contain([new CodeVariable("ledger", "Ledger"), new CodeVariable("receipt", "Receipt")]);
        pay.Calls.Select(c => (c.Name, c.Receiver, c.IsNew)).Should().Equal(
            ("Receipt", null, true), ("Post", "ledger", false), ("Apply", "_taxes", false), ("Validate", null, false),
            ("Now", "clock", false));

        Symbol(outline, "Acme.Billing.Invoice.Total").Kind.Should().Be(CodeKinds.Property);
        Symbol(outline, "Acme.Billing.Invoice.Validate").ElideStart.Should().Be(0, "an expression body has nothing to leave out");
        Symbol(outline, "Acme.Billing.Invoice.Line").Parent.Should().Be(outline.Symbols.ToList().IndexOf(invoice));
        Symbol(outline, "Acme.Billing.Invoice").Calls.Should().ContainSingle(c => c.Name == "TaxTable" && c.IsNew,
            "a field initialiser runs in the type");
    }

    [Fact]
    public async Task ParseAsync_CSharpBlockNamespacesAndConditionalCalls_AreQualified()
    {
        const string source = """
            namespace A
            {
                namespace B
                {
                    interface IRun { void Run(); }
                    record Job(string Name) : Base(Name)
                    {
                        void Go(IRun? r) { r?.Run(); System.Console.WriteLine(Name); }
                    }
                }
            }
            """;

        var (_, outline) = await ParseAsync(source, "Job.cs");

        Symbol(outline!, "A.B.IRun").Kind.Should().Be(CodeKinds.Interface);
        Symbol(outline!, "A.B.IRun.Run").Kind.Should().Be(CodeKinds.Method);
        Symbol(outline!, "A.B.Job").Bases.Should().Equal("Base");
        Symbol(outline!, "A.B.Job.Go").Calls.Select(c => (c.Name, c.Receiver)).Should().Equal(("Run", "r"), ("WriteLine", "System.Console"));
    }

    [Fact]
    public async Task ParseAsync_TypeScript_FindsClassesFunctionsImportsAndCalls()
    {
        const string source = """
            import defaultThing, { fetchAll, send as post } from './http';
            import * as util from "../util";

            // Makes requests.
            export class Client extends Base<Options> implements Closer {
              private retries: RetryPolicy;

              constructor(private store: Store) {
                super();
              }

              async get(url: string, policy: RetryPolicy): Promise<Response> {
                const timer = new Timer();
                this.store.read(url);
                return post(util.join(url), policy);
              }
            }

            export const makeClient = (store: Store) => {
              return new Client(store);
            };

            function helper() { fetchAll(); }
            """;

        var (_, outline) = await ParseAsync(source, "client.ts");

        outline!.Language.Should().Be("typescript");
        outline.Imports.Should().HaveCount(2);
        outline.Imports[0].Target.Should().Be("./http");
        outline.Imports[0].Names.Should().Equal("default as defaultThing", "fetchAll", "send as post");
        outline.Imports[1].Should().BeEquivalentTo(new { Target = "../util", Alias = "util" });

        var client = Symbol(outline, "Client");
        (client.StartLine, client.EndLine).Should().Be((4, 17), "the comment above the export is part of it");
        client.Bases.Should().Equal("Base", "Closer");
        client.Variables.Should().Contain([new CodeVariable("retries", "RetryPolicy"), new CodeVariable("store", "Store")]);

        Symbol(outline, "Client.constructor").Kind.Should().Be(CodeKinds.Constructor);
        var get = Symbol(outline, "Client.get");
        get.Calls.Select(c => (c.Name, c.Receiver, c.IsNew)).Should().Equal(
            ("Timer", null, true), ("read", "this.store", false), ("post", null, false), ("join", "util", false));
        get.Variables.Should().Contain(new CodeVariable("timer", "Timer"));

        var makeClient = Symbol(outline, "makeClient");
        makeClient.Kind.Should().Be(CodeKinds.Function);
        (makeClient.StartLine, makeClient.EndLine).Should().Be((19, 21));
        makeClient.Calls.Should().ContainSingle(c => c.Name == "Client" && c.IsNew);

        Symbol(outline, "helper").Calls.Should().ContainSingle(c => c.Name == "fetchAll");
    }

    [Fact]
    public async Task ParseAsync_Tsx_ReadsJsx()
    {
        const string source = "export function App(props: Props) { return <div>{render(props)}</div>; }\n";

        var (_, outline) = await ParseAsync(source, "App.tsx");

        outline!.HasErrors.Should().BeFalse();
        Symbol(outline, "App").Calls.Should().ContainSingle(c => c.Name == "render");
    }

    [Fact]
    public async Task ParseAsync_Python_FindsClassesFunctionsImportsAndCalls()
    {
        const string source = """
            import os.path as osp
            from .core import Command, Group as G
            from typing import *

            # A command line.
            @dataclass
            class Cli(G, base.Mixin):
                def __init__(self, store: Store):
                    self.store = Store()
                    self.parser = make_parser()

                def run(self, args=None):
                    ctx = Context(self)
                    self.store.save(ctx)
                    return main(osp.join(args))

            def main(argv: list[str]) -> int:
                Cli(None).run(argv)
                return 0

            main([])
            """;

        var (parsed, outline) = await ParseAsync(source, "cli.py");

        parsed.Warnings.Should().BeEmpty();
        outline!.Imports.Select(i => (i.Target, string.Join(",", i.Names), i.Alias)).Should().Equal(
            ("os.path", "", "osp"), (".core", "Command,Group as G", null), ("typing", "*", null));

        var cli = Symbol(outline, "Cli");
        (cli.StartLine, cli.EndLine).Should().Be((5, 15), "the comment and decorator above the class are part of it");
        cli.Bases.Should().Equal("G", "base.Mixin");
        cli.Variables.Should().Contain(new CodeVariable("store", "Store"));

        Symbol(outline, "Cli.__init__").Kind.Should().Be(CodeKinds.Constructor);
        var run = Symbol(outline, "Cli.run");
        run.Kind.Should().Be(CodeKinds.Method);
        (run.ElideStart, run.ElideEnd).Should().Be((13, 15), "an indented body is left out whole");
        run.Calls.Select(c => (c.Name, c.Receiver)).Should().Equal(
            ("Context", null), ("save", "self.store"), ("main", null), ("join", "osp"));
        run.Variables.Should().Contain(new CodeVariable("ctx", "Context"));

        Symbol(outline, "main").Variables.Should().Contain(new CodeVariable("argv", "list"));
        Symbol(outline, "main").Calls.Select(c => (c.Name, c.Receiver)).Should().Equal(("run", "?"), ("Cli", null)); // an outer call comes before the calls in its receiver
        outline.ModuleCalls.Should().ContainSingle(c => c.Name == "main" && c.Line == 21);
    }

    [Fact]
    public async Task ParseAsync_NestedFunctions_StayInsideTheirParent()
    {
        const string source = """
            def outer():
                def inner():
                    helper()
                return inner
            """;

        var (_, outline) = await ParseAsync(source, "nest.py");

        outline!.Symbols.Should().ContainSingle(s => s.Name == "outer");
        Symbol(outline, "outer").Calls.Should().ContainSingle(c => c.Name == "helper");
    }

    [Fact]
    public async Task ParseAsync_SyntaxErrors_KeepTheDefinitionsAroundThemAndWarn()
    {
        const string source = """
            class Good { void A() { } }
            class Broken { void B( { }
            """;

        var (parsed, outline) = await ParseAsync(source, "broken.cs");

        outline!.HasErrors.Should().BeTrue();
        outline.Symbols.Should().Contain(s => s.QualifiedName == "Good.A");
        parsed.Warnings.Should().ContainSingle().Which.Should().Contain("syntax errors");
    }

    [Fact]
    public async Task ParseAsync_DeeplyNestedInput_DoesNotOverflowTheStack()
    {
        string source = "class C { int M() { return " + string.Concat(Enumerable.Repeat("(\n", 20_000)) + "1" + new string(')', 20_000) + "; } }";

        var (_, outline) = await ParseAsync(source, "deep.cs");

        outline!.Symbols.Should().Contain(s => s.QualifiedName == "C.M");
    }

    [Fact]
    public async Task ParseAsync_MinifiedFile_IsIndexedAsTextWithoutAnOutline()
    {
        string source = string.Join(";", Enumerable.Repeat("var a=f(b,c)", 400));

        var (parsed, outline) = await ParseAsync(source, "bundle.min.ts");

        parsed.Content.Should().Be(source);
        outline.Should().BeNull();
        parsed.Warnings.Should().ContainSingle().Which.Should().Contain("minified");
    }

    [Fact]
    public async Task ParseAsync_EmptyFile_WarnsAndReturnsNoText()
    {
        var (parsed, outline) = await ParseAsync("  \n", "empty.py");

        parsed.Content.Should().BeEmpty();
        outline.Should().BeNull();
        parsed.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void SupportedExtensions_AreTheThreeLanguages()
    {
        _parser.SupportedExtensions.Should().BeEquivalentTo([".cs", ".ts", ".mts", ".cts", ".tsx", ".py", ".pyi"]);
    }
}
