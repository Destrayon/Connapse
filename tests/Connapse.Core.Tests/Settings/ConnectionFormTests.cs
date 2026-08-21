using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Web.Components.Settings;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Settings;

/// <summary>
/// The connection editor's mapping between form fields and stored config JSON.
/// <para>
/// This is the part of the Connections tab that carries risk: a field dropped in the round trip
/// produces a connection that resolves somewhere other than the operator intended, and nothing
/// downstream can tell the difference. Extracted from the Razor component precisely so it can be
/// tested — this repository has no component test harness.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ConnectionFormTests
{
    private static Connection Stored(ConnectionProvider provider, string configJson, bool hasSecret = false) => new(
        Id: Guid.NewGuid(),
        Name: "conn",
        Provider: provider,
        ConfigJson: configJson,
        CreatedByUserId: null,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        HasSecret: hasSecret);

    // ── Round trip ────────────────────────────────────────────────────

    [Fact]
    public void S3Connection_RoundTripsEveryField()
    {
        var stored = Stored(ConnectionProvider.S3,
            """{"region":"eu-west-1","roleArn":"arn:aws:iam::1:role/r","allowedLocations":["a","b/docs"]}""");

        var form = ConnectionForm.FromConnection(stored);

        form.Region.Should().Be("eu-west-1");
        form.RoleArn.Should().Be("arn:aws:iam::1:role/r");
        form.AllowedLocations.Should().Be("a\nb/docs");

        var reserialized = JsonNode.Parse(form.ToConfigJson())!.AsObject();
        reserialized["region"]!.GetValue<string>().Should().Be("eu-west-1");
        reserialized["roleArn"]!.GetValue<string>().Should().Be("arn:aws:iam::1:role/r");
        reserialized["allowedLocations"]!.AsArray().Select(x => x!.GetValue<string>())
            .Should().BeEquivalentTo(["a", "b/docs"]);
    }

    [Fact]
    public void AzureConnection_RoundTripsEveryField()
    {
        var stored = Stored(ConnectionProvider.AzureBlob,
            """{"storageAccountName":"acct","managedIdentityClientId":"mi-1","allowedLocations":["public"]}""");

        var form = ConnectionForm.FromConnection(stored);

        form.StorageAccountName.Should().Be("acct");
        form.ManagedIdentityClientId.Should().Be("mi-1");

        var reserialized = JsonNode.Parse(form.ToConfigJson())!.AsObject();
        reserialized["storageAccountName"]!.GetValue<string>().Should().Be("acct");
        // Dropping this silently falls back to the default identity, which may have wider access.
        reserialized["managedIdentityClientId"]!.GetValue<string>().Should().Be("mi-1");
    }

    [Fact]
    public void FilesystemConnection_RoundTripsItsRoot()
    {
        var stored = Stored(ConnectionProvider.Filesystem, """{"allowedRoot":"/data/docs"}""");

        var form = ConnectionForm.FromConnection(stored);
        form.AllowedRoot.Should().Be("/data/docs");

        JsonNode.Parse(form.ToConfigJson())!["allowedRoot"]!.GetValue<string>().Should().Be("/data/docs");
    }

    // ── Serialization rules ───────────────────────────────────────────

    [Fact]
    public void ToConfigJson_OmitsFieldsBelongingToOtherProviders()
    {
        // A form that has been switched between providers must not leave a stale key behind —
        // the connector factory reads whatever is present and would honour it.
        var form = new ConnectionForm
        {
            Name = "c",
            Provider = ConnectionProvider.S3,
            Region = "us-east-1",
            StorageAccountName = "left-over-from-azure",
            AllowedRoot = "/left-over-from-filesystem",
        };

        var node = JsonNode.Parse(form.ToConfigJson())!.AsObject();

        node.ContainsKey("region").Should().BeTrue();
        node.ContainsKey("storageAccountName").Should().BeFalse();
        node.ContainsKey("allowedRoot").Should().BeFalse();
    }

    [Fact]
    public void ToConfigJson_FilesystemOmitsAllowedLocations()
    {
        // Filesystem confinement is the root plus the subpath check; allowedLocations is the
        // cloud equivalent and does not apply.
        var form = new ConnectionForm
        {
            Name = "c",
            Provider = ConnectionProvider.Filesystem,
            AllowedRoot = "/data",
            AllowedLocations = "some-bucket",
        };

        JsonNode.Parse(form.ToConfigJson())!.AsObject()
            .ContainsKey("allowedLocations").Should().BeFalse();
    }

    [Fact]
    public void ToConfigJson_OmitsAllowedLocationsWhenBlank()
    {
        // Absent must stay absent: an empty array would read as a declared-but-empty allowlist,
        // which denies everything rather than taking the grace path.
        var form = new ConnectionForm { Name = "c", Provider = ConnectionProvider.S3, AllowedLocations = "  \n \n" };

        JsonNode.Parse(form.ToConfigJson())!.AsObject()
            .ContainsKey("allowedLocations").Should().BeFalse();
    }

    [Fact]
    public void ToConfigJson_DefaultsRegionWhenLeftBlank()
    {
        var form = new ConnectionForm { Name = "c", Provider = ConnectionProvider.S3 };

        JsonNode.Parse(form.ToConfigJson())!["region"]!.GetValue<string>().Should().Be("us-east-1");
    }

    // ── The secret is never read back ─────────────────────────────────

    [Fact]
    public void FromConnection_NeverPopulatesTheSecret()
    {
        // There is no read path for a stored secret outside the sync engine, and the editor must
        // not become one. The field only ever carries a replacement the operator just typed.
        var form = ConnectionForm.FromConnection(
            Stored(ConnectionProvider.S3, """{"region":"eu-west-1","secret":"should-never-surface"}""", hasSecret: true));

        form.Secret.Should().BeNull();
        form.ToConfigJson().Should().NotContain("should-never-surface");
    }

    // ── Robustness ────────────────────────────────────────────────────

    [Fact]
    public void FromConnection_MalformedJson_YieldsAnEmptyFormRatherThanThrowing()
    {
        var form = ConnectionForm.FromConnection(Stored(ConnectionProvider.S3, "{not valid json"));

        form.Provider.Should().Be(ConnectionProvider.S3);
        form.Region.Should().BeNull();
    }

    [Fact]
    public void FromConnection_NullConfig_YieldsAnEmptyForm()
    {
        var stored = new Connection(Guid.NewGuid(), "c", ConnectionProvider.S3, null, null,
            DateTime.UtcNow, DateTime.UtcNow);

        ConnectionForm.FromConnection(stored).Region.Should().BeNull();
    }

    // ── Location splitting, for the test probe ────────────────────────

    [Theory]
    [InlineData("bucket", "bucket", null)]
    [InlineData("bucket/docs", "bucket", "docs")]
    [InlineData("bucket/docs/2026/", "bucket", "docs/2026")]
    [InlineData("/bucket/docs/", "bucket", "docs")]
    public void SplitLocation_SeparatesContainerFromPrefix(string input, string container, string? prefix)
    {
        var (actualContainer, actualPrefix) = ConnectionForm.SplitLocation(input);

        actualContainer.Should().Be(container);
        actualPrefix.Should().Be(prefix);
    }

    [Fact]
    public void FirstAllowedLocation_IsTheProbeDefault()
    {
        var form = new ConnectionForm { AllowedLocations = "first-bucket\nsecond-bucket" };

        form.FirstAllowedLocation().Should().Be("first-bucket");
    }

    [Fact]
    public void FirstAllowedLocation_IsNullWhenNoneDeclared()
    {
        new ConnectionForm().FirstAllowedLocation().Should().BeNull();
    }

    // ── Validation ────────────────────────────────────────────────────

    [Fact]
    public void Validate_RequiresAName()
    {
        new ConnectionForm { Name = "  " }.Validate().Should().Contain("name");
    }

    [Fact]
    public void Validate_FilesystemRequiresARoot()
    {
        new ConnectionForm { Name = "c", Provider = ConnectionProvider.Filesystem }
            .Validate().Should().Contain("root");
    }

    [Fact]
    public void Validate_AzureRequiresAStorageAccount()
    {
        new ConnectionForm { Name = "c", Provider = ConnectionProvider.AzureBlob }
            .Validate().Should().Contain("storage account");
    }

    [Fact]
    public void Validate_ValidS3Form_PassesWithNoRegion()
    {
        // Region defaults rather than being required.
        new ConnectionForm { Name = "c", Provider = ConnectionProvider.S3 }
            .Validate().Should().BeNull();
    }
}
