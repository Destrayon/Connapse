using System.Text.Json;
using Connapse.Core;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class MemoryAgentTests
{
    private static readonly Guid Space = Guid.NewGuid();
    private static readonly Guid HeadA = Guid.NewGuid(), ChainA = Guid.NewGuid(), OldA = Guid.NewGuid();
    private static readonly Guid HeadB = Guid.NewGuid(), ChainB = Guid.NewGuid();

    // f001 (Harrow release branch) has an older version; f003 does not.
    private static readonly MemoryAgentEval.Seeded Seed = new(Space,
        new Dictionary<string, Guid> { ["f001"] = HeadA, ["f003"] = HeadB },
        new Dictionary<string, Guid> { ["f001"] = OldA },
        new Dictionary<string, Guid> { ["f001"] = ChainA, ["f003"] = ChainB });

    private static MemoryFact Fact(Guid id, Guid chain, string subject, string attribute, string value, bool ended = false, bool superseded = false) =>
        new(id, chain, Space, subject, attribute, value, null, ended ? DateTimeOffset.UtcNow : null, DateTimeOffset.UtcNow,
            superseded ? DateTimeOffset.UtcNow : null, null, null, new MemoryCaller(null, null, null), []);

    private static List<MemoryFact> Seeded() =>
    [
        Fact(OldA, ChainA, "Harrow", "release branch", "release/2.3", ended: true),
        Fact(HeadA, ChainA, "Harrow", "release branch", "release/2.4"),
        Fact(HeadB, ChainB, "Harrow", "database", "PostgreSQL 15"),
    ];

    private static MemoryAgentEval.AgentTask Save(string kind, string? target, params string[][] expect) =>
        new("save-x", "save", kind, "test", "something", [], target, [.. expect.Select(g => (IReadOnlyList<string>)g)]);

    private static MemoryAgentEval.AgentTask Find(string kind, params string[] gold) =>
        new("find-x", "find", kind, "test", "something", gold, null, []);

    private static AgentRun Run(AgentOutcome outcome, params Guid[] cited) =>
        new(outcome, "answer", [.. cited.Select(c => c.ToString())], [], TokenUsage.Zero, 1, null);

    [Fact]
    public void ScoreSave_NewFactOnNewChain_Scores1()
    {
        List<MemoryFact> after = [.. Seeded(), Fact(Guid.NewGuid(), Guid.NewGuid(), "Lantern", "search cache", "Redis, 60 seconds")];

        MemoryAgentEval.ScoreSave(Save("new", null, ["lantern"], ["redis"], ["60"]), Run(AgentOutcome.Answered), Seed, after).Score.Should().Be(1);
    }

    [Fact]
    public void ScoreSave_NewFactThatAlsoEndsASeededFact_Scores0()
    {
        List<MemoryFact> after =
        [
            Fact(OldA, ChainA, "Harrow", "release branch", "release/2.3", ended: true),
            Fact(HeadA, ChainA, "Harrow", "release branch", "release/2.4", ended: true),
            Fact(HeadB, ChainB, "Harrow", "database", "PostgreSQL 15"),
            Fact(Guid.NewGuid(), Guid.NewGuid(), "Lantern", "search cache", "Redis, 60 seconds"),
        ];

        MemoryAgentEval.TaskScore score = MemoryAgentEval.ScoreSave(Save("new", null, ["redis"]), Run(AgentOutcome.Answered), Seed, after);

        score.Score.Should().Be(0);
        score.Note.Should().Contain("f001");
    }

    [Fact]
    public void ScoreSave_UpdateCorrectedOnTheSameChain_Scores1()
    {
        List<MemoryFact> after =
        [
            Fact(OldA, ChainA, "Harrow", "release branch", "release/2.3", ended: true),
            Fact(HeadA, ChainA, "Harrow", "release branch", "release/2.4", ended: true),
            Fact(Guid.NewGuid(), ChainA, "Harrow", "release branch", "release/2.5"),
            Fact(HeadB, ChainB, "Harrow", "database", "PostgreSQL 15"),
        ];

        MemoryAgentEval.TaskScore score = MemoryAgentEval.ScoreSave(Save("update", "f001", ["2.5"]), Run(AgentOutcome.Answered), Seed, after);

        score.Score.Should().Be(1);
        score.Measures["sameChain"].Should().Be(1);
    }

    [Fact]
    public void ScoreSave_UpdateSavedBesideAStillLiveOldValue_Scores0()
    {
        List<MemoryFact> after = [.. Seeded(), Fact(Guid.NewGuid(), Guid.NewGuid(), "Harrow", "release branch", "release/2.5")];

        MemoryAgentEval.TaskScore score = MemoryAgentEval.ScoreSave(Save("update", "f001", ["2.5"]), Run(AgentOutcome.Answered), Seed, after);

        score.Score.Should().Be(0);
        score.Note.Should().Be("target unchanged");
    }

    [Fact]
    public void ScoreSave_DuplicateLeftAlone_Scores1()
    {
        MemoryAgentEval.ScoreSave(Save("duplicate", "f003", ["15"]), Run(AgentOutcome.Answered), Seed, Seeded()).Score.Should().Be(1);
    }

    [Fact]
    public void ScoreSave_DuplicateSavedAgain_Scores0()
    {
        List<MemoryFact> after = [.. Seeded(), Fact(Guid.NewGuid(), Guid.NewGuid(), "Harrow", "database", "Postgres 15")];

        MemoryAgentEval.ScoreSave(Save("duplicate", "f003", ["15"]), Run(AgentOutcome.Answered), Seed, after).Score.Should().Be(0);
    }

    [Fact]
    public void ScoreSave_ErrorRun_Scores0()
    {
        List<MemoryFact> after = [.. Seeded(), Fact(Guid.NewGuid(), Guid.NewGuid(), "Lantern", "search cache", "Redis")];

        MemoryAgentEval.ScoreSave(Save("new", null, ["redis"]), Run(AgentOutcome.Error), Seed, after).Score.Should().Be(0);
    }

    [Fact]
    public void ScoreFind_CitesCurrentGold_Scores1()
    {
        MemoryAgentEval.ScoreFind(Find("reworded", "f001"), Run(AgentOutcome.Answered, HeadA), Seed).Score.Should().Be(1);
    }

    [Fact]
    public void ScoreFind_AlsoCitesTheOldVersion_Scores0()
    {
        MemoryAgentEval.TaskScore score = MemoryAgentEval.ScoreFind(Find("reworded", "f001"), Run(AgentOutcome.Answered, HeadA, OldA), Seed);

        score.Score.Should().Be(0);
        score.Measures["citedOld"].Should().Be(1);
    }

    [Fact]
    public void ScoreFind_AbsentAnsweredWithNoCitation_Scores1_AndCitingAnything_Scores0()
    {
        MemoryAgentEval.ScoreFind(Find("absent"), Run(AgentOutcome.Answered), Seed).Score.Should().Be(1);
        MemoryAgentEval.ScoreFind(Find("absent"), Run(AgentOutcome.Answered, HeadB), Seed).Score.Should().Be(0);
    }

    [Fact]
    public void ReadAnswer_ReadsFactIds()
    {
        Dictionary<string, JsonElement> input = JsonSerializer.SerializeToElement(new { answer = "release/2.4", factIds = new[] { HeadA.ToString() } })
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        AgentContract.ReadAnswer(input).Cited.Should().Equal(HeadA.ToString());
    }

    [Fact]
    public void Percentile_IsNearestRank()
    {
        MemoryAgentEval.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.95).Should().Be(10);
        MemoryAgentEval.Percentile([1, 2, 3, 4], 0.5).Should().Be(2);
    }

    [Theory]
    [InlineData(MemoryAgentEval.DefaultDataset, "")]
    [InlineData(MemoryAgentEval.SealedDataset, ".gz")]
    public void Dataset_HasTheRegisteredTasksAndEveryKeyResolves(string dataset, string suffix)
    {
        string root = Path.Combine(RepoRoot(), "eval", "datasets", dataset);
        List<MemoryAgentEval.SeedFact> facts = MemoryAgentEval.ReadFacts(Path.Combine(root, "facts.jsonl" + suffix));
        List<MemoryAgentEval.AgentTask> tasks = MemoryAgentEval.ReadTasks(Path.Combine(root, "tasks.jsonl" + suffix));
        HashSet<string> keys = [.. facts.Select(f => f.Key)];
        MemoryAgentEval.DatasetProfile profile = MemoryAgentEval.Profiles[dataset];

        tasks.Count(t => t.Split == "test" && t.Type == "find").Should().Be(profile.FindTasks);
        tasks.Count(t => t.Split == "test" && t.Type == "save").Should().Be(profile.SaveTasks);
        tasks.Select(t => t.Kind).Distinct().Should().HaveCount(8, "every find and save kind is present");
        facts.Select(f => f.Key).Should().OnlyHaveUniqueItems();
        tasks.SelectMany(t => t.Gold).Should().OnlyContain(k => keys.Contains(k));
        tasks.Where(t => t.Target is not null).Should().OnlyContain(t => keys.Contains(t.Target!));
        tasks.Where(t => t.Kind == "absent").Should().OnlyContain(t => t.Gold.Count == 0);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eval", "MANIFEST.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
