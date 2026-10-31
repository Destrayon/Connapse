using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests.Links;

/// <summary>Builds the reader under test over a world, asking <paramref name="access"/> who may read what.</summary>
public delegate Task<ILinkReader> LinkReaderFactory(LinkWorld world, IDocumentAccess access);

/// <summary>
/// The permission-leak contract (#841) every <see cref="ILinkReader"/> must pass, written before the link
/// layer exists. Each case returns the rule's violations instead of asserting, so the canaries can show that
/// a reader with a planted leak fails it.
/// </summary>
public static class LinkContractCases
{
    public const int GeneratedWorlds = 500;
    public const int QueriesPerWorld = 20;

    /// <summary>10,000 walks over seeded random worlds, each checked against the rule.</summary>
    public static async Task<List<string>> GeneratedAsync(LinkReaderFactory factory, int worlds = GeneratedWorlds)
    {
        List<string> violations = [];
        int withAnswers = 0, withHidden = 0, total = 0;
        for (int seed = 1; seed <= worlds; seed++)
        {
            (LinkWorld world, DocumentAccessStub access) = LinkWorld.Generate(seed);
            ILinkReader reader = await factory(world, access);
            Random rng = new(seed * 7919);
            for (int q = 0; q < QueriesPerWorld; q++)
            {
                LinkExpandRequest request = RandomRequest(world, rng);
                IReadOnlySet<string> readable = access.Readable(request.UserId);
                LinkExpansion answer = await reader.ExpandAsync(request);
                total++;
                if (LinkLeakChecker.Reachable(world, request, readable).Count > 0) withAnswers++;
                if (world.Documents.Any(d => !readable.Contains(d))) withHidden++;
                violations.AddRange(LinkLeakChecker.Violations(world, request, readable, answer)
                    .Select(x => $"world {seed} query {q}: {x}"));
            }
        }
        // A contract that only ever asks questions with empty answers, or with nothing hidden, proves nothing.
        if (withAnswers < total / 2 || withHidden < total / 2)
            violations.Add($"generated cases too weak: {withAnswers} of {total} have answers, {withHidden} hide something");
        return violations;
    }

    /// <summary>Revoking a document hides what only it supported on the very next query, through the same reader.</summary>
    public static async Task<List<string>> RevocationAsync(LinkReaderFactory factory, int worlds = 100)
    {
        List<string> violations = [];
        int revoked = 0;
        for (int seed = 1; seed <= worlds; seed++)
        {
            (LinkWorld world, DocumentAccessStub access) = LinkWorld.Generate(10_000 + seed);
            ILinkReader reader = await factory(world, access);
            Guid? user = world.Users[0];
            LinkExpandRequest request = new(user, [.. world.Nodes.Take(5).Select(n => n.Id)], MaxHops: 2);
            LinkExpansion before = await reader.ExpandAsync(request);
            string? victim = before.Nodes.SelectMany(n => n.Sources).Concat(before.Edges.SelectMany(e => e.Sources)).FirstOrDefault();
            if (victim is null)
                continue;
            access.Revoke(user, victim);
            revoked++;
            LinkExpansion after = await reader.ExpandAsync(request);
            violations.AddRange(LinkLeakChecker.Violations(world, request, access.Readable(user), after)
                .Select(x => $"revocation world {seed} ({victim} revoked): {x}"));
        }
        if (revoked < worlds / 2)
            violations.Add($"revocation cases too weak: only {revoked} of {worlds} had something to revoke");
        return violations;
    }

    /// <summary>A permission check that throws gives a denied, empty answer, never a guess.</summary>
    public static async Task<List<string>> FailedCheckAsync(LinkReaderFactory factory, int worlds = 50)
    {
        List<string> violations = [];
        for (int seed = 1; seed <= worlds; seed++)
        {
            (LinkWorld world, DocumentAccessStub access) = LinkWorld.Generate(20_000 + seed);
            ILinkReader reader = await factory(world, access);
            LinkExpandRequest request = new(world.Users[0], [.. world.Nodes.Select(n => n.Id)], MaxHops: 3);
            await reader.ExpandAsync(request);
            access.Fail = true;
            LinkExpansion answer;
            try
            {
                answer = await reader.ExpandAsync(request);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                violations.Add($"failed check world {seed}: threw {ex.GetType().Name} instead of returning a denied answer");
                continue;
            }
            violations.AddRange(LinkLeakChecker.Violations(world, request, new HashSet<string>(), answer, accessFailed: true)
                .Select(x => $"failed check world {seed}: {x}"));
        }
        return violations;
    }

