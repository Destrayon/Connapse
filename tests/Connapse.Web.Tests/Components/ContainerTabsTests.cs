using Bunit;
using Connapse.Web.Components.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>The Files | Tables | Settings tabs on a documents container's page (CON-319).</summary>
[Trait("Category", "Unit")]
public class ContainerTabsTests : IDisposable
{
    private readonly BunitContext ctx = new();

    public void Dispose() => ctx.Dispose();

    [Theory]
    [InlineData("files", "Files")]
    [InlineData("tables", "Tables")]
    [InlineData("settings", "Settings")]
    public void Render_EachTab_MarksOnlyThatTabActive(string tab, string label)
    {
        var cut = ctx.Render<ContainerTabs>(p => p.Add(x => x.Tab, tab));

        cut.FindAll(".nav-link.active").Select(b => b.TextContent).Should().Equal(label);
    }

    [Theory]
    [InlineData("#files-tab", "files")]
    [InlineData("#tables-tab", "tables")]
    public void Click_FilesOrTables_ReportsTheChosenTab(string button, string expected)
    {
        string? chosen = null;
        bool settings = false;
        var cut = ctx.Render<ContainerTabs>(p => p.Add(x => x.Tab, "settings")
            .Add(x => x.TabChanged, EventCallback.Factory.Create<string>(this, t => chosen = t))
            .Add(x => x.OnSettings, EventCallback.Factory.Create(this, () => settings = true)));

        cut.Find(button).Click();

        chosen.Should().Be(expected);
        settings.Should().BeFalse();
    }

    [Fact]
    public void Click_Settings_AsksThePageToOpenSettings()
    {
        string? chosen = null;
        bool settings = false;
        var cut = ctx.Render<ContainerTabs>(p => p
            .Add(x => x.TabChanged, EventCallback.Factory.Create<string>(this, t => chosen = t))
            .Add(x => x.OnSettings, EventCallback.Factory.Create(this, () => settings = true)));

        cut.FindAll(".nav-link")[2].Click();

        settings.Should().BeTrue();
        chosen.Should().BeNull();
    }
}
