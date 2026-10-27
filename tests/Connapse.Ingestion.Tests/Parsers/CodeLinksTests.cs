using System.Text;
using Connapse.Core;
using Connapse.Ingestion.Parsers.Code;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class CodeLinksTests
{
    private static readonly OwnerRef Owner = OwnerRef.ForSource(Guid.Parse("11111111-2222-3333-4444-555555555555"));
    private const string O = "s11111111222233334444555555555555";

    private static async Task<DocumentLinks> LinksAsync(string source, string path, string documentId = "doc")
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var parsed = await new CodeParser().ParseAsync(stream, path);
        return CodeLinks.Build(documentId, Owner, path, parsed.Content, CodeOutline.From(parsed.Metadata)!);
    }

    private static LinkAssertion Link(DocumentLinks links, string type, string from, string to) =>
        links.Links.Single(l => l.Type == type && l.FromId == from && l.ToId == to);

    private static string Name(DocumentLinks links, string node) =>
        links.Facts.Single(f => f.NodeId == node && f.Field == LinkFactField.Name).Text;

    private const string LedgerSource = """
        using System;
        using Acme.Audit;

        namespace Acme.Billing;

        public class Ledger(IClock clock) : LedgerBase, ILedger
        {
            private readonly Journal _journal = new();

            public void Post(Entry entry)
            {
                Validate(entry);
                _journal.Append(entry);
                AuditLog.Write(entry);
                clock.Tick();
                Reset();
            }

            private void Validate(Entry entry) { }
        }

        public class Journal
        {
            public void Append(Entry entry) { }
        }
        """;

    [Fact]
    public async Task Build_CSharp_DefinesImportsCallsAndInheritsWithConfidence()
    {
        var links = await LinksAsync(LedgerSource, "src/Billing/Ledger.cs");
        string file = $"code:{O}:file:src/Billing/Ledger.cs";
        string ns = $"code:{O}:csharp-namespace:Acme.Billing";
        string ledger = $"code:{O}:csharp:Acme.Billing.Ledger";
        string post = $"code:{O}:csharp:Acme.Billing.Ledger.Post";

        links.NodeId.Should().Be(file);
        links.AlsoStandsFor.Should().Contain([ledger, post]);
        Name(links, file).Should().Be("src/Billing/Ledger.cs");
        Name(links, post).Should().Be("Acme.Billing.Ledger.Post");
        links.Facts.Should().Contain(new LinkFact(post, LinkFactField.Summary, "public void Post(Entry entry)"));

        Link(links, CodeLinks.Defines, file, ns).Confidence.Should().Be(1.0);
        Link(links, CodeLinks.Defines, ns, ledger).Confidence.Should().Be(1.0);
        Link(links, CodeLinks.Defines, ledger, post).Confidence.Should().Be(1.0);
        Link(links, CodeLinks.Imports, file, $"code:{O}:csharp-namespace:Acme.Audit").Confidence.Should().Be(1.0);

        Link(links, CodeLinks.Calls, post, $"code:{O}:csharp:Acme.Billing.Ledger.Validate").Confidence.Should().Be(0.95);
        Link(links, CodeLinks.Calls, post, $"code:{O}:csharp:Acme.Billing.Journal.Append").Confidence
            .Should().Be(0.9, "the field's declared type is defined in this file and has the method");
        Link(links, CodeLinks.Calls, post, $"code:{O}:csharp:Acme.Audit.AuditLog.Write").Confidence
            .Should().Be(0.7, "AuditLog is found through a using");
        links.Links.Should().Contain(l => l.ToId == $"code:{O}:csharp:Acme.Billing.AuditLog.Write" && l.Confidence == 0.7,
            "the file's own namespace is a candidate too; whichever no file defines stays invisible");
        links.Links.Should().Contain(l => l.ToId.EndsWith(".IClock.Tick") && l.Confidence == 0.7, "a primary-constructor parameter's type");
        Link(links, CodeLinks.Calls, post, $"code:{O}:csharp:Acme.Billing.Ledger.Reset").Confidence
            .Should().Be(0.5, "a member this file does not show may be inherited or partial");
        links.Links.Should().Contain(l => l.ToId == $"code:{O}:csharp:Acme.Billing.LedgerBase.Reset" && l.Confidence == 0.5);

        Link(links, CodeLinks.Inherits, ledger, $"code:{O}:csharp:Acme.Billing.LedgerBase").Confidence.Should().Be(0.7);
        Link(links, CodeLinks.Calls, ledger, $"code:{O}:csharp:Acme.Billing.Journal").Confidence.Should().Be(0.95, "new() in a field initialiser");

        Link(links, CodeLinks.Calls, post, $"code:{O}:csharp:Acme.Billing.Journal.Append").Evidence
            .Should().Be("src/Billing/Ledger.cs:13 (declared type, same file) _journal.Append(entry);");
        links.Links.Select(l => l.EdgeId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Build_CallInAnotherFile_PointsAtTheNodeTheDefiningFileNames()
    {
        var defining = await LinksAsync("namespace Acme.Audit;\npublic static class AuditLog { public static void Write(object e) { } }", "Audit/AuditLog.cs", "a");
        var calling = await LinksAsync(LedgerSource, "src/Billing/Ledger.cs", "b");

        string write = $"code:{O}:csharp:Acme.Audit.AuditLog.Write";
        defining.AlsoStandsFor.Should().Contain(write);
        calling.Links.Should().Contain(l => l.Type == CodeLinks.Calls && l.ToId == write);
    }

    [Fact]
    public async Task Build_Python_ResolvesImportsSelfAndModuleNames()
    {
        const string source = """
            import os.path as osp
            from .core import Command
            from .formatting import *

            class Group(Command):
                def __init__(self):
                    self.parser = Parser()

                def invoke(self, ctx):
                    self.check()
                    self.parser.parse(ctx)
                    wrap_text(ctx)
                    return osp.join(main(), "x")

                def check(self):
                    pass

            class Parser:
                def parse(self, ctx):
                    pass

            def main():
                return Group().invoke(None)
            """;

        var links = await LinksAsync(source, "src/click/decorators.py");
        string module = $"code:{O}:python:click.decorators";
        string invoke = $"code:{O}:python:click.decorators.Group.invoke";

        links.NodeId.Should().Be(module, "a Python file is its module's node, which other files import");
        Link(links, CodeLinks.Imports, module, $"code:{O}:python:click.core").Confidence.Should().Be(1.0);
        Link(links, CodeLinks.Imports, module, $"code:{O}:python:os.path").Confidence.Should().Be(0.9);
        Link(links, CodeLinks.Inherits, $"code:{O}:python:click.decorators.Group", $"code:{O}:python:click.core.Command").Confidence.Should().Be(0.9);

        Link(links, CodeLinks.Calls, invoke, $"code:{O}:python:click.decorators.Group.check").Confidence.Should().Be(0.95);
        Link(links, CodeLinks.Calls, invoke, $"code:{O}:python:click.decorators.Parser.parse").Confidence
            .Should().Be(0.9, "self.parser was assigned a Parser, defined here");
        Link(links, CodeLinks.Calls, invoke, $"code:{O}:python:click.formatting.wrap_text").Confidence.Should().Be(0.4);
        Link(links, CodeLinks.Calls, invoke, $"code:{O}:python:os.path.join").Confidence.Should().Be(0.85);
        Link(links, CodeLinks.Calls, invoke, $"code:{O}:python:click.decorators.main").Confidence.Should().Be(0.95);
        Link(links, CodeLinks.Calls, $"code:{O}:python:click.decorators.main", $"code:{O}:python:click.decorators.Group").Confidence.Should().Be(0.95);
    }

    [Fact]
    public async Task Build_PythonPackageInit_ResolvesRelativeImportsFromThePackage()
    {
        var links = await LinksAsync("from .core import Command\n", "src/click/__init__.py");

        links.NodeId.Should().Be($"code:{O}:python:click");
        links.Links.Should().ContainSingle(l => l.Type == CodeLinks.Imports && l.ToId == $"code:{O}:python:click.core");
    }

    [Fact]
    public async Task Build_TypeScript_ResolvesRelativeImportsAndTypedReceivers()
    {
        const string source = """
            import { request, Options as Opts } from './core/request';
            import * as util from '../util';
            import express from 'express';

            export class Client {
              constructor(private store: Store) {}

              async get(url: string) {
                const opts: Opts = defaults();
                this.store.read(url);
                util.join(url);
                return request(url, opts);
              }
            }

            class Store { read(key: string) {} }

            function defaults() { return {}; }
            """;

        var links = await LinksAsync(source, "source/client.ts");
        string module = $"code:{O}:typescript:source/client";
        string get = $"code:{O}:typescript:source/client#Client.get";

        links.NodeId.Should().Be(module);
        Name(links, get).Should().Be("Client.get (source/client.ts)");
        Link(links, CodeLinks.Imports, module, $"code:{O}:typescript:source/core/request").Confidence.Should().Be(1.0);
        Link(links, CodeLinks.Imports, module, $"code:{O}:typescript:util").Confidence.Should().Be(1.0);
        links.Links.Should().NotContain(l => l.ToId.Contains("express"), "a package is outside the repository");

        Link(links, CodeLinks.Calls, get, $"code:{O}:typescript:source/client#defaults").Confidence.Should().Be(0.95);
        Link(links, CodeLinks.Calls, get, $"code:{O}:typescript:source/client#Store.read").Confidence.Should().Be(0.9);
        Link(links, CodeLinks.Calls, get, $"code:{O}:typescript:util#join").Confidence.Should().Be(0.85);
        Link(links, CodeLinks.Calls, get, $"code:{O}:typescript:source/core/request#request").Confidence.Should().Be(0.9);
    }

    [Fact]
    public async Task Build_TypeScriptIndexFile_IsItsDirectorysModule()
    {
        var links = await LinksAsync("export function ky() {}\n", "source/index.ts");

        links.NodeId.Should().Be($"code:{O}:typescript:source");
        links.AlsoStandsFor.Should().Contain($"code:{O}:typescript:source#ky");
    }

    [Fact]
    public async Task Build_UnknownReceiver_GetsNoLink()
    {
        var links = await LinksAsync("class C { void M(dynamic d) { d.Run(); Get().Run(); } }", "C.cs");

        links.Links.Should().NotContain(l => l.Type == CodeLinks.Calls && l.ToId.EndsWith(".Run"),
            "neither d's type nor what Get() returns can be told from the file");
    }

    [Fact]
    public async Task Build_SameContainerDifferentOwner_DoesNotShareNodes()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("class C { }"));
        var parsed = await new CodeParser().ParseAsync(stream, "C.cs");
        var outline = CodeOutline.From(parsed.Metadata)!;

        var a = CodeLinks.Build("d1", OwnerRef.ForContainer(Guid.NewGuid()), "C.cs", parsed.Content, outline);
        var b = CodeLinks.Build("d2", OwnerRef.ForContainer(Guid.NewGuid()), "C.cs", parsed.Content, outline);

        a.AlsoStandsFor.Should().NotIntersectWith(b.AlsoStandsFor!);
    }
}
