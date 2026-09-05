using System.Text.Json.Nodes;
using System.Text.Json;
using Connapse.Core;
using Connapse.Web.Components.Settings;
using FluentAssertions;

namespace Connapse.Core.Tests.Sources;

/// <summary>
/// The scope keys this form writes must be the ones <c>ConnectorFactory.Create(Source,
/// Connection)</c> reads. Nothing enforces that at compile time — a mistyped key produces a
/// source that is created successfully and then syncs nothing at all, which is the failure this
/// file exists to catch.
/// </summary>
[Trait("Category", "Unit")]
public class SourceFormTests
{
    private static SourceForm Filled() => new()
    {
        Name = "company-docs",
        ConnectionId = Guid.NewGuid(),
        Container = "company-knowledge",
        Prefix = "docs/",
        SubPath = "team-a",
        IncludePatterns = "*.pdf\n*.md",
        ExcludePatterns = "*.tmp",
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ToScopeJson_S3_WritesBucketNameNotContainerName()
    {
        var scope = Parse(Filled().ToScopeJson(ConnectionProvider.S3));

        scope.GetProperty("bucketName").GetString().Should().Be("company-knowledge");
        scope.GetProperty("prefix").GetString().Should().Be("docs/");

        // The Azure key must not leak into an S3 scope: the connector reads one or the other,
        // so a stray key is silently ignored rather than rejected.
        scope.TryGetProperty("containerName", out _).Should().BeFalse();
        scope.TryGetProperty("subPath", out _).Should().BeFalse();
    }

    [Fact]
    public void ToScopeJson_Filesystem_WritesSubPathAndPatternsAsArrays()
    {
        var scope = Parse(Filled().ToScopeJson(ConnectionProvider.Filesystem));

        scope.GetProperty("subPath").GetString().Should().Be("team-a");

        scope.GetProperty("includePatterns").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(["*.pdf", "*.md"]);
        scope.GetProperty("excludePatterns").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(["*.tmp"]);

        // A bucket typed before the provider was switched must not survive into the scope.
        scope.TryGetProperty("bucketName", out _).Should().BeFalse();
    }

    [Fact]
    public void ToScopeJson_FilesystemWithNoSubPath_StillWritesTheKeyAsEmpty()
    {
        // Empty means "the allowed root itself" to ConnectorFactory, which is a real choice
        // rather than a missing value — omitting the key entirely would mean the same thing
        // today but relies on the connector's null handling rather than saying so.
        var scope = Parse(new SourceForm { Name = "n" }.ToScopeJson(ConnectionProvider.Filesystem));

        scope.GetProperty("subPath").GetString().Should().BeEmpty();
    }

    [Fact]
    public void ToScopeJson_BlankPrefix_IsOmittedRatherThanWrittenEmpty()
    {
        // An empty prefix and no prefix mean the same thing to the connector, but writing
        // "prefix": "" makes the stored scope look deliberately narrowed when it is not.
        var form = new SourceForm { Name = "n", Container = "b", Prefix = "   " };

        Parse(form.ToScopeJson(ConnectionProvider.S3))
            .TryGetProperty("prefix", out _).Should().BeFalse();
    }

    [Fact]
    public void ToScopeJson_TrimsWhitespaceAroundValues()
    {
        var form = new SourceForm { Name = "n", Container = "  bucket  ", Prefix = "  docs/  " };
        var scope = Parse(form.ToScopeJson(ConnectionProvider.S3));

        scope.GetProperty("bucketName").GetString().Should().Be("bucket");
        scope.GetProperty("prefix").GetString().Should().Be("docs/");
    }

    [Theory]
    [InlineData("*.pdf\n*.md", 2)]
    [InlineData("*.pdf, *.md", 2)]
    [InlineData("*.pdf\r\n\r\n*.md", 2)]
    [InlineData("   ", 0)]
    [InlineData(null, 0)]
    public void ParsePatterns_HandlesTheSeparatorsAnOperatorActuallyTypes(string? raw, int expected)
    {
        SourceForm.ParsePatterns(raw).Should().HaveCount(expected);
    }

    [Fact]
    public void Validate_MissingName_IsRejected()
    {
        var form = new SourceForm { ConnectionId = Guid.NewGuid(), Container = "b" };
        form.Validate(ConnectionProvider.S3).Should().Contain("name");
    }

    [Fact]
    public void Validate_MissingConnection_IsRejected()
    {
        var form = new SourceForm { Name = "n", Container = "b" };
        form.Validate(ConnectionProvider.S3).Should().Contain("connection");
    }

    [Theory]
    [InlineData(ConnectionProvider.S3, "bucket")]
    public void Validate_CloudProviderWithoutAContainer_IsRejected(ConnectionProvider provider, string noun)
    {
        var form = new SourceForm { Name = "n", ConnectionId = Guid.NewGuid() };
        form.Validate(provider).Should().Contain(noun);
    }

    [Fact]
    public void Validate_FilesystemWithoutASubPath_IsAccepted()
    {
        // Unlike a bucket, an empty sub-path is meaningful: it selects the connection's root.
        var form = new SourceForm { Name = "n", ConnectionId = Guid.NewGuid() };
        form.Validate(ConnectionProvider.Filesystem).Should().BeNull();
    }

    [Fact]
    public void Validate_SyncIntervalBelowAMinute_IsRejected()
    {
        var form = new SourceForm
        {
            Name = "n", ConnectionId = Guid.NewGuid(), Container = "b", SyncIntervalSeconds = 30
        };

        form.Validate(ConnectionProvider.S3).Should().Contain("60");
    }

    [Fact]
    public void Validate_NoSyncInterval_IsAccepted()
    {
        // Null means "use the configured default", not "never sync".
        var form = new SourceForm { Name = "n", ConnectionId = Guid.NewGuid(), Container = "b" };
        form.Validate(ConnectionProvider.S3).Should().BeNull();
    }
    // ── SFTP ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The same scope shape as Filesystem, which was the point of designing it that way: this
    /// form, the preflight and the summary each gain a provider rather than a parallel
    /// implementation to keep in step.
    /// </summary>
    [Fact]
    public void ToScopeJson_Sftp_ProducesTheSameShapeAsFilesystem()
    {
        var form = new SourceForm
        {
            Name = "s",
            ConnectionId = Guid.NewGuid(),
            SubPath = "Documents",
            IncludePatterns = "*.md",
            ExcludePatterns = "*.tmp",
        };

        var sftp = JsonNode.Parse(form.ToScopeJson(ConnectionProvider.Sftp))!.AsObject();
        var filesystem = JsonNode.Parse(form.ToScopeJson(ConnectionProvider.Filesystem))!.AsObject();

        sftp.ToJsonString().Should().Be(filesystem.ToJsonString());
        sftp["subPath"]!.GetValue<string>().Should().Be("Documents");
    }

    /// <summary>
    /// Blank means the connection's root itself, which ConnectorFactory reads as a real value
    /// rather than a missing one — so the key must be present.
    /// </summary>
    [Fact]
    public void ToScopeJson_SftpWithNoSubPath_StillWritesTheKey()
    {
        var form = new SourceForm { Name = "s", ConnectionId = Guid.NewGuid() };

        JsonNode.Parse(form.ToScopeJson(ConnectionProvider.Sftp))!.AsObject()
            .ContainsKey("subPath").Should().BeTrue();
    }

    /// <summary>
    /// Before this, picking an SFTP connection threw out of the form. The throw is caught now
    /// rather than tearing down the circuit, but an operator still could not create the source.
    /// </summary>
    [Fact]
    public void ToScopeJson_Sftp_DoesNotThrow()
    {
        var form = new SourceForm { Name = "s", ConnectionId = Guid.NewGuid(), SubPath = "docs" };

        Action act = () => form.ToScopeJson(ConnectionProvider.Sftp);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_Sftp_DoesNotRequireAContainerName()
    {
        var form = new SourceForm { Name = "s", ConnectionId = Guid.NewGuid() };

        form.Validate(ConnectionProvider.Sftp).Should().BeNull(
            "an SFTP source is bounded by a sub-path, not a bucket");
    }
}

