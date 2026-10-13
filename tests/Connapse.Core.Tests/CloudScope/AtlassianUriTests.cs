using Connapse.Storage.CloudScope;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public sealed class AtlassianUriTests
{
    private const string Cloud = "11111111-2222-3333-4444-55555555abcd";

    [Fact]
    public void ForPage_And_ForAttachment_BuildTheDocumentedShapes()
    {
        AtlassianUri.ForPage(Cloud, "42").Should().Be($"atlassian://{Cloud}/confluence/page/42");
        AtlassianUri.ForAttachment(Cloud, "42", "7").Should().Be($"atlassian://{Cloud}/confluence/page/42/attachment/7");
    }

    [Theory]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42/attachment/7")]
    [InlineData("atlassian://11111111-2222-3333-4444-55555555ABCD/confluence/page/42")]
    public void TryParse_ValidUri_ReturnsLowercaseCloudIdAndPageId(string uri)
    {
        AtlassianUri.TryParse(uri, out string cloudId, out string contentId).Should().BeTrue();

        cloudId.Should().Be(Cloud);
        contentId.Should().Be("42");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3://bucket/x")]
    [InlineData("atlassian://acme/confluence/page/42")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/4a")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42/")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42/extra")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42/attachment/x")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42?x=1")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42#frag")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/42\n")]
    [InlineData("atlassian://" + Cloud + "/confluence/page/../42")]
    [InlineData("atlassian://" + Cloud + "/confluence/blogpost/42")]
    [InlineData("ATLASSIAN://" + Cloud + "/confluence/page/42")]
    public void TryParse_AnythingElse_IsRejected(string? uri) =>
        AtlassianUri.TryParse(uri, out _, out _).Should().BeFalse();

    private const string Group = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private static JiraResource Parse(string uri)
    {
        AtlassianUri.TryParseJira(uri, out JiraResource resource).Should().BeTrue();
        return resource;
    }

    [Fact]
    public void JiraUri_Issue_RoundTrips()
    {
        string uri = AtlassianUri.ForJiraIssue(Cloud, "10001");

        uri.Should().Be($"atlassian://{Cloud}/jira/issue/10001");
        Parse(uri).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Issue, null, false, null));
    }

    [Fact]
    public void JiraUri_CommentGroup_RoundTrips()
    {
        var restriction = new JiraRestriction(JiraRestrictionType.Group, Group);
        string uri = AtlassianUri.ForJiraComment(Cloud, "10001", "55", false, restriction);

        uri.Should().Be($"atlassian://{Cloud}/jira/issue/10001/comment/55/group/{Group}");
        Parse(uri).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Comment, "55", false, restriction));
    }

    [Fact]
    public void JiraUri_CommentRole_RoundTrips()
    {
        var restriction = new JiraRestriction(JiraRestrictionType.Role, "Administrators", "10200");
        string uri = AtlassianUri.ForJiraComment(Cloud, "10001", "55", false, restriction);

        uri.Should().Be($"atlassian://{Cloud}/jira/issue/10001/comment/55/role/10200/Administrators");
        Parse(uri).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Comment, "55", false, restriction));
    }

    [Fact]
    public void JiraUri_InternalNote_RoundTrips()
    {
        string uri = AtlassianUri.ForJiraComment(Cloud, "10001", "55", true, null);

        uri.Should().Be($"atlassian://{Cloud}/jira/issue/10001/comment/55/internal");
        Parse(uri).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Comment, "55", true, null));
    }

    [Fact]
    public void JiraUri_InternalNoteWithGroup_RoundTrips()
    {
        var restriction = new JiraRestriction(JiraRestrictionType.Group, Group);
        string uri = AtlassianUri.ForJiraComment(Cloud, "10001", "55", true, restriction);

        uri.Should().Be($"atlassian://{Cloud}/jira/issue/10001/comment/55/internal/group/{Group}");
        Parse(uri).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Comment, "55", true, restriction));
    }

    [Fact]
    public void JiraUri_AttachmentWithRole_RoundTrips()
    {
        var restriction = new JiraRestriction(JiraRestrictionType.Role, "Developers", "10200");
        string uri = AtlassianUri.ForJiraAttachment(Cloud, "10001", "77", false, restriction, "55");

        uri.Should().Be($"atlassian://{Cloud}/jira/issue/10001/attachment/77/comment/55/role/10200/Developers");
        Parse(uri).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Attachment, "77", false, restriction, "55"));
    }

    [Fact]
    public void JiraUri_AttachmentOfInternalNote_RoundTrips()
    {
        string uri = AtlassianUri.ForJiraAttachment(Cloud, "10001", "77", true, null, "55");

        uri.Should().Be($"atlassian://{Cloud}/jira/issue/10001/attachment/77/comment/55/internal");
        Parse(uri).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Attachment, "77", true, null, "55"));
    }

    [Fact]
    public void JiraUri_AttachmentWithInternalGroup_RoundTrips()
    {
        var restriction = new JiraRestriction(JiraRestrictionType.Group, Group);
        string uri = AtlassianUri.ForJiraAttachment(Cloud, "10001", "77", true, restriction, "55");

        uri.Should().Be($"atlassian://{Cloud}/jira/issue/10001/attachment/77/comment/55/internal/group/{Group}");
        Parse(uri).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Attachment, "77", true, restriction, "55"));
    }

    [Fact]
    public void JiraUri_AttachmentGovernedByPublicComment_RoundTrips()
    {
        // Released because its comment went public: no restriction, but still the comment to re-check.
        string uri = AtlassianUri.ForJiraAttachment(Cloud, "10001", "77", false, null, "55");

        uri.Should().Be($"atlassian://{Cloud}/jira/issue/10001/attachment/77/comment/55");
        Parse(uri).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Attachment, "77", false, null, "55"));
        AtlassianUri.TryParseJira($"atlassian://{Cloud}/jira/issue/10001/comment/55", out _)
            .Should().BeFalse("a comment document with no restriction is never separate");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JiraUri_UntiedAttachment_PlainOrDenyAll_RoundTrips(bool internalNote)
    {
        string plain = AtlassianUri.ForJiraAttachment(Cloud, "10001", "77", false, null);
        string denied = AtlassianUri.ForJiraAttachment(Cloud, "10001", "77", internalNote, JiraRestriction.DenyAll);

        plain.Should().Be($"atlassian://{Cloud}/jira/issue/10001/attachment/77");
        Parse(plain).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Attachment, "77", false, null));
        Parse(denied).Should().Be(new JiraResource(Cloud, "10001", JiraResourceKind.Attachment, "77", internalNote, JiraRestriction.DenyAll));
    }

    [Theory]
    [InlineData("/jira/issue/1/attachment/2/group/" + Group)]               // a live restriction without its comment
    [InlineData("/jira/issue/1/attachment/2/role/10200/Developers")]
    [InlineData("/jira/issue/1/attachment/2/internal")]
    [InlineData("/jira/issue/1/attachment/2/comment/3/")]
    [InlineData("/jira/issue/1/attachment/2/comment/x/group/" + Group)]
    [InlineData("/jira/issue/1/attachment/2/group/" + Group + "/comment/3")] // the comment out of place
    [InlineData("/jira/issue/1/attachment/2/comment/3/comment/4/internal")]
    [InlineData("/jira/issue/1/comment/2/comment/3/internal")]               // only an attachment names a comment
    [InlineData("/jira/issue/1/comment/3/internal/comment/4")]
    public void JiraUri_AttachmentTieShape_Rejected(string suffix) =>
        AtlassianUri.TryParseJira("atlassian://" + Cloud + suffix, out _).Should().BeFalse();

    [Theory]
    [InlineData("QA/Ops Team")]
    [InlineData("Service Desk Team")]
    [InlineData("Développeurs")]
    public void JiraUri_RoleNameWithSlashAndSpaces_RoundTrips(string name)
    {
        var restriction = new JiraRestriction(JiraRestrictionType.Role, name, "10200");
        string uri = AtlassianUri.ForJiraComment(Cloud, "10001", "55", false, restriction);

        JiraResource resource = Parse(uri);

        resource.Restriction.Should().Be(restriction);
        resource.Kind.Should().Be(JiraResourceKind.Comment);
    }

    [Fact]
    public void JiraUri_UppercaseGuids_AreNormalizedToLowercase()
    {
        string uri = $"atlassian://{Cloud.ToUpperInvariant()}/jira/issue/1/comment/2/group/{Group.ToUpperInvariant()}";

        JiraResource resource = Parse(uri);

        resource.CloudId.Should().Be(Cloud);
        resource.Restriction!.Value.Should().Be(Group);
    }

    [Theory]
    [InlineData("/jira/issue/1/comment/2")]
    [InlineData("/jira/issue/1/comment/2/")]
    public void JiraUri_CommentWithoutRestriction_Rejected(string suffix) =>
        AtlassianUri.TryParseJira("atlassian://" + Cloud + suffix, out _).Should().BeFalse();

    [Theory]
    [InlineData("/jira/issue/1/internal")]
    [InlineData("/jira/issue/1/group/" + Group)]
    [InlineData("/jira/issue/1/role/10200/Developers")]
    public void JiraUri_IssueWithRestriction_Rejected(string suffix) =>
        AtlassianUri.TryParseJira("atlassian://" + Cloud + suffix, out _).Should().BeFalse();

    [Theory]
    [InlineData("/jira/issue/1/comment/2/internal/extra")]
    [InlineData("/jira/issue/1/comment/2/internal/")]
    [InlineData("/jira/issue/1/comment/2/group/" + Group + "/x")]
    [InlineData("/jira/issue/1/extra")]
    [InlineData("/jira/issue/1\n")]
    public void JiraUri_TrailingSegment_Rejected(string suffix) =>
        AtlassianUri.TryParseJira("atlassian://" + Cloud + suffix, out _).Should().BeFalse();

    [Theory]
    [InlineData("/jira/issue/1/comment/2/group/not-a-guid")]
    [InlineData("/jira/issue/1/comment/2/group/10002")]
    public void JiraUri_NonGuidGroup_Rejected(string suffix) =>
        AtlassianUri.TryParseJira("atlassian://" + Cloud + suffix, out _).Should().BeFalse();

    [Theory]
    [InlineData("/jira/issue/1/comment/2/role/10200/%20")]
    [InlineData("/jira/issue/1/comment/2/role/10200/%20%20")]
    [InlineData("/jira/issue/1/comment/2/role/x/Developers")]
    public void JiraUri_BlankOrMalformedRole_Rejected(string suffix) =>
        AtlassianUri.TryParseJira("atlassian://" + Cloud + suffix, out _).Should().BeFalse();

    [Theory]
    [InlineData("%GG")]
    [InlineData("%")]
    [InlineData("%2")]
    [InlineData("%FF")]
    [InlineData("Dev%")]
    [InlineData("QA%2fOps%20Team")]
    [InlineData("D%c3%a9veloppeurs")]
    [InlineData("Dev%65lopers")]
    [InlineData("%C3")]
    public void JiraUri_NonCanonicalRoleEscape_Rejected(string role) =>
        AtlassianUri.TryParseJira($"atlassian://{Cloud}/jira/issue/1/comment/2/role/10200/{role}", out _).Should().BeFalse();

    [Theory]
    [InlineData("QA/Ops Team", "QA%2FOps%20Team")]
    [InlineData("Développeurs", "D%C3%A9veloppeurs")]
    public void JiraUri_CanonicalRoleEscape_Accepted(string name, string segment)
    {
        JiraResource resource = Parse($"atlassian://{Cloud}/jira/issue/1/comment/2/role/10200/{segment}");

        resource.Restriction.Should().Be(new JiraRestriction(JiraRestrictionType.Role, name, "10200"));
        AtlassianUri.ForJiraComment(Cloud, "1", "2", false, resource.Restriction).Should().EndWith("/role/10200/" + segment);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3://bucket/x")]
    public void JiraUri_NotAtlassian_Rejected(string? uri) =>
        AtlassianUri.TryParseJira(uri, out _).Should().BeFalse();

    [Fact]
    public void JiraUri_ConfluenceUri_NotJira() =>
        AtlassianUri.TryParseJira(AtlassianUri.ForPage(Cloud, "42"), out _).Should().BeFalse();

    [Fact]
    public void JiraUri_TryParse_RejectsJiraUri() =>
        AtlassianUri.TryParse(AtlassianUri.ForJiraIssue(Cloud, "42"), out _, out _).Should().BeFalse();
}
