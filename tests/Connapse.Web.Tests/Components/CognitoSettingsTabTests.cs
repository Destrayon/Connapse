using System.Reflection;
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The manual route to a configured Cognito pool must actually reach one.
/// </summary>
/// <remarks>
/// This has been wrong twice. <c>ApplicationArn</c> and then <c>IdentityProvider</c> were each added
/// to <see cref="CognitoSettings.IsConfigured"/> without a field to enter them, so filling the form
/// in produced a pool that stayed unconfigured with nowhere to fix it — and the guided script was
/// the only way through. Both times an administrator found it rather than a test.
/// <para>
/// Source-scanned rather than rendered: Blazor compiles <c>@bind-Value</c> into a render tree, and
/// no reflection over the compiled component reads back which property each field is bound to. The
/// file is located from the solution marker, so this follows the repository rather than a build
/// path.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class CognitoSettingsTabTests
{
    /// <summary>
    /// Settings that are deliberately not on the form, and why.
    /// </summary>
    /// <remarks>
    /// Empty. Every value the pool needs is one an administrator can be given, so anything added
    /// here should be argued for rather than assumed — a name landing here silently is how the form
    /// loses a field again.
    /// </remarks>
    private static readonly HashSet<string> NotOnTheForm = [];

    private static string FormSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test walks up to the solution file to find the component");

        var path = Path.Combine(
            dir!.FullName, "src", "Connapse.Web", "Components", "Settings", "CognitoSettingsTab.razor");

        File.Exists(path).Should().BeTrue($"expected the form at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void EveryStringSetting_HasAFieldOnTheForm()
    {
        // Reflected rather than listed, so a field added to the model tomorrow is covered without
        // anybody remembering this test exists — which is the failure mode it is here to close.
        var settings = typeof(CognitoSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite)
            .Select(p => p.Name)
            .Where(n => !NotOnTheForm.Contains(n))
            .ToList();

        settings.Should().NotBeEmpty("reflection should find the pool's fields");

        string source = FormSource();

        settings.Should().OnlyContain(
            name => source.Contains($"localSettings.{name}", StringComparison.Ordinal),
            "every value the pool needs must be enterable by hand, or the manual route is a dead end");
    }

    [Fact]
    public void APoolFilledInEntirelyByHand_IsConfigured()
    {
        // The other half of the same guarantee: the fields being present is worth nothing if the
        // values they carry still do not add up to a usable pool.
        var byHand = new CognitoSettings();

        foreach (var property in typeof(CognitoSettings)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.PropertyType == typeof(string) && p.CanWrite))
        {
            // The two URLs are checked for scheme, not merely for content.
            property.SetValue(byHand, property.Name is "IssuerUrl" or "Domain"
                ? "https://example.aws.invalid/x"
                : "value");
        }

        byHand.IsConfigured.Should().BeTrue();
    }
}
