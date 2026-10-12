using Bunit;
using Connapse.Web.Components.Shared;
using FluentAssertions;

namespace Connapse.Web.Tests.Components;

[Trait("Category", "Unit")]
public class SecretTextAreaTests : IDisposable
{
    private readonly BunitContext ctx = new();

    public void Dispose() => ctx.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RevealButton_KeepsOneNameNamingTheField_AndReportsItsStateThroughAriaPressed(bool multiline)
    {
        var cut = ctx.Render<SecretTextArea>(p => p
            .Add(s => s.Id, "client-secret")
            .Add(s => s.Label, "Client secret")
            .Add(s => s.Multiline, multiline));

        var button = cut.Find("#client-secret-reveal");
        button.GetAttribute("aria-label").Should().Be("Show client secret");
        button.GetAttribute("aria-pressed").Should().Be("false");
        button.GetAttribute("aria-controls").Should().Be("client-secret");
        button.TextContent.Trim().Should().Be("Show");

        button.Click();

        button = cut.Find("#client-secret-reveal");
        button.GetAttribute("aria-label").Should().Be("Show client secret", "a toggle's name stays the same; only its state changes");
        button.GetAttribute("aria-pressed").Should().Be("true");
        button.TextContent.Trim().Should().Be("Show");
        cut.Find("#client-secret-reveal span").ClassList.Should().Contain("bi-eye-slash");

        button.Click();
        cut.Find("#client-secret-reveal").GetAttribute("aria-pressed").Should().Be("false");
    }

    [Fact]
    public void RevealButton_Revealing_UnmasksTheField()
    {
        var cut = ctx.Render<SecretTextArea>(p => p
            .Add(s => s.Id, "key-passphrase")
            .Add(s => s.Label, "Key passphrase")
            .Add(s => s.Multiline, false));

        cut.Find("#key-passphrase").GetAttribute("type").Should().Be("password");
        cut.Find("#key-passphrase-reveal").Click();
        cut.Find("#key-passphrase").GetAttribute("type").Should().Be("text");
    }

    [Theory]
    [InlineData("Client secret", "Show client secret")]
    [InlineData("Private key (PEM)", "Show private key (PEM)")]
    [InlineData("SSH private key", "Show SSH private key")]
    [InlineData("  Certificate password ", "Show certificate password")]
    public void RevealNameFor_LowersTheFirstLetterUnlessItStartsAnAcronym(string label, string expected) =>
        SecretTextArea.RevealNameFor(label).Should().Be(expected);

    [Fact]
    public void EveryCaller_PassesTheFieldLabel()
    {
        string components = Path.Combine(RepoRoot(), "src", "Connapse.Web", "Components");
        var callers = Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Where(l => l.Contains("<SecretTextArea", StringComparison.Ordinal))
                .Select(l => (File: Path.GetFileName(f), Line: l)))
            .ToList();

        callers.Should().NotBeEmpty();
        callers.Should().OnlyContain(c => c.Line.Contains(" Label=\"", StringComparison.Ordinal),
            "the reveal button is named after the field it reveals");
    }

    private static string RepoRoot() => PageTestPaths.RepositoryRoot();
}