    /// <summary>The shapes a leak takes, one small world each.</summary>
    public static async Task<List<string>> NamedAsync(LinkReaderFactory factory)
    {
        List<string> violations = [];
        foreach ((string name, LinkWorld world, DocumentAccessStub access, LinkExpandRequest request, Func<LinkExpansion, string?> expect) in NamedCases())
        {
            ILinkReader reader = await factory(world, access);
            LinkExpansion answer = await reader.ExpandAsync(request);
            violations.AddRange(LinkLeakChecker.Violations(world, request, access.Readable(request.UserId), answer).Select(x => $"{name}: {x}"));
            if (expect(answer) is string miss)
                violations.Add($"{name}: {miss}");
        }
        return violations;
    }

    private static readonly Guid Reader = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    private static SourcedText T(string text, string doc) => new(text, doc);

    // t201x is titled t201x and summarised t2011x: tokens stay unique within a world.
    private static LinkNode Record(string doc, string title) => new($"n:{doc}", [T("record", doc)], [T(title, doc)], [T(title.Replace("x", "1x"), doc)]);

    private static IEnumerable<(string, LinkWorld, DocumentAccessStub, LinkExpandRequest, Func<LinkExpansion, string?>)> NamedCases()
    {
        DocumentAccessStub Access(params string[] readable)
        {
            DocumentAccessStub a = new();
            a.Grant(Reader, readable);
            return a;
        }

        // A person merged from a public page and a private one: the private spelling and summary stay out.
        LinkNode person = new("e:alex", [T("t108x", "public")],
            [T("t101x t102x", "secret"), T("t103x", "public")],
            [T("t104x t105x", "secret"), T("t106x", "public")]);
        yield return ("merged entity",
            new LinkWorld([person, Record("public", "t107x")], [new LinkEdge("l:1", "n:public", "e:alex", "mentions", [T("", "public")])], ["public", "secret"], [Reader]),
            Access("public"), new LinkExpandRequest(Reader, ["n:public"], 1),
            a => a.Nodes.SingleOrDefault(n => n.Id == "e:alex") is { Name: "t103x" } ? null : "expected Alex under the public name only");

        // Issue → private pull request → file: the file is not reached through the pull request, even though
        // both links have readable evidence (the issue and the file each name the pull request).
        yield return ("two hops through a private record",
            new LinkWorld([Record("issue", "t201x"), Record("pr", "t202x"), Record("file", "t203x")],
                [new LinkEdge("l:1", "n:pr", "n:issue", "closes", [T("", "issue")]), new LinkEdge("l:2", "n:pr", "n:file", "changes", [T("", "file")])],
                ["issue", "pr", "file"], [Reader]),
            Access("issue", "file"), new LinkExpandRequest(Reader, ["n:issue"], 2),
            a => a.Nodes.Any(n => n.Id is "n:file" or "n:pr") ? "reached the file through the private pull request" : null);

        // A fact two documents support: only the readable one's sentence and name are shown.
        yield return ("fact with one readable source",
            new LinkWorld([Record("a", "t301x"), Record("b", "t302x"), Record("memo", "t303x")],
                [new LinkEdge("l:1", "n:a", "n:b", "uses", [T("t304x t305x", "memo"), T("t306x", "a")])],
                ["a", "b", "memo"], [Reader]),
            Access("a", "b"), new LinkExpandRequest(Reader, ["n:a"], 1),
            a => a.Edges.SingleOrDefault() is { Sources: ["a"] } ? null : "expected the fact with source a only");

        // A fact only an unreadable document supports is hidden although both ends are visible.
        yield return ("fact with no readable source",
            new LinkWorld([Record("a", "t401x"), Record("b", "t402x")],
                [new LinkEdge("l:1", "n:a", "n:b", "uses", [T("t403x", "memo")])], ["a", "b", "memo"], [Reader]),
            Access("a", "b"), new LinkExpandRequest(Reader, ["n:a"], 1),
            a => a.Edges.Count == 0 && a.Nodes.Count == 1 ? null : "expected only node a");

        // What kind of thing a node is can be private too: a public name does not reveal a private kind.
        LinkNode target = new("e:acme", [T("t801x", "deal-memo"), T("t802x", "press")], [T("t803x", "press")], []);
        yield return ("kind from a private document",
            new LinkWorld([target, Record("press", "t804x")], [new LinkEdge("l:1", "n:press", "e:acme", "mentions", [T("", "press")])],
                ["press", "deal-memo"], [Reader]),
            Access("press"), new LinkExpandRequest(Reader, ["n:press"], 1),
            a => a.Nodes.SingleOrDefault(n => n.Id == "e:acme") is { Kinds: [{ Text: "t802x" }] } ? null : "expected Acme with only the public kind");

        // Starting from something the reader cannot see finds nothing.
        yield return ("unreadable seed",
            new LinkWorld([Record("secret", "t501x"), Record("open", "t502x")],
                [new LinkEdge("l:1", "n:secret", "n:open", "mentions", [T("", "open")])], ["secret", "open"], [Reader]),
            Access("open"), new LinkExpandRequest(Reader, ["n:secret"], 2),
            a => a.Nodes.Count == 0 && !a.Denied ? null : "expected an empty, not denied, answer");

        // Someone with no access at all gets an empty answer, not a denied one.
        yield return ("reader with no access",
            new LinkWorld([Record("a", "t601x")], [], ["a"], [null]),
            new DocumentAccessStub(), new LinkExpandRequest(null, ["n:a"], 1),
            a => a.Nodes.Count == 0 && !a.Denied ? null : "expected an empty, not denied, answer");

        // Link types asked for limit the walk, not just the answer.
        yield return ("link-type filter",
            new LinkWorld([Record("a", "t701x"), Record("b", "t702x"), Record("c", "t703x")],
                [new LinkEdge("l:1", "n:a", "n:b", "mentions", [T("", "a")]), new LinkEdge("l:2", "n:b", "n:c", "closes", [T("", "b")])],
                ["a", "b", "c"], [Reader]),
            Access("a", "b", "c"), new LinkExpandRequest(Reader, ["n:a"], 2, EdgeTypes: ["closes"]),
            a => a.Nodes.Count == 1 ? null : "expected only the seed: the only path starts with a mentions link");
    }

