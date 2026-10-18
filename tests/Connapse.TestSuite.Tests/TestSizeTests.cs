using FluentAssertions;

namespace Connapse.TestSuite.Tests;

/// <summary>
/// Proof test for epic #783, problem P10: a test with no tier, or two, escapes every CI filter or
/// runs in the wrong one. The Small tier is the fast gate, so a slow test hiding there is the
/// failure this guards against; its timing is checked against CI's results by
/// .github/scripts/test_budgets.py.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class TestSizeTests
{
    [Fact]
    public void EveryTest_HasExactlyOneCategory_AndAValidSize()
    {
        var problems = TestSources.TestFiles().SelectMany(f => TestSizeRules.Problems(f.RelativePath, f.Root)).ToList();

        Assert.True(problems.Count == 0,
            "Every test needs one Category (Unit, Integration or LiveAws) and at most one Size (Medium or Large). " +
            $"A test with no Category runs in no CI step.\n{string.Join("\n", problems)}");
    }

    [Fact]
    public void TestSources_AreFound()
    {
        // Guards the scan above against passing because it read nothing.
        TestSources.TestFiles().Select(f => f.RelativePath).Should()
            .Contain(p => p.StartsWith("tests/Connapse.Core.Tests/"))
            .And.Contain(p => p.StartsWith("tests/Connapse.Integration.Tests/"))
            .And.NotContain(p => p.StartsWith("tests/AIKnowledge."));
    }

    [Theory]
    [InlineData(new[] { "Category=Unit" }, null)]
    [InlineData(new[] { "Category=Unit", "Size=Medium" }, null)]
    [InlineData(new[] { "Category=Integration", "Size=Large" }, null)]
    [InlineData(new string[0], "needs exactly one Category trait, has 0")]
    [InlineData(new[] { "Category=Unit", "Category=Integration" }, "needs exactly one Category trait, has 2")]
    [InlineData(new[] { "Category=Unit", "Category=Unit" }, "needs exactly one Category trait, has 2")]
    [InlineData(new[] { "Category=Unit", "Size=Medium", "Size=Medium" }, "has 2 Size traits; traits add up, so keep one")]
    [InlineData(new[] { "Category=Unit", "Size=Medium", "Size=Large" }, "has 2 Size traits; traits add up, so keep one")]
    [InlineData(new[] { "Category=Unit", "Size=Small" }, "Size 'Small' isn't Medium or Large; Small is the default and takes no trait")]
    [InlineData(new[] { "Category=Integration", "Size=Medium" }, "Size=Medium only applies to unit tests; integration tests are Medium already")]
    public void Check_ReportsEachInvalidCombination(string[] traits, string? expected)
    {
        var parsed = traits.Select(t => t.Split('=')).Select(p => (p[0], p[1])).ToList();
        TestSizeRules.Check(parsed).Should().Be(expected);
    }

    [Fact]
    public void Discovery_FindsQualifiedSkippableAndDerivedTestAttributes()
    {
        const string source = """
            public sealed class LiveFactAttribute : SkippableFactAttribute { }
            public sealed class SlowTestAttribute : LiveFactAttribute { }
            public class Planted
            {
                [Xunit.Fact] public void Qualified() { }
                [SkippableFact] public void Skippable() { }
                [SlowTest] public void Derived() { }
                [Trait("Category", "Unit")][Fact] public void Tagged() { }
            }
            """;
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source).GetRoot();
        var problems = TestSizeRules.Problems("Planted.cs", root, ["Fact", "Theory", "SkippableFact", "LiveFact", "SlowTest"]).ToList();

        problems.Should().HaveCount(3).And.OnlyContain(p => p.Contains("needs exactly one Category trait, has 0"));
    }

    [Fact]
    public void LiveAws_IsAllowedOnlyForListedClasses()
    {
        TestSizeRules.CheckLiveAws("RolesAnywhereLiveAwsTests", [("Category", "LiveAws")]).Should().BeNull();
        TestSizeRules.CheckLiveAws("NewProofTests", [("Category", "LiveAws")]).Should().Contain("reserved");
    }
}
