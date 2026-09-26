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
        args.PositiveInt("limit-queries").Should().Be(5);
        args.Option("missing").Should().BeNull();
    }

    [Fact]
    public void Parse_NoArguments_HasEmptyCommand()
    {
        CliArgs.Parse([]).Command.Should().BeEmpty();
    }

    [Fact]
    public void EnsureKnownOptions_UnknownOption_ThrowsArgumentExceptionNamingIt()
    {
        CliArgs args = CliArgs.Parse(["run", "--suite", "v1", "--config", "c", "--limit-querys", "5"]);

        Action act = args.EnsureKnownOptions;

        act.Should().Throw<ArgumentException>().WithMessage("*--limit-querys*");
    }

    [Fact]
    public void EnsureKnownOptions_OptionOfAnotherCommand_Throws()
    {
        Action act = CliArgs.Parse(["report", "runA", "--out", "x"]).EnsureKnownOptions;

        act.Should().Throw<ArgumentException>().WithMessage("*--out*");
    }

    [Fact]
    public void EnsureKnownOptions_AllowedOptions_DoesNotThrow()
    {
        Action act = CliArgs.Parse(["run", "--suite", "v1", "--config", "c", "--system", "connapse", "--datasets", "a",
            "--resume", "r", "--limit-queries", "5"]).EnsureKnownOptions;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("five")]
    [InlineData("0")]
    [InlineData("-3")]
    public void PositiveInt_BadValue_ThrowsArgumentException(string value)
    {
        CliArgs args = CliArgs.Parse(["run", "--limit-queries", value]);

        Action act = () => args.PositiveInt("limit-queries");

        act.Should().Throw<ArgumentException>().WithMessage("*--limit-queries*");
    }

    [Fact]
    public void PositiveInt_OptionWithoutValue_ThrowsArgumentException()
    {
        Action act = () => CliArgs.Parse(["run", "--limit-queries"]).PositiveInt("limit-queries");

        act.Should().Throw<ArgumentException>().WithMessage("*--limit-queries*");
    }
}
