using FluentAssertions;

namespace Connapse.Web.Tests;

[Trait("Category", "Unit")]
public class ComposeFilesTests
{
    private static readonly string Root = Components.PageTestPaths.RepositoryRoot();

    private static string[] SettingLines(string relativePath) =>
        File.ReadAllLines(Path.Combine(Root, relativePath))
            .Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))
            .ToArray();

    [Fact]
    public void ComposeFiles_ApartFromTheSourceBuildAndAwsFolder_AreTheSame()
    {
        string[] root = SettingLines("docker-compose.yml")
            .Where(line => line.Trim() != "build: ." && !line.Contains("/home/app/.aws"))
            .ToArray();

        root.Should().Equal(SettingLines("release/docker-compose.yml"));
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("release/docker-compose.yml")]
    public void ComposeFile_NoSharedPasswordAndOnlyTheWebPortPublished(string file)
    {
        string[] lines = SettingLines(file);

        // Only the upgrade path may name the old defaults: the setup script keeps them for data that already uses them.
        string[] fromUpgradePath = lines
            .Where(line => line.Contains("=connapse_dev") || line.Contains(":-connapse_dev"))
            .ToArray();
        fromUpgradePath.Should().HaveCount(3).And.OnlyContain(line => line.Contains("old_"));
        lines.Count(line => line.Trim() == "ports:").Should().Be(1);
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("release/docker-compose.yml")]
    public void ComposeFile_NewVolumeSeededByTheMinioImage_IsNotTakenForOldData(string file)
    {
        // The image's own .minio.sys folder lands in a new volume, which once gave every new install the old defaults.
        string script = string.Join('\n', SettingLines(file));

        script.Should().Contain("[ -f /minio-data/.minio.sys/format.json ]");
        script.Should().NotContain("[ -d /minio-data/.minio.sys ]");
    }

    [Fact]
    public void DevOverlay_PublishesToThisMachineOnly()
    {
        string[] published = SettingLines("docker-compose.dev.yml")
            .Where(line => line.TrimStart().StartsWith("- \"", StringComparison.Ordinal))
            .ToArray();

        published.Should().NotBeEmpty().And.OnlyContain(line => line.Contains("\"127.0.0.1:"));
    }
}
