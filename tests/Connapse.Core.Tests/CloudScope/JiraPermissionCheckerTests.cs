using System.Net;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static Connapse.Core.Tests.CloudScope.ConfluencePermissionCheckerTests;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public sealed class JiraPermissionCheckerTests : IDisposable
{
    private const string Account = "557058:abc";
    private const string GroupA = "6e87dc72-4f1f-421f-9382-2fee8b652487";
    private const string GroupB = "0b5e1b8e-56b0-4a35-9b8a-3a6b3c0c9f11";
    private const string GroupC = "c2d6f3a4-1e5b-4c7d-8e9f-0a1b2c3d4e5f";
    private const string Project = FakeJira.ProjectId;

    private readonly FakeAtlassianApi _api = new();
    private readonly RecordingCache _cache = new();
    private readonly FakeJira _jira;
    private readonly JiraPermissionChecker _checker;

    public JiraPermissionCheckerTests()
    {
        _jira = new FakeJira(_api, CloudId).MapEnforcement();
        _checker = NewJiraChecker(_cache);
    }

    public void Dispose()
    {
        _api.Dispose();
        _cache.Dispose();
    }

    internal static JiraPermissionChecker NewJiraChecker(IMemoryCache cache) =>
        new(cache, NullLogger<JiraPermissionChecker>.Instance);

    private AtlassianSiteLookup Sites() => NewChecker(_api, _cache, AtlassianConnectionStore([AtlassianProduct.Jira])).NewSiteLookup();

    private Task<bool[]> CheckAsync(params JiraResource[] resources) => CheckAsync(Account, resources);

    /// <summary>
    /// Checks the resources. A comment whose live read no test has mapped is answered as Jira
    /// agreeing with its URI, so tests about browse and membership need not map one.
    /// </summary>
    private Task<bool[]> CheckAsync(string account, params JiraResource[] resources)
    {
        foreach (var r in resources.Where(r => r.Kind == JiraResourceKind.Comment && !_jira.HasLiveComment(r.IssueId, r.ChildId!)))
            _jira.LiveComment(r.IssueId, r.ChildId!, VisibilityOf(r.Restriction), r.Internal ? false : null);
        foreach (var r in resources.Where(r => r.Restriction is { Type: JiraRestrictionType.Role, ProjectId: not null } && !_jira.HasLiveIssue(r.IssueId)))
            _jira.LiveIssueProject(r.IssueId, r.Restriction!.ProjectId!);
        return _checker.CanSeeAsync(CloudId, account, resources, Sites(), default);
    }

    /// <summary>Jira's visibility object for a restriction, as a comment carrying it reads.</summary>
    private static object? VisibilityOf(JiraRestriction? restriction) => restriction switch
    {
        null => null,
        { Type: JiraRestrictionType.Group } => new { type = "group", value = "some-group", identifier = restriction.Value },
        _ => new { type = "role", value = restriction.Value, identifier = restriction.Value },
    };

    private static JiraResource Issue(string id) => new(CloudId, id, JiraResourceKind.Issue, null, false, null);

    private readonly Dictionary<string, string> _commentIds = [];

    /// <summary>A comment document; each distinct internal flag and restriction is its own comment id, from 77 up.</summary>
    private JiraResource Comment(string issue, bool internalNote = false, JiraRestriction? restriction = null, string? id = null)
    {
        string shape = $"{internalNote}|{restriction}";
        if (id is null && !_commentIds.TryGetValue(shape, out id))
            _commentIds[shape] = id = (77 + _commentIds.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(CloudId, issue, JiraResourceKind.Comment, id, internalNote, restriction);
    }

    private int CommentReads => _api.Calls.Where(c => c.Key.Contains("/comment/", StringComparison.Ordinal)).Sum(c => c.Value);

    private const string LiveKeyPrefix = $"atl:jira:{CloudId}:comment:";

    private static JiraRestriction Group(string groupId) => new(JiraRestrictionType.Group, groupId);

    private static JiraRestriction Role(string name) => new(JiraRestrictionType.Role, name, Project);

    private int Calls(string relative) => _api.Calls.GetValueOrDefault(_jira.Root + relative);

    private int PermissionCalls => Calls("/permissions/check");

    private static string Key(string permission, string issue) => $"atl:jira:{CloudId}:{Account}:{permission}:{issue}";

    [Fact]
    public async Task Check_BrowseAllowed_IssueAllowed()
    {
        _jira.Browse(Account, "10001");

        (await CheckAsync(Issue("10001"))).Should().Equal(true);

        _jira.PermissionChecks.Should().Equal(
            """{"accountId":"557058:abc","projectPermissions":[{"permissions":["BROWSE_PROJECTS"],"issues":[10001]}]}""");
        _cache.TtlOf(Key("BROWSE_PROJECTS", "10001")).Should().Be(JiraPermissionChecker.AllowTtl);
    }

    [Fact]
    public async Task Check_IssueAbsentFromAnswer_Denied()
    {
        _jira.Browse(Account, "10001");

        (await CheckAsync(Issue("10001"), Issue("10002"))).Should().Equal(true, false);

        _cache.TtlOf(Key("BROWSE_PROJECTS", "10002")).Should().Be(JiraPermissionChecker.AllowTtl);
        (await CheckAsync(Issue("10002"))).Should().Equal(false);
        PermissionCalls.Should().Be(1, "the definite no is cached");
    }

    [Fact]
    public async Task Check_1001Issues_TwoCalls()
    {
        string[] ids = [.. Enumerable.Range(1, 1001).Select(i => (10000 + i).ToString())];
        _jira.Browse(Account, ids);

        bool[] result = await CheckAsync([.. ids.Select(Issue)]);

        result.Should().HaveCount(1001).And.OnlyContain(allowed => allowed);
        PermissionCalls.Should().Be(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Check_EmptyAccount_NoCallAllDenied(string account)
    {
        _jira.Browse(account, "10001");

        (await CheckAsync(account, Issue("10001"), Issue("10002"))).Should().Equal(false, false);
        _api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("ANONYMOUS")]
    [InlineData(" Anonymous ")]
    public async Task Check_AnonymousAccount_NoCallAllDenied(string account)
    {
        _jira.Browse(account, "10001");

        (await CheckAsync(account, Issue("10001"))).Should().Equal(false);
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Verify_AccountUnknownToJira_DeniesAll()
    {
        // Jira answers 200 with empty issue lists for an account it does not know.
        _jira.Browse("someone-else", "10001", "10002");

        (await CheckAsync(Issue("10001"), Issue("10002"))).Should().Equal(false, false);
        PermissionCalls.Should().Be(1);
    }

    [Fact]
    public async Task Check_NonGuidCloudId_NoCallAllDenied()
    {
        _jira.Browse(Account, "10001");

        (await _checker.CanSeeAsync("not-a-guid", Account, [Issue("10001")], Sites(), default)).Should().Equal(false);
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Check_GroupMember_CommentAllowed()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));

        (await CheckAsync(Comment("10001", restriction: Group(GroupA)))).Should().Equal(true);
    }

    [Fact]
    public async Task Check_GroupNonMember_CommentDenied()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("support", GroupB));

        (await CheckAsync(Issue("10001"), Comment("10001", restriction: Group(GroupA)))).Should().Equal(true, false);
    }

    [Fact]
    public async Task Check_GroupMatchedById_NotName()
    {
        _jira.Browse(Account, "10001");
        // GroupA was renamed; the user's other group happens to be named like GroupB's id.
        _jira.Groups(Account, ("engineering-renamed", GroupA.ToUpperInvariant()), (GroupB, GroupC));

        (await CheckAsync(Comment("10001", restriction: Group(GroupA)), Comment("10001", restriction: Group(GroupB))))
            .Should().Equal(true, false);
    }

    [Fact]
    public async Task Check_AllZeroGroup_NeverMatches()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("odd", Guid.Empty.ToString("D")));

        (await CheckAsync(Comment("10001", restriction: JiraRestriction.DenyAll))).Should().Equal(false);
    }

    [Fact]
    public async Task Check_RoleDirectUser_Allowed()
    {
        _jira.Browse(Account, "10001");
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor(Account));
        _jira.Role(Project, "10003", "Administrators");

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(true);
        Calls($"/project/{Project}/role/10003").Should().Be(0, "only the named role's actors are read");
    }

    [Fact]
    public async Task Check_RoleViaGroupActor_Allowed()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor("someone-else"), FakeJira.GroupActor(GroupA.ToUpperInvariant()));

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(true);
    }

    [Fact]
    public async Task Check_RoleNotAMember_Denied()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("support", GroupB));
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor("someone-else"), FakeJira.GroupActor(GroupA));

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(false);
    }

    [Fact]
    public async Task Check_RoleNameAmbiguous_Denied()
    {
        _jira.Browse(Account, "10001");
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor(Account));
        _jira.Role(Project, "10003", "Developers", FakeJira.UserActor(Account));

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(false);
    }

    [Fact]
    public async Task Check_RoleMissing_Denied()
    {
        _jira.Browse(Account, "10001");
        _jira.Role(Project, "10003", "Administrators", FakeJira.UserActor(Account));

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(false);
    }

    [Fact]
    public async Task Check_RoleNameCaseDiffers_Denied()
    {
        _jira.Browse(Account, "10001");
        _jira.Role(Project, "10002", "developers", FakeJira.UserActor(Account));

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(false);
    }

    [Fact]
    public async Task Check_RoleNameWithPathCharacters_NeverInARequestPath()
    {
        _jira.Browse(Account, "10001");
        _jira.Role(Project, "10002", "../x/y", FakeJira.UserActor(Account));

        (await CheckAsync(Comment("10001", restriction: Role("../x/y")))).Should().Equal(true);
        _api.Requests.Should().NotContain(u => u.OriginalString.Contains("x/y") || u.OriginalString.Contains("x%2F"));
    }

    [Fact]
    public async Task Check_RoleUrlWithTrailingSlash_Allowed()
    {
        _jira.Browse(Account, "10001");
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor(Account));
        _api.MapJson(_jira.Root + $"/project/{Project}/role", new Dictionary<string, string>
        {
            ["Developers"] = $"https://api.atlassian.com/ex/jira/{CloudId}/rest/api/3/project/{Project}/role/10002/",
        });

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(true);
    }

    [Fact]
    public async Task Check_NineteenDigitRoleId_Read()
    {
        const string longId = "1234567890123456789";
        _jira.Browse(Account, "10001");
        _jira.Role(Project, longId, "Developers", FakeJira.UserActor(Account));

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(true);
        Calls($"/project/{Project}/role/{longId}").Should().Be(1);
    }

    [Theory]
    [InlineData("https://api.atlassian.com/ex/jira/c/rest/api/3/project/10000/role/10002", "10002")]
    [InlineData("https://api.atlassian.com/ex/jira/c/rest/api/3/project/10000/role/10002/", "10002")]
    [InlineData("https://api.atlassian.com/ex/jira/c/rest/api/3/project/10000/role/10002 ", "10002")]
    [InlineData("https://x/role/1234567890123456789", "1234567890123456789")]
    [InlineData("https://x/role/12345678901234567890", null)]
    [InlineData("https://x/role/..", null)]
    [InlineData("https://x/role/", null)]
    [InlineData("https://x/role/１２", null)]
    [InlineData("", null)]
    public void RoleIdOf_TakesTheTrailingDigitsOnly(string url, string? id) =>
        JiraPermissionChecker.RoleIdOf(url).Should().Be(id);

    [Fact]
    public async Task Check_RoleUrlNotNumeric_Denied()
    {
        _jira.Browse(Account, "10001");
        _jira.Role(Project, "..", "Developers", FakeJira.UserActor(Account));

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(false);
    }

    [Fact]
    public async Task Check_InternalNote_AgentAllowed()
    {
        _jira.Browse(Account, "10001");
        _jira.Agent(Account, "10001");

        (await CheckAsync(Comment("10001", internalNote: true))).Should().Equal(true);

        _jira.PermissionChecks.Should().Equal(
            """{"accountId":"557058:abc","projectPermissions":[{"permissions":["BROWSE_PROJECTS"],"issues":[10001]},{"permissions":["SERVICEDESK_AGENT"],"issues":[10001]}]}""");
    }

    [Fact]
    public async Task Check_InternalNote_CustomerDenied()
    {
        _jira.Browse(Account, "10001");

        (await CheckAsync(Issue("10001"), Comment("10001", internalNote: true))).Should().Equal(true, false);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public async Task Check_InternalNoteWithGroup_NeedsAllThree(bool browse, bool agent, bool member, bool expected)
    {
        if (browse) _jira.Browse(Account, "10001");
        if (agent) _jira.Agent(Account, "10001");
        _jira.Groups(Account, ("engineering", member ? GroupA : GroupB));

        (await CheckAsync(Comment("10001", internalNote: true, restriction: Group(GroupA)))).Should().Equal(expected);
    }

    [Fact]
    public async Task Check_RestrictedButNoBrowse_Denied()
    {
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor(Account));

        (await CheckAsync(Comment("10001", restriction: Group(GroupA)), Comment("10001", restriction: Role("Developers"))))
            .Should().Equal(false, false);
    }

    [Fact]
    public async Task Check_Forbidden_AllDeniedAndCachedBriefly()
    {
        _jira.Answer("/permissions/check", HttpStatusCode.Forbidden);

        (await CheckAsync(Issue("10001"), Comment("10001", internalNote: true))).Should().Equal(false, false);

        _cache.TtlOf(Key("BROWSE_PROJECTS", "10001")).Should().Be(JiraPermissionChecker.FailureTtl);
        _cache.TtlOf(Key("SERVICEDESK_AGENT", "10001")).Should().Be(JiraPermissionChecker.FailureTtl);
    }

    [Fact]
    public async Task Check_RateLimited_AllDenied()
    {
        _jira.Browse(Account, "10001");
        _api.RateLimitNext = true;

        (await CheckAsync(Issue("10001"))).Should().Equal(false);
        (await CheckAsync(Issue("10001"))).Should().Equal(new[] { false }, "the failure is cached, not retried at once");

        _cache.TtlOf(Key("BROWSE_PROJECTS", "10001")).Should().Be(JiraPermissionChecker.FailureTtl);
        PermissionCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("""{"projectPermissions":[{"permission":"BROWSE_PROJECTS","issues":["10001"]}]}""")]
    [InlineData("""{"projectPermissions":{"permission":"BROWSE_PROJECTS","issues":[10001]}}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public async Task Check_MalformedAnswer_Denied(string json)
    {
        _api.Map(_jira.Root + "/permissions/check", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        (await CheckAsync(Issue("10001"))).Should().Equal(false);
    }

    [Fact]
    public async Task Check_GroupsCallFails_RestrictedDeniedPublicStillAllowed()
    {
        _jira.Browse(Account, "10001");
        _jira.Answer("/user/groups", HttpStatusCode.InternalServerError);
        _jira.Role(Project, "10002", "Developers", FakeJira.GroupActor(GroupA));

        (await CheckAsync(Issue("10001"), Comment("10001", restriction: Group(GroupA)), Comment("10001", restriction: Role("Developers"))))
            .Should().Equal(true, false, false);
        _cache.TtlOf($"atl:jira:{CloudId}:{Account}:groups").Should().Be(JiraPermissionChecker.FailureTtl);
    }

    [Fact]
    public async Task Check_RoleCallFails_Denied()
    {
        _jira.Browse(Account, "10001");
        _jira.Answer($"/project/{Project}/role", HttpStatusCode.InternalServerError);

        (await CheckAsync(Comment("10001", restriction: Role("Developers")))).Should().Equal(false);
        _cache.TtlOf($"atl:jira:{CloudId}:roles:{Project}").Should().Be(JiraPermissionChecker.FailureTtl);
    }

    [Fact]
    public async Task Check_CacheHit_NoSecondCall()
    {
        _jira.Browse(Account, "10001");
        _jira.Agent(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor(Account));
        JiraResource[] resources =
        [
            Issue("10001"), Comment("10001", internalNote: true, restriction: Group(GroupA)),
            Comment("10001", restriction: Role("Developers")),
        ];

        (await CheckAsync(resources)).Should().Equal(true, true, true);
        (await CheckAsync(resources)).Should().Equal(true, true, true);

        PermissionCalls.Should().Be(1);
        Calls("/user/groups").Should().Be(1);
        Calls($"/project/{Project}/role").Should().Be(1);
        Calls($"/project/{Project}/role/10002").Should().Be(1);
    }

    [Fact]
    public async Task Lookup_SameCloudBothProducts_EachCheckerGetsItsOwn()
    {
        // The Jira connection is listed first, so a lookup on the cloud id alone would hand Confluence its credential.
        var store = Substitute.For<IConnectionStore>();
        Connection jira = SiteConnection("jira-client", ",\"product\":\"jira\""), confluence = SiteConnection("confluence-client", "");
        store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([jira, confluence]);
        store.GetSecretAsync(jira.Id, Arg.Any<CancellationToken>()).Returns("jira-secret");
        store.GetSecretAsync(confluence.Id, Arg.Any<CancellationToken>()).Returns("confluence-secret");
        var sites = NewChecker(_api, _cache, store).NewSiteLookup();

        var forConfluence = await sites.SiteForAsync(CloudId, AtlassianProduct.Confluence, default);
        var forJira = await sites.SiteForAsync(CloudId, AtlassianProduct.Jira, default);

        forConfluence!.Value.Site.Should().Be(new AtlassianSite("https://acme.atlassian.net", CloudId, "confluence-client"));
        forConfluence.Value.Secret.Should().Be("confluence-secret");
        forJira!.Value.Site.Should().Be(new AtlassianSite("https://acme.atlassian.net", CloudId, "jira-client", AtlassianProduct.Jira));
        forJira.Value.Secret.Should().Be("jira-secret");

        // A site with only a Confluence connection has nothing for the Jira checker, which denies without asking.
        _jira.Browse(Account, "10001");
        var confluenceOnly = NewChecker(_api, _cache, AtlassianConnectionStore()).NewSiteLookup();
        (await confluenceOnly.SiteForAsync(CloudId, AtlassianProduct.Jira, default)).Should().BeNull();
        (await _checker.CanSeeAsync(CloudId, Account, [Issue("10001")], confluenceOnly, default)).Should().Equal(false);
        PermissionCalls.Should().Be(0);
    }

    private static Connection SiteConnection(string clientId, string product) => new(
        Guid.NewGuid(), clientId, ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"{{clientId}}"{{product}}}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);

    [Fact]
    public async Task Check_CacheKeyIncludesProduct()
    {
        _api.MapJson(CheckPath, new { hasPermission = true });
        (await NewChecker(_api, _cache, AtlassianConnectionStore()).CanReadAsync(CloudId, "42", Account, default))
            .Should().BeTrue();

        (await CheckAsync(Issue("42"))).Should().Equal(new[] { false }, "a Confluence answer for content 42 says nothing about Jira issue 42");
        PermissionCalls.Should().Be(1);
    }

    [Fact]
    public async Task Check_CallerCancelled_Throws()
    {
        _jira.Browse(Account, "10001");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => _checker.CanSeeAsync(CloudId, Account, [Issue("10001")], Sites(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        _cache.TtlOf(Key("BROWSE_PROJECTS", "10001")).Should().BeNull();
    }

    // ── Restricted comments: Jira's live visibility must still be the URI's ──────────

    [Fact]
    public async Task Check_LiveRestrictionUnchanged_Allowed()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor(Account));
        _jira.LiveComment("10001", "501", new { type = "group", value = "engineering", identifier = GroupA.ToUpperInvariant() });
        _jira.LiveComment("10001", "502", new { type = "role", value = "Developers" });

        (await CheckAsync(Comment("10001", restriction: Group(GroupA), id: "501"), Comment("10001", restriction: Role("Developers"), id: "502")))
            .Should().Equal(true, true);

        Calls("/issue/10001/comment/501").Should().Be(1);
        Calls("/issue/10001/comment/502").Should().Be(1);
        _cache.TtlOf(LiveKeyPrefix + "10001:501").Should().Be(JiraPermissionChecker.LiveCommentTtl);
        JiraPermissionChecker.LiveCommentTtl.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Check_LiveGroupChanged_Denied()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA), ("support", GroupB));
        _jira.LiveComment("10001", "501", new { type = "group", value = "support", identifier = GroupB });

        (await CheckAsync(Comment("10001", restriction: Group(GroupA), id: "501"))).Should().Equal(false);
        _cache.TtlOf(LiveKeyPrefix + "10001:501").Should().Be(JiraPermissionChecker.LiveCommentTtl, "Jira's answer was definite");
    }

    [Fact]
    public async Task Check_LiveRoleRenamedOrGroupNowRole_Denied()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor(Account));
        _jira.LiveComment("10001", "501", new { type = "role", value = "Administrators" });
        _jira.LiveComment("10001", "502", new { type = "role", value = "Developers" });

        (await CheckAsync(Comment("10001", restriction: Role("Developers"), id: "501"), Comment("10001", restriction: Group(GroupA), id: "502")))
            .Should().Equal(false, false);
    }

    [Fact]
    public async Task Check_LiveNowPublic_Denied()
    {
        // A public comment lives in the issue document, so its own document is no longer the one to show.
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.LiveComment("10001", "501");

        (await CheckAsync(Comment("10001", restriction: Group(GroupA), id: "501"))).Should().Equal(false);
    }

    [Theory]
    [InlineData(true, true)]   // an internal note made public to customers
    [InlineData(false, false)] // a group comment now an internal note too
    public async Task Check_LiveInternalFlagChanged_Denied(bool uriInternal, bool liveJsdPublic)
    {
        _jira.Browse(Account, "10001");
        _jira.Agent(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.LiveComment("10001", "501", new { type = "group", identifier = GroupA }, liveJsdPublic);

        (await CheckAsync(Comment("10001", internalNote: uriInternal, restriction: Group(GroupA), id: "501"))).Should().Equal(false);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Check_LiveReadFails_DeniedAndCachedBriefly(HttpStatusCode status)
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.LiveCommentFails("10001", "501", status);

        (await CheckAsync(Issue("10001"), Comment("10001", restriction: Group(GroupA), id: "501"))).Should().Equal(true, false);
        _cache.TtlOf(LiveKeyPrefix + "10001:501").Should().Be(JiraPermissionChecker.FailureTtl);
    }

    [Fact]
    public async Task Check_LiveReadTimesOut_Denied()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.LiveComment("10001", "501", new { type = "group", identifier = GroupA });
        _api.Map(_jira.Root + "/issue/10001/comment/501", _ => throw new TaskCanceledException("timed out", new TimeoutException()));

        (await CheckAsync(Comment("10001", restriction: Group(GroupA), id: "501"))).Should().Equal(false);
        _cache.TtlOf(LiveKeyPrefix + "10001:501").Should().Be(JiraPermissionChecker.FailureTtl);
    }

    [Theory]
    [InlineData("""not json""")]
    [InlineData("""[]""")]
    [InlineData("""{"id":"999","visibility":{"type":"group","identifier":"6e87dc72-4f1f-421f-9382-2fee8b652487"}}""")]
    [InlineData("""{"id":"501","visibility":"group"}""")]
    [InlineData("""{"id":"501","visibility":{"type":"project","identifier":"6e87dc72-4f1f-421f-9382-2fee8b652487"}}""")]
    [InlineData("""{"id":"501","visibility":{"type":"group"}}""")]
    public async Task Check_LiveAnswerUnreadable_Denied(string json)
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.LiveComment("10001", "501");
        _api.Map(_jira.Root + "/issue/10001/comment/501", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        (await CheckAsync(Comment("10001", restriction: Group(GroupA), id: "501"))).Should().Equal(false);
    }

    [Fact]
    public async Task Check_LiveRouteCached_SharedAcrossUsers()
    {
        const string other = "557058:other";
        _jira.Browse(Account, "10001");
        _jira.Browse(other, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.Groups(other, ("engineering", GroupA));
        _jira.LiveComment("10001", "501", new { type = "group", identifier = GroupA });
        JiraResource comment = Comment("10001", restriction: Group(GroupA), id: "501");

        (await CheckAsync(comment)).Should().Equal(true);
        (await CheckAsync(comment)).Should().Equal(true);
        (await CheckAsync(other, comment)).Should().Equal(true);

        Calls("/issue/10001/comment/501").Should().Be(1, "the live route does not depend on who searches");
    }

    [Fact]
    public async Task Check_LiveReadFailureCached_NotRetriedAtOnce()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        _jira.LiveCommentFails("10001", "501", HttpStatusCode.InternalServerError);
        JiraResource comment = Comment("10001", restriction: Group(GroupA), id: "501");

        (await CheckAsync(comment)).Should().Equal(false);
        _jira.LiveComment("10001", "501", new { type = "group", identifier = GroupA });
        (await CheckAsync(comment)).Should().Equal(false);

        Calls("/issue/10001/comment/501").Should().Be(1);
    }

    [Fact]
    public async Task Check_IssuesAndAttachments_NoCommentRead()
    {
        _jira.Browse(Account, "10001", "10002");
        _jira.Groups(Account, ("engineering", GroupA));

        (await CheckAsync(
                Issue("10001"), Issue("10002"),
                new JiraResource(CloudId, "10001", JiraResourceKind.Attachment, "30001", false, Group(GroupA)),
                new JiraResource(CloudId, "10001", JiraResourceKind.Attachment, "30002", false, null)))
            .Should().Equal(true, true, true, true);

        CommentReads.Should().Be(0);
    }

    [Fact]
    public async Task Check_CommentDeniedByBrowse_NoCommentRead()
    {
        _jira.Groups(Account, ("engineering", GroupA));

        (await CheckAsync(Comment("10001", restriction: Group(GroupA)))).Should().Equal(false);

        CommentReads.Should().Be(0);
    }

    [Fact]
    public async Task Check_ManyComments_ReadConcurrentlyAndEachOnce()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        var resources = new List<JiraResource>();
        for (int i = 0; i < 25; i++)
        {
            string id = (600 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _jira.LiveComment("10001", id, new { type = "group", identifier = GroupA });
            resources.Add(Comment("10001", restriction: Group(GroupA), id: id));
        }
        resources.Add(resources[0]);

        (await CheckAsync([.. resources])).Should().HaveCount(26).And.OnlyContain(allowed => allowed);

        CommentReads.Should().Be(25);
    }

    // ── Role-restricted comments: the issue must still be in the role's project ──────────

    private void RoleMember()
    {
        _jira.Browse(Account, "10001");
        _jira.Role(Project, "10002", "Developers", FakeJira.UserActor(Account));
        _jira.LiveComment("10001", "501", new { type = "role", value = "Developers" });
    }

    private const string IssueKey = $"atl:jira:{CloudId}:issue-project:10001";

    [Fact]
    public async Task Check_RoleCommentIssueInSameProject_Allowed()
    {
        RoleMember();
        _jira.LiveIssueProject("10001", Project);

        (await CheckAsync(Comment("10001", restriction: Role("Developers"), id: "501"))).Should().Equal(true);

        Calls("/issue/10001").Should().Be(1);
        _api.Requests.Should().Contain(u => u.AbsolutePath.EndsWith("/issue/10001") && u.Query == "?fields=project");
        _cache.TtlOf(IssueKey).Should().Be(JiraPermissionChecker.LiveCommentTtl);
    }

    [Fact]
    public async Task Check_RoleCommentIssueMovedToOtherProject_Denied()
    {
        // Same role name in the new project, but a different rule: the URI's role is the old project's.
        RoleMember();
        _jira.LiveIssueProject("10001", "10099");

        (await CheckAsync(Comment("10001", restriction: Role("Developers"), id: "501"))).Should().Equal(false);
        _cache.TtlOf(IssueKey).Should().Be(JiraPermissionChecker.LiveCommentTtl, "Jira's answer was definite");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Check_RoleCommentIssueReadFails_DeniedAndCachedBriefly(HttpStatusCode status)
    {
        RoleMember();
        _jira.LiveIssueFails("10001", status);

        (await CheckAsync(Issue("10001"), Comment("10001", restriction: Role("Developers"), id: "501"))).Should().Equal(true, false);
        _cache.TtlOf(IssueKey).Should().Be(JiraPermissionChecker.FailureTtl);
    }

    [Theory]
    [InlineData("""not json""")]
    [InlineData("""{"id":"10001","fields":{}}""")]
    [InlineData("""{"id":"10001","fields":{"project":{"id":10000}}}""")]
    [InlineData("""{"id":"10001","fields":{"project":"10000"}}""")]
    [InlineData("""[]""")]
    public async Task Check_RoleCommentIssueAnswerUnreadable_Denied(string json)
    {
        RoleMember();
        _jira.LiveIssueProject("10001", Project);
        _api.Map(_jira.Root + "/issue/10001", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        (await CheckAsync(Comment("10001", restriction: Role("Developers"), id: "501"))).Should().Equal(false);
    }

    [Fact]
    public async Task Check_RoleCommentIssueProject_CachedOnRepeat()
    {
        RoleMember();
        _jira.LiveComment("10001", "502", new { type = "role", value = "Developers" });
        _jira.LiveIssueProject("10001", Project);

        (await CheckAsync(Comment("10001", restriction: Role("Developers"), id: "501"),
            Comment("10001", restriction: Role("Developers"), id: "502"))).Should().Equal(true, true);
        (await CheckAsync(Comment("10001", restriction: Role("Developers"), id: "501"))).Should().Equal(true);

        Calls("/issue/10001").Should().Be(1, "one read per issue, then the cache");
    }

    [Fact]
    public async Task Check_GroupInternalAndIssueHits_NoIssueProjectRead()
    {
        _jira.Browse(Account, "10001");
        _jira.Agent(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));

        (await CheckAsync(Issue("10001"), Comment("10001", restriction: Group(GroupA)), Comment("10001", internalNote: true)))
            .Should().Equal(true, true, true);

        Calls("/issue/10001").Should().Be(0);
    }

    [Fact]
    public async Task Check_CallerCancelledDuringCommentRead_Throws()
    {
        _jira.Browse(Account, "10001");
        _jira.Groups(Account, ("engineering", GroupA));
        using var cts = new CancellationTokenSource();
        _api.Map(_jira.Root + "/issue/10001/comment/501", _ =>
        {
            cts.Cancel();
            return FakeAtlassianApi.Json(new { id = "501", visibility = new { type = "group", identifier = GroupA } });
        });

        await FluentActions.Awaiting(() => _checker.CanSeeAsync(
                CloudId, Account, [Comment("10001", restriction: Group(GroupA), id: "501")], Sites(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        _cache.TtlOf(LiveKeyPrefix + "10001:501").Should().BeNull();
    }
}
