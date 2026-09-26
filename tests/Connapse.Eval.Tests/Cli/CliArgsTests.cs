using Connapse.Eval.Cli;
using FluentAssertions;

namespace Connapse.Eval.Tests.Cli;

[Trait("Category", "Unit")]
public class CliArgsTests
{
    [Fact]
    public void Parse_CommandWithOptionsFlagsAndPositionals_SplitsThemApart()
    {
        CliArgs args = CliArgs.Parse(["compare", "runA", "runB", "--allow-dataset-mismatch", "--datasets", "a,b", "--limit-queries", "5"]);

        args.Command.Should().Be("compare");
        args.Positionals.Should().Equal("runA", "runB");
        args.Flag("allow-dataset-mismatch").Should().BeTrue();
        args.List("datasets").Should().Equal("a", "b");
        args.Int("limit-queries").Should().Be(5);
        args.Option("missing").Should().BeNull();
    }

    [Fact]
    public void Parse_NoArguments_HasEmptyCommand()
    {
        CliArgs.Parse([]).Command.Should().BeEmpty();
    }
}
