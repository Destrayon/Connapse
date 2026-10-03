using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// Guards on the settings tabs that a save could otherwise get wrong (#655). Source-scanned, like
/// <see cref="SearchSettingsTabTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class UploadSettingsTabTests
{
    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Connapse.Web", "Components", "Settings", file));
    }

    [Fact]
    public void WeakerParserIsolation_CannotBeSavedWithoutConfirming()
    {
        string source = Source("UploadSettingsTab.razor");

        source.Should().Contain("disabled=\"@(WeakensIsolation && !confirmedWeakerIsolation)\"");
        source.Should().Contain("if (WeakensIsolation && !confirmedWeakerIsolation)", "the submit handler refuses too, not only the button");
    }

    [Fact]
    public void EnumDropdowns_ShowAnUnrecognisedSavedValue()
    {
        string source = Source("UploadSettingsTab.razor");

        source.Should().Contain("(not recognised)");
        foreach (string setting in new[] { "PdfTextMode", "PdfTableMode", "ParserSandbox" })
            source.Should().Contain($"Options(Enum.GetNames<", $"{setting}'s list must include its saved value")
                .And.Contain($"localSettings.{setting}))");
    }

    [Fact]
    public void BreakpointMethodChange_ResetsTheAmountToThatMethodsDefault()
    {
        string source = Source("ChunkingSettingsTab.razor");

        source.Should().Contain("@bind-Value:after=\"OnBreakpointMethodChanged\"");
        source.Should().Contain("\"StandardDeviation\" => 3").And.Contain("\"InterQuartile\" => 1.5").And.Contain("_ => 95");
    }
}