    private static LinkExpandRequest RandomRequest(LinkWorld world, Random rng)
    {
        Guid? user = world.Users[rng.Next(world.Users.Count)];
        List<string> seeds = [.. Enumerable.Range(0, rng.Next(1, 4)).Select(_ => world.Nodes[rng.Next(world.Nodes.Count)].Id).Distinct()];
        int maxNodes = rng.Next(4) == 0 ? rng.Next(1, 6) : 100;
        string[]? types = rng.Next(4) == 0 ? ["closes", "parent"] : null;
        return new LinkExpandRequest(user, seeds, rng.Next(1, 4), maxNodes, types);
    }
}

/// <summary>
/// Subclass once per <see cref="ILinkReader"/> and supply <see cref="Factory"/>; the link layer (#839 ticket 3)
/// adds a PostgreSQL-backed subclass that must pass unchanged.
/// </summary>
[Trait("Category", "Integration")]
public abstract class LinkReaderContract
{
    protected abstract LinkReaderFactory Factory { get; }

    [Fact]
    public async Task ExpandAsync_GeneratedWorlds_ShowOnlyWhatReadableSourcesSupport() =>
        (await LinkContractCases.GeneratedAsync(Factory)).Should().BeEmpty();

    [Fact]
    public async Task ExpandAsync_AfterRevocation_HidesItOnTheNextQuery() =>
        (await LinkContractCases.RevocationAsync(Factory)).Should().BeEmpty();

    [Fact]
    public async Task ExpandAsync_PermissionCheckFails_ReturnsDeniedAndNothing() =>
        (await LinkContractCases.FailedCheckAsync(Factory)).Should().BeEmpty();

    [Fact]
    public async Task ExpandAsync_NamedLeakShapes_KeepTheRule() =>
        (await LinkContractCases.NamedAsync(Factory)).Should().BeEmpty();
}
