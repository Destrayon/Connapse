using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// #792's proof test: the same case run through REST, MCP and the page should leave the same
/// outcome, side effects included. Each row compares one surface with the reference surface (REST,
/// or MCP for an operation REST lacks). Rows that differ today are listed in
/// known-divergences.json with the reason: a listed row must still differ and an unlisted one must
/// agree, so fixing a divergence means deleting its entry, and a new one fails until it is fixed
/// or listed by hand.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public partial class SameOutcomeTests(SharedWebAppFixture fixture)
{
    private static readonly string[] SurfaceOrder = ["rest", "mcp", "ui"];
    private static readonly ConcurrentDictionary<string, Task<Outcome>> References = new(StringComparer.Ordinal);

    public static TheoryData<string, string, string> Rows()
    {
        var rows = new TheoryData<string, string, string>();
        foreach (var operation in Operations.All)
            foreach (string @case in operation.Cases)
                foreach (string surface in SurfaceOrder.Where(s => s != ReferenceSurface(operation)))
                    rows.Add(operation.Name, @case, surface);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Operation_SameCase_SameOutcomeOnEverySurface(string operationName, string @case, string surface)
    {
        var operation = Operations.All.Single(o => o.Name == operationName);
        string reference = ReferenceSurface(operation);
        var expected = await References.GetOrAdd($"{operationName}/{@case}", _ => RunAsync(operation, @case, reference));
        var actual = await RunAsync(operation, @case, surface);

        string key = $"{operationName}/{@case}/{surface}";
        bool agrees = actual.Equals(expected);
        if (KnownDivergences.Value.TryGetValue(key, out string? reason))
        {
            agrees.Should().BeFalse(
                $"{key} is listed as diverging ({reason}), but it now matches {reference}. Delete its entry from known-divergences.json.\n{actual}");
            // Pinned to the recorded divergence, not just any: a different mismatch on a listed row
            // (the audit gap fixed but the description now saved wrong) changes this snapshot.
            await Verify(new { Reason = reason, Reference = expected, Surface = actual })
                .UseDirectory("Snapshots/Divergences").UseParameters(operationName, @case, surface);
        }
        else
            agrees.Should().BeTrue(
                $"{key} differs from {reference}. Fix the surface, or list it in known-divergences.json with the reason.\n"
                + $"{reference}:\n{expected}\n{surface}:\n{actual}");
    }

    [Fact]
    public void KnownDivergences_NameOnlyRowsThatExist()
    {
        var rows = Rows().Select(r => $"{r[0]}/{r[1]}/{r[2]}").ToHashSet(StringComparer.Ordinal);

        KnownDivergences.Value.Keys.Where(k => !rows.Contains(k)).Should().BeEmpty("every entry must name a row the theory runs");
        KnownDivergences.Value.Values.Should().OnlyContain(r => r.Length > 0, "every entry says why");
    }

    /// <summary>REST is the reference; an operation REST lacks is compared against MCP, and REST's absence is a row of its own.</summary>
    private static string ReferenceSurface(Operation operation) => SurfaceOrder.First(s => operation.DriverFor(s) is not null);

    private async Task<Outcome> RunAsync(Operation operation, string @case, string surface)
    {
        var driver = operation.DriverFor(surface);
        if (driver is null)
            return new Outcome($"absent: {operation.AbsentReasons.GetValueOrDefault(surface, "no such action")}", [], [], []);

        var context = await Operations.ArrangeAsync(fixture, operation, @case);
        var principal = operation.PrincipalFor(@case);
        Outcome outcome;
        try
        {
            outcome = await SideEffects.CaptureAsync(fixture,
                async () => await driver(fixture, context, principal, @case) ?? "absent",
                sp => operation.Project(sp, context),
                new Dictionary<string, string> { [context.ContainerName] = "{name}" },
                knownIds: [context.ContainerId, .. context.Files.Select(f => f.Id), context.Unknown],
                responseIds: ResponseIds(operation, context));
        }
        finally
        {
            await Operations.CleanupAsync(fixture, context);
        }
        // Audit rows and ingestion jobs record which surface did the work ("Via": "MCP"). The right
        // surface's value compares equal; any other is kept as it is, so a row crediting the wrong
        // surface stays a difference.
        string Mask(string line) => Via().Replace(line, m => m.Groups["value"].Value == ViaOf[surface]
            ? $"\"{m.Groups["field"].Value}\":{m.Groups["space"].Value}\"{{via}}\""
            : m.Value);
        return outcome with
        {
            Audit = outcome.Audit.Select(Mask).ToList(),
            Jobs = outcome.Jobs.Select(Mask).ToList(),
        };
    }

    /// <summary>
    /// Ids a success may name. An upload answers with the document it made, which the projection
    /// already lists, so naming its container instead is a wrong id; the other operations may name
    /// the case's container or files.
    /// </summary>
    private static string[] ResponseIds(Operation operation, CaseContext context) =>
        operation.Name is "upload_file" or "bulk_upload"
            ? []
            : [context.ContainerId, .. context.Files.Select(f => f.Id)];

    /// <summary>What each surface records itself as.</summary>
    private static readonly IReadOnlyDictionary<string, string> ViaOf = new Dictionary<string, string>
    {
        ["rest"] = "API",
        ["mcp"] = "MCP",
        ["ui"] = "UI",
    };

    [GeneratedRegex(@"""(?<field>Via|IngestedVia)"":(?<space> ?)""(?<value>[^""]*)""")]
    private static partial Regex Via();

    private static readonly Lazy<IReadOnlyDictionary<string, string>> KnownDivergences = new(() =>
    {
        string path = Path.Combine(RepoRoot(), "tests", "Connapse.Integration.Tests", "CrossSurface", "known-divergences.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    });

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Connapse.slnx not found above the test output.");
    }
}
