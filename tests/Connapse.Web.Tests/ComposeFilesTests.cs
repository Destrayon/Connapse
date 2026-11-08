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

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("release/docker-compose.yml")]
    public void ComposeFile_LongRunningServices_ComeBackAfterAReboot(string file)
    {
        // Without a restart policy, the one-command install stayed down after the host rebooted (CON-276).
        Dictionary<string, string[]> services = Services(file);

        foreach (string name in new[] { "postgres", "minio", "ollama", "reranker", "web" })
        {
            services[name].Should().Contain(line => line.Trim() == "restart: unless-stopped", $"{name} must restart after a reboot");
        }

        services["setup"].Should().Contain(line => line.Trim() == "restart: \"no\"", "setup runs once per start and exits");
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("release/docker-compose.yml")]
    public void ComposeFile_ThirdPartyImages_ArePinned(string file)
    {
        // web's tag is CONNAPSE_VERSION, which the release workflow pins to the release.
        string[] images = SettingLines(file)
            .Where(line => line.TrimStart().StartsWith("image:", StringComparison.Ordinal))
            .Select(line => line.Trim()["image:".Length..].Trim())
            .Where(image => !image.Contains("CONNAPSE_VERSION"))
            .ToArray();

        images.Should().NotBeEmpty();
        foreach (string image in images)
        {
            string name = image.Split('@')[0];
            string lastPart = name[(name.LastIndexOf('/') + 1)..];
            bool hasDigest = image.Contains("@sha256:");
            bool hasVersionTag = lastPart.Contains(':') && !lastPart.EndsWith(":latest", StringComparison.Ordinal);

            (hasDigest || hasVersionTag).Should().BeTrue($"{image} must name a version or a digest, not float on latest");
        }
    }

    private static Dictionary<string, string[]> Services(string relativePath)
    {
        string[] lines = SettingLines(relativePath)
            .SkipWhile(line => line != "services:")
            .Skip(1)
            .TakeWhile(line => line.StartsWith(' '))
            .ToArray();
        Dictionary<string, string[]> services = [];
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("  ", StringComparison.Ordinal) && !lines[i].StartsWith("   ", StringComparison.Ordinal))
            {
                string name = lines[i].Trim().TrimEnd(':');
                services[name] = lines.Skip(i + 1).TakeWhile(line => line.StartsWith("   ", StringComparison.Ordinal)).ToArray();
            }
        }

        return services;
    }

    [Fact]
    public void DevOverlay_FixedPasswords_OnlyFillAnEmptyVolume()
    {
        // POSTGRES_PASSWORD and the MinIO pair overwrite a saved secret; the overlay once set them and broke a running stack.
        string[] setupEnvironment = SettingLines("docker-compose.dev.yml")
            .SkipWhile(line => line.Trim() != "setup:")
            .Skip(1)
            .TakeWhile(line => line.StartsWith("    ", StringComparison.Ordinal))
            .ToArray();
        string baseScript = string.Join('\n', SettingLines("docker-compose.yml"));

        setupEnvironment.Should().NotContain(line => line.Contains("POSTGRES_PASSWORD:") && !line.Contains("DEV_"));
        setupEnvironment.Should().NotContain(line => line.Contains("MINIO_ROOT_") && !line.Contains("DEV_"));
        foreach (string name in new[] { "DEV_POSTGRES_PASSWORD", "DEV_MINIO_ROOT_USER", "DEV_MINIO_ROOT_PASSWORD" })
        {
            setupEnvironment.Should().Contain(line => line.Contains(name + ":"));
            baseScript.Should().Contain("\"$${" + name + ":-}\"");
        }
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
