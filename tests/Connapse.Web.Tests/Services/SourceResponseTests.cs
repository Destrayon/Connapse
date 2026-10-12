using System.Text.Json;
using Connapse.Core;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Endpoints;
using FluentAssertions;

namespace Connapse.Web.Tests.Services;

[Trait("Category", "Unit")]
public class SourceResponseTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static Source SourceWith(string scopeJson, string? lastSyncError = null) =>
        new(Guid.NewGuid(), "acme.atlassian.net / Engineering", null, Guid.NewGuid(), scopeJson, DateTime.UtcNow, DateTime.UtcNow)
        {
            LastSyncError = lastSyncError,
        };

    private const string ConfluenceScope =
        """{"kind":"confluence-space","spaceId":"98306","spaceKey":"ENG","spaceName":"Engineering","includeAttachments":false,"maxAttachmentMb":40}""";

    [Fact]
    public void Confluence_ForAnAdministrator_ReadsBackEveryFieldUnderTheNamesCreateAndPatchTake()
    {
        var response = SourceResponse.From(SourceWith(ConfluenceScope), includeDiagnostics: true);

        response.Confluence.Should().Be(new ConfluenceSourceSettings("98306", "ENG", "Engineering", false, 40));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, Web));
        var confluence = json.RootElement.GetProperty("confluence");
        confluence.GetProperty("spaceId").GetString().Should().Be("98306");
        confluence.GetProperty("spaceKey").GetString().Should().Be("ENG");
        confluence.GetProperty("spaceName").GetString().Should().Be("Engineering");
        confluence.GetProperty("includeAttachments").GetBoolean().Should().BeFalse();
        confluence.GetProperty("maxAttachmentMb").GetInt32().Should().Be(40);
    }

    [Fact]
    public void Confluence_AnOlderScopeWithoutTheAttachmentKeys_ReadsTheConnectorsDefaults()
    {
        var response = SourceResponse.From(
            SourceWith("""{"kind":"confluence-space","spaceId":"98306","spaceKey":"ENG"}"""), includeDiagnostics: true);

        response.Confluence!.IncludeAttachments.Should().BeTrue();
        response.Confluence.MaxAttachmentMb.Should().Be(25);
        response.Confluence.SpaceName.Should().BeNull();
    }

    [Fact]
    public void Confluence_ForAReader_IsWithheld()
    {
        SourceResponse.From(SourceWith(ConfluenceScope), includeDiagnostics: false).Confluence.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"bucketName":"payroll-data","prefix":"2026/"}""")]
    [InlineData("""{"owner":"acme","repo":"docs","kind":"Docs"}""")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void OtherProviders_NeverGetTheirScopeBack(string scopeJson)
    {
        var response = SourceResponse.From(SourceWith(scopeJson), includeDiagnostics: true);

        response.Confluence.Should().BeNull();
        string body = JsonSerializer.Serialize(response, Web);
        body.Should().NotContain("payroll-data").And.NotContain("scopeJson").And.NotContain("bucketName");
    }

    [Fact]
    public void LastSyncTroubleshootingUrl_ForAnAdministrator_LinksTheEntryTheSourcesPageLinks()
    {
        string error = AtlassianSyncErrors.SpaceUnreadable("ENG", 403);

        var admin = SourceResponse.From(SourceWith(ConfluenceScope, error), includeDiagnostics: true);
        var reader = SourceResponse.From(SourceWith(ConfluenceScope, error), includeDiagnostics: false);

        admin.LastSyncTroubleshootingUrl.Should().Be(AtlassianSyncErrors.TroubleshootingUrl(error));
        reader.LastSyncTroubleshootingUrl.Should().BeNull();
        SourceResponse.From(SourceWith(ConfluenceScope), includeDiagnostics: true).LastSyncTroubleshootingUrl.Should().BeNull();
    }
}
