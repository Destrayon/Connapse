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

    [Theory]
    [InlineData("code", "--help")]
    [InlineData("code", "-h")]
    [InlineData("memory", "-?")]
    [InlineData("code", "--config", "-h")]
    [InlineData("--help")]
    [InlineData("help")]
    public void Parse_HelpAnywhere_WantsHelp(params string[] argv)
    {
        CliArgs.Parse(argv).WantsHelp.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoHelpToken_DoesNotWantHelp()
    {
        CliArgs.Parse(["code", "--config", "code"]).WantsHelp.Should().BeFalse();
    }

    [Fact]
    public void EnsureKnownOptions_CodeWithUnknownOption_Throws()
    {
        Action act = CliArgs.Parse(["code", "--splt", "tuning"]).EnsureKnownOptions;

        act.Should().Throw<ArgumentException>().WithMessage("*--splt*");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("extract")]
    [InlineData("pool")]
    [InlineData("datasets")]
    [InlineData("compare")]
    [InlineData("vector-index")]
    [InlineData("report")]
    [InlineData("memory")]
    [InlineData("code")]
    public void AllowedOptions_EveryCommand_HasAnEntry(string command)
    {
        // A command missing here skips the option check, so a typo or --help is ignored and the command runs.
        CliArgs.AllowedOptions.Should().ContainKey(command);
    }
}
