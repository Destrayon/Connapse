using Connapse.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The filesystem root allowlist, and proof that it actually loads from configuration.
/// <para>
/// The binding test is the point of this file. This allowlist is only reachable through
/// configuration binding, so if binding silently produced an empty list the control would be
/// off and every deployment would take the permissive grace path without anyone noticing —
/// the failure is invisible precisely because "no roots configured" is a legitimate state.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class SourceSecuritySettingsTests
{
    private static SourceSecuritySettings Bind(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v =>
                new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

        return configuration.GetSection(SourceSecuritySettings.SectionName)
            .Get<SourceSecuritySettings>() ?? new SourceSecuritySettings();
    }

    [Fact]
    public void AllowedFilesystemRoots_BindsFromConfiguration()
    {
        var settings = Bind(
            ("Sources:Security:AllowedFilesystemRoots:0", "/data/docs"),
            ("Sources:Security:AllowedFilesystemRoots:1", "/mnt/share"));

        settings.AllowedFilesystemRoots.Should().BeEquivalentTo(["/data/docs", "/mnt/share"]);
    }

    [Fact]
    public void AllowedFilesystemRoots_AbsentFromConfiguration_IsEmpty()
    {
        Bind().AllowedFilesystemRoots.Should().BeEmpty();
    }

    [Fact]
    public void EvaluateRoot_WithNoAllowlist_IsUnrestricted()
    {
        new SourceSecuritySettings().EvaluateRoot("/anything")
            .Should().Be(FilesystemRootDecision.UnrestrictedByConfiguration);
    }

    [Fact]
    public void EvaluateRoot_RootOutsideEveryEntry_IsDenied()
    {
        var settings = new SourceSecuritySettings
        {
            AllowedFilesystemRoots = [Path.Combine(Path.GetTempPath(), "permitted")]
        };

        settings.EvaluateRoot(Path.Combine(Path.GetTempPath(), "elsewhere"))
            .Should().Be(FilesystemRootDecision.Denied);
    }

    [Fact]
    public void EvaluateRoot_RootBeneathAnEntry_IsAllowed()
    {
        string permitted = Path.Combine(Path.GetTempPath(), $"connapse-eval-{Guid.NewGuid():N}");
        string nested = Path.Combine(permitted, "team");
        Directory.CreateDirectory(nested);

        try
        {
            var settings = new SourceSecuritySettings { AllowedFilesystemRoots = [permitted] };

            settings.EvaluateRoot(nested).Should().Be(FilesystemRootDecision.Allowed);
        }
        finally
        {
            Directory.Delete(permitted, recursive: true);
        }
    }

    [Fact]
    public void EvaluateRoot_AllEntriesBlank_IsDenied()
    {
        // Matches StorageLocationPolicy: a malformed allowlist is not an absent one, so it
        // must not fall through to the permissive path.
        var settings = new SourceSecuritySettings { AllowedFilesystemRoots = ["", "   "] };

        settings.EvaluateRoot("/anything").Should().Be(FilesystemRootDecision.Denied);
    }
}
