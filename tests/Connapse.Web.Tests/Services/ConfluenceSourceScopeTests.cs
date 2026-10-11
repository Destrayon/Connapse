using System.Text.Json;
using Connapse.Web.Services;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Services;

/// <summary>
/// Changing a Confluence source's scope through the API: what it reads from the space may change,
/// under the dialog's rules, but the space itself is fixed.
/// </summary>
[Trait("Category", "Unit")]
public class ConfluenceSourceScopeTests
{
    private const string Current =
        """{"kind":"confluence-space","spaceId":"100","spaceKey":"ENG","includeAttachments":true,"maxAttachmentMb":25}""";

    [Fact]
    public void Update_AttachmentSettings_KeepsTheSpaceAndChangesOnlyThem()
    {
        var result = ConfluenceSourceScope.Update(Current, """{"includeAttachments":false,"maxAttachmentMb":10}""");

        result.Outcome.Should().Be(ConfluenceSourceScope.Outcome.Valid);
        using var doc = JsonDocument.Parse(result.ScopeJson!);
        doc.RootElement.GetProperty("spaceId").GetString().Should().Be("100");
        doc.RootElement.GetProperty("spaceKey").GetString().Should().Be("ENG");
        doc.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("maxAttachmentMb").GetInt32().Should().Be(10);
    }

    [Fact]
    public void Update_SameSpaceIdSentBack_IsAccepted()
    {
        ConfluenceSourceScope.Update(Current, """{"spaceId":100,"maxAttachmentMb":30}""").Outcome
            .Should().Be(ConfluenceSourceScope.Outcome.Valid);
    }

    [Fact]
    public void Update_AnotherSpace_IsRefusedOnSpaceId()
    {
        var result = ConfluenceSourceScope.Update(Current, """{"spaceId":"200"}""");

        result.Outcome.Should().Be(ConfluenceSourceScope.Outcome.Invalid);
        result.Field.Should().Be("spaceId");
    }

    [Theory]
    [InlineData("""{"maxAttachmentMb":0}""", "maxAttachmentMb")]
    [InlineData("""{"maxAttachmentMb":101}""", "maxAttachmentMb")]
    [InlineData("""{"maxAttachmentMb":"big"}""", "maxAttachmentMb")]
    [InlineData("""{"maxAttachmentMb":2.5}""", "maxAttachmentMb")]
    [InlineData("""{"includeAttachments":"yes"}""", "includeAttachments")]
    [InlineData("""{"spaceId":"abc"}""", "spaceId")]
    [InlineData("""[1,2]""", "scopeJson")]
    public void Update_InvalidValue_IsRefusedNamingTheField(string scope, string field)
    {
        var result = ConfluenceSourceScope.Update(Current, scope);

        result.Outcome.Should().Be(ConfluenceSourceScope.Outcome.Invalid);
        result.Field.Should().Be(field);
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Update_OutOfRangeSizeWithAttachmentsOff_IsAcceptedAsTheDialogDoes()
    {
        ConfluenceSourceScope.Update(Current, """{"includeAttachments":false,"maxAttachmentMb":0}""").Outcome
            .Should().Be(ConfluenceSourceScope.Outcome.Valid);
    }

    [Fact]
    public void Update_OutOfRangeSizeWithAttachmentsOff_KeepsTheStoredSizeSoTheSourceStillSyncs()
    {
        // The connector refuses a cap that is not positive whether or not attachments are on.
        var result = ConfluenceSourceScope.Update(Current, """{"includeAttachments":false,"maxAttachmentMb":0}""");

        using var doc = JsonDocument.Parse(result.ScopeJson!);
        doc.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("maxAttachmentMb").GetInt32().Should().Be(25);
    }
}
