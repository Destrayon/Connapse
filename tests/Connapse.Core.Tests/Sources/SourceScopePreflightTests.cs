using Connapse.Core;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Sources;

/// <summary>
/// The pre-flight check is a UX affordance, not a security boundary — <c>ConnectorFactory</c>
/// re-checks the same policy on every sync cycle. What these tests pin is that it gives the
/// <em>same answer</em> as the factory would, because a pre-check that disagrees with the real
/// one is worse than none: it either blocks a valid source or promises one that will fail.
/// </summary>
[Trait("Category", "Unit")]
public class SourceScopePreflightTests
{
    private static SourceScopePreflight Build(params string[] allowedFilesystemRoots)
    {
        var monitor = Substitute.For<IOptionsMonitor<SourceSecuritySettings>>();
        monitor.CurrentValue.Returns(new SourceSecuritySettings
        {
            AllowedFilesystemRoots = allowedFilesystemRoots
        });

        return new SourceScopePreflight(monitor);
    }

    private static Connection Conn(ConnectionProvider provider, string? configJson) => new(
        Id: Guid.NewGuid(),
        Name: "conn",
        Provider: provider,
        ConfigJson: configJson,
        CreatedByUserId: null,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    [Fact]
    public void Check_S3BucketInsideAllowedLocations_IsAllowed()
    {
        var connection = Conn(ConnectionProvider.S3, """{"allowedLocations":["company-knowledge"]}""");

        var result = Build().Check(connection, """{"bucketName":"company-knowledge"}""");

        result.IsRefused.Should().BeFalse();
        result.Warning.Should().BeNull();
    }

    [Fact]
    public void Check_S3BucketOutsideAllowedLocations_IsRefusedNamingTheScope()
    {
        var connection = Conn(ConnectionProvider.S3, """{"allowedLocations":["company-knowledge"]}""");

        var result = Build().Check(connection, """{"bucketName":"payroll-data"}""");

        result.IsRefused.Should().BeTrue();
        // The operator needs to know which of the two things is wrong — what they typed, or
        // what the connection permits.
        result.Error.Should().Contain("payroll-data").And.Contain("conn");
    }

    [Fact]
    public void Check_NoAllowedLocations_IsAcceptedWithAWarning()
    {
        // Matches ConnectorFactory, which warns and proceeds: #350 backfilled connections that
        // declare no locations, so refusing here would block every upgraded deployment.
        var connection = Conn(ConnectionProvider.S3, """{"region":"us-east-1"}""");

        var result = Build().Check(connection, """{"bucketName":"anything"}""");

        result.IsRefused.Should().BeFalse();
        result.Warning.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Check_AllowedLocationsPresentButAllBlank_IsRefused()
    {
        // A malformed allowlist is not an absent one. Reading it as "no restrictions" would
        // turn a typo into an open door, which is the distinction StorageLocationPolicy draws.
        var connection = Conn(ConnectionProvider.S3, """{"allowedLocations":["","   "]}""");

        var result = Build().Check(connection, """{"bucketName":"anything"}""");

        result.IsRefused.Should().BeTrue();
    }

    [Fact]
    public void Check_AzureContainerOutsideAllowedLocations_IsRefused()
    {
        var connection = Conn(ConnectionProvider.AzureBlob,
            """{"storageAccountName":"acct","allowedLocations":["knowledge"]}""");

        Build().Check(connection, """{"containerName":"secrets"}""").IsRefused.Should().BeTrue();
    }

    [Fact]
    public void Check_MissingContainerName_IsRefusedBeforeThePolicyRuns()
    {
        // StorageLocationPolicy.Evaluate throws on a blank container, so the guard has to come
        // first — otherwise a half-filled form takes the page down instead of showing an error.
        var connection = Conn(ConnectionProvider.S3, """{"allowedLocations":["b"]}""");

        var act = () => Build().Check(connection, "{}");

        act.Should().NotThrow();
        Build().Check(connection, "{}").IsRefused.Should().BeTrue();
    }

    [Fact]
    public void Check_UnparseableScope_IsRefusedRatherThanThrowing()
    {
        var connection = Conn(ConnectionProvider.S3, """{"allowedLocations":["b"]}""");

        Build().Check(connection, "not json").IsRefused.Should().BeTrue();
    }

    [Fact]
    public void Check_AllowedLocationsHoldingANonString_DoesNotThrow()
    {
        // A stored array with a number in it is a bad config, not a reason to break the form.
        var connection = Conn(ConnectionProvider.S3, """{"allowedLocations":["ok",42]}""");

        var act = () => Build().Check(connection, """{"bucketName":"ok"}""");

        act.Should().NotThrow();
    }

    [Fact]
    public void Check_FilesystemWithoutAnAllowedRoot_IsRefused()
    {
        var connection = Conn(ConnectionProvider.Filesystem, "{}");

        Build().Check(connection, """{"subPath":"a"}""").IsRefused.Should().BeTrue();
    }

    [Fact]
    public void Check_FilesystemSubPathEscapingTheRoot_IsRefused()
    {
        string root = Path.Combine(Path.GetTempPath(), $"preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var connection = Conn(ConnectionProvider.Filesystem,
                $$"""{"allowedRoot":{{System.Text.Json.JsonSerializer.Serialize(root)}}}""");

            var result = Build(root).Check(connection, """{"subPath":"../../etc"}""");

            result.IsRefused.Should().BeTrue();
            result.Error.Should().Contain("outside");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Check_FilesystemRootOutsideTheDeploymentAllowlist_IsRefused()
    {
        string permitted = Path.Combine(Path.GetTempPath(), $"permitted-{Guid.NewGuid():N}");
        string other = Path.Combine(Path.GetTempPath(), $"other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(permitted);
        Directory.CreateDirectory(other);

        try
        {
            var connection = Conn(ConnectionProvider.Filesystem,
                $$"""{"allowedRoot":{{System.Text.Json.JsonSerializer.Serialize(other)}}}""");

            var result = Build(permitted).Check(connection, """{"subPath":""}""");

            result.IsRefused.Should().BeTrue();
            result.Error.Should().Contain(SourceSecuritySettings.SectionName);
        }
        finally
        {
            Directory.Delete(permitted, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void Check_FilesystemWithNoDeploymentAllowlist_IsAcceptedWithAWarning()
    {
        string root = Path.Combine(Path.GetTempPath(), $"unbounded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var connection = Conn(ConnectionProvider.Filesystem,
                $$"""{"allowedRoot":{{System.Text.Json.JsonSerializer.Serialize(root)}}}""");

            var result = Build().Check(connection, """{"subPath":""}""");

            result.IsRefused.Should().BeFalse();
            result.Warning.Should().Contain(SourceSecuritySettings.SectionName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    // ── Malformed connection configuration ─────────────────────────────────

    /// <summary>
    /// Blank and malformed both parse to null, and conflating them defeats the point of
    /// preflighting at all: ConnectorFactory throws on malformed JSON at the first sync, while
    /// an empty credential reads here as "declares no allowlist", which is only a warning. The
    /// source would be accepted and then never work.
    /// </summary>
    [Theory]
    [InlineData(ConnectionProvider.S3, """{"bucketName":"b"}""")]
    [InlineData(ConnectionProvider.AzureBlob, """{"containerName":"c"}""")]
    [InlineData(ConnectionProvider.Filesystem, """{"subPath":"docs"}""")]
    public void Check_MalformedConnectionConfig_IsRefused(ConnectionProvider provider, string scope)
    {
        var result = Build().Check(Conn(provider, "{not valid json"), scope);

        result.IsRefused.Should().BeTrue();
        result.Error.Should().Contain("not valid JSON");
    }

    /// <summary>
    /// Blank must keep its meaning. The factory reads a blank config as "{}", so preflight has
    /// to agree — refusing here would block connections that work perfectly well.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Check_BlankConnectionConfig_IsNotTreatedAsMalformed(string? configJson)
    {
        var result = Build().Check(Conn(ConnectionProvider.S3, configJson), """{"bucketName":"b"}""");

        result.IsRefused.Should().BeFalse(
            "the connector factory reads a blank config as an empty object, and preflight must match");
    }
    /// <summary>
    /// The same collapse the all-blank case guards against, arriving by another route. Skipping
    /// non-string elements shrinks a declared-but-broken allowlist down to an empty one, which
    /// reads as "declared nothing" and only warns — so a broken allowlist would be presented as
    /// an absent one.
    /// </summary>
    [Theory]
    [InlineData("""{"allowedLocations":[42]}""")]
    [InlineData("""{"allowedLocations":[null]}""")]
    [InlineData("""{"allowedLocations":[{"bucket":"b"}]}""")]
    [InlineData("""{"allowedLocations":[42,"other-bucket"]}""")]
    public void Check_AllowedLocationsHoldingNonStrings_IsRefusedNotWarned(string configJson)
    {
        var result = Build().Check(Conn(ConnectionProvider.S3, configJson), """{"bucketName":"b"}""");

        result.IsRefused.Should().BeTrue(
            "a malformed allowlist must not be mistaken for an absent one");
    }

    /// <summary>
    /// The other half: a well-formed allowlist that genuinely covers the bucket still passes, so
    /// the fix above cannot have made every array refuse.
    /// </summary>
    [Fact]
    public void Check_WellFormedAllowedLocations_StillAllowTheirBucket()
    {
        var result = Build().Check(
            Conn(ConnectionProvider.S3, """{"allowedLocations":["b","other"]}"""),
            """{"bucketName":"b"}""");

        result.IsRefused.Should().BeFalse();
    }
}
