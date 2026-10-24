using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Settings;
using Connapse.Web.Components.Sources;
using Connapse.Web.Endpoints;
using Connapse.Web.Services;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Web;

/// <summary>
/// Proof test for CON-46: one rule decides a stored Atlassian scope's kind, and every reader agrees with
/// it. The kind is the exact "kind" value, "jira-project" or "confluence-space", as the connector factory
/// syncs it; anything else names no kind. Connapse always stores the exact value, so only rows edited
/// outside it change.
/// </summary>
[Trait("Category", "Unit")]
public class AtlassianKindRuleTests
{
    public enum Kind { None, Jira, Confluence }

    public static TheoryData<string, string, Kind> Rows => new()
    {
        { "Jira, as Connapse stores it", """{"kind":"jira-project","projectId":"10000","projectKey":"ENG"}""", Kind.Jira },
        { "Confluence, as Connapse stores it", """{"kind":"confluence-space","spaceId":"98306","spaceKey":"DOCS"}""", Kind.Confluence },
        { "Jira kind in another case", """{"kind":"Jira-Project","projectId":"10000","projectKey":"ENG"}""", Kind.None },
        { "Confluence kind in another case", """{"kind":"CONFLUENCE-SPACE","spaceId":"98306","spaceKey":"DOCS"}""", Kind.None },
        { "kindless space", """{"spaceId":"98306","spaceKey":"DOCS"}""", Kind.None },
        { "kindless project", """{"projectId":"10000","projectKey":"ENG"}""", Kind.None },
        { "Jira carrying a custom source's keys",
            """{"kind":"jira-project","projectId":"10000","projectKey":"ENG","scopeId":"team","baseUrl":"https://cs.example.com"}""", Kind.Jira },
        { "unknown kind", """{"kind":"trello-board","spaceId":"98306","spaceKey":"DOCS"}""", Kind.None },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task EveryReader_AgreesOnTheStoredKind(string row, string scopeJson, Kind kind)
    {
        Guid connectionId = Guid.NewGuid();
        var source = new Source(Guid.NewGuid(), "s", null, connectionId, scopeJson, DateTime.UtcNow, DateTime.UtcNow);
        var connections = Substitute.For<IConnectionStore>();
        connections.GetAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns(new Connection(connectionId, "site", ConnectionProvider.Atlassian, "{}", null, DateTime.UtcNow, DateTime.UtcNow));

        bool jira = kind == Kind.Jira, confluence = kind == Kind.Confluence;

        // What syncs: the reader the others must agree with.
        SourceScopeCodec.Read(ConnectionProvider.Atlassian, scopeJson).Should().Match(s =>
            kind == Kind.None ? s == null : jira ? s is JiraProjectScope : s is ConfluenceSpaceScope, row);

        (SourceScopeCodec.AtlassianKindOf(scopeJson) == kind switch
        {
            Kind.Jira => AtlassianScopeKind.JiraProject,
            Kind.Confluence => AtlassianScopeKind.ConfluenceSpace,
            _ => AtlassianScopeKind.None,
        }).Should().BeTrue(row);

        string? summary = SourceScopeSummary.Describe(scopeJson);
        (summary?.Contains("ENG") == true).Should().Be(jira, $"{row}: the summary describes a Jira project only for a Jira row");
        (summary?.Contains("DOCS") == true).Should().Be(confluence, $"{row}: the summary describes a space only for a Confluence row");

        var response = SourceResponse.From(source, includeDiagnostics: true);
        (response.Jira is not null).Should().Be(jira, $"{row}: an administrator sees Jira settings only for a Jira row");
        (response.Confluence is not null).Should().Be(confluence, $"{row}: an administrator sees Confluence settings only for a Confluence row");

        PrivateSourceVisibility.IsSearchableWhenHidden(source).Should().Be(kind != Kind.None, $"{row}: only an Atlassian kind is checked live per hit");

        // Hiding fails closed: a row in any case of an Atlassian kind stays hidden from non-administrators,
        // here with no connection to hide it by, whatever the kind rule reads.
        var unbound = source with { ConnectionId = null };
        bool namesAnAtlassianKindInAnyCase = scopeJson.Contains("jira-project", StringComparison.OrdinalIgnoreCase)
            || scopeJson.Contains("confluence-space", StringComparison.OrdinalIgnoreCase);
        if (namesAnAtlassianKindInAnyCase)
            PrivateSourceVisibility.IsVisible(unbound, new HashSet<string>(), new HashSet<Guid>()).Should().BeFalse($"{row}: hidden as before");

        var form = SourceEditForm.From(source, ConnectionProvider.Atlassian);
        (form.Jira is not null).Should().Be(jira, $"{row}: the edit page offers Jira settings only for a Jira row");
        (form.Confluence is not null).Should().Be(confluence, $"{row}: the edit page offers Confluence settings only for a Confluence row");

        (await new SourceScopeRules(connections).CheckUpdateAsync(source, scopeJson)).Ok
            .Should().Be(kind != Kind.None, $"{row}: an update keeps a row's kind, so a row with none can't be updated in place");

        JiraProjectSelection.ProjectIdsOf([source]).Contains("10000").Should().Be(jira, $"{row}: only a Jira row holds a project");
        ConfluenceSpaceSelection.SpaceIdsOf([source]).Contains("98306").Should().Be(confluence, $"{row}: only a Confluence row holds a space");
    }
}
