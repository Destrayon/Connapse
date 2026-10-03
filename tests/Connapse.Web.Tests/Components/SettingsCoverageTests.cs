using System.Reflection;
using System.Text.RegularExpressions;
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// Every setting an admin can change through the settings API is on the Settings page (#655). A
/// settable property of a settings category must be bound in its tab, or listed here with the
/// reason it is not. Source-scanned, like <see cref="SearchSettingsTabTests"/>: a property counts as
/// bound when its tab binds it (<c>@bind-Value="localSettings.Name"</c>), assigns it from a bound
/// field (<c>localSettings.Name = ...</c>, as the MiB fields do), or sets it in the object a tab
/// builds on save (a line starting <c>Name = ...</c>); a mention in help text or a comment does not
/// count.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsCoverageTests
{
    private static readonly (Type Settings, string Tab)[] Tabs =
    [
        (typeof(EmbeddingSettings), "EmbeddingSettingsTab.razor"),
        (typeof(ChunkingSettings), "ChunkingSettingsTab.razor"),
        (typeof(SearchSettings), "SearchSettingsTab.razor"),
        (typeof(LlmSettings), "LlmSettingsTab.razor"),
        (typeof(UploadSettings), "UploadSettingsTab.razor"),
        (typeof(SummarySettings), "SummarySettingsTab.razor"),
        (typeof(AzureAdSignInSettings), "AzureAdSignInSettingsTab.razor"),
        (typeof(AzureProviderSettings), "AzureProviderSettingsTab.razor"),
        (typeof(IdentityCenterSettings), "IdentityCenterSettingsTab.razor"),
        (typeof(SamlSignInSettings), "SamlSignInSettingsTab.razor"),
    ];

    /// <summary>Settings deliberately not on the page, each with its reason.</summary>
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal)
    {
        // The shared key from before per-provider keys; the tab edits the provider-specific ones,
        // which take precedence.
        ["EmbeddingSettings.ApiKey"] = "legacy shared key, superseded by per-provider keys",
        ["LlmSettings.ApiKey"] = "legacy shared key, superseded by per-provider keys",
    };

    private static string TabSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test walks up to the solution file to find the components");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Connapse.Web", "Components", "Settings", file));
    }

    public static IEnumerable<object[]> Categories() => Tabs.Select(t => new object[] { t.Settings.Name });

    [Theory]
    [MemberData(nameof(Categories))]
    public void EverySettableProperty_IsOnItsTabOrExcludedWithAReason(string settingsName)
    {
        (Type type, string tab) = Tabs.Single(t => t.Settings.Name == settingsName);
        string source = TabSource(tab);

        var missing = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod!.IsPublic)
            .Select(p => p.Name)
            .Where(name => !Excluded.ContainsKey($"{type.Name}.{name}"))
            .Where(name => !Regex.IsMatch(source,
                $@"@bind-Value=""localSettings\.{name}""|localSettings\.{name}\s*=[^=]|(?m)^\s*{name}\s*=[^=]"))
            .ToList();

        missing.Should().BeEmpty(
            $"every {type.Name} setting belongs on {tab}, or in {nameof(Excluded)} with the reason it is not");
    }

    [Fact]
    public void Exclusions_NameRealProperties()
    {
        foreach (string key in Excluded.Keys)
        {
            string[] parts = key.Split('.');
            Type type = Tabs.Single(t => t.Settings.Name == parts[0]).Settings;
            type.GetProperty(parts[1]).Should().NotBeNull($"{key} is excluded, so it must still exist");
        }
    }
}
