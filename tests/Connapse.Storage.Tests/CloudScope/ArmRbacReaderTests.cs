using System.Net;
using System.Text;
using Azure.Core;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class ArmRbacReaderTests
{
    private const string Oid = "11111111-1111-1111-1111-111111111111";
    private const string Sub = "22222222-2222-2222-2222-222222222222";
    private const string ReaderRole = "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1";

    // Records every request URL so tests can assert which ARM endpoints were called.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) =>
            new("stub", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) =>
            new(GetToken(c, ct));
    }

    private static HttpResponseMessage Json(HttpStatusCode s, string body) =>
        new(s) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string RoleAssignmentsBody(params (string roleGuid, string scope, string? condition)[] rows)
    {
        string items = string.Join(",", rows.Select(r =>
        {
            string cond = r.condition is null ? "null" : $"\"{r.condition.Replace("\"", "\\\"")}\"";
            return $$$"""
            {"properties":{"roleDefinitionId":"/subscriptions/{{{Sub}}}/providers/Microsoft.Authorization/roleDefinitions/{{{r.roleGuid}}}","principalId":"{{{Oid}}}","scope":"{{{r.scope}}}","condition":{{{cond}}},"conditionVersion":null}}
            """;
        }));
        return $$"""{"value":[{{items}}]}""";
    }

    private static readonly string EmptyDeny = """{"value":[]}""";

    // A reader whose roleAssignments call returns `roles` and whose denyAssignments call returns empty.
    private static ArmRbacReader NewReader(string rolesBody, string? subId = Sub, IMemoryCache? cache = null)
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.OK, EmptyDeny)
                : Json(HttpStatusCode.OK, rolesBody));
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = subId });
        return new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            cache ?? new MemoryCache(new MemoryCacheOptions()), opts);
    }

    [Fact]
    public async Task Resolve_AccountScopedReaderRole_NoCondition_YieldsAccountPrefix()
    {
        string body = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", null));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.Outcome.Should().Be(RbacOutcome.Resolved);
        r.ReadablePrefixes.Select(p => p.Prefix).Should().Contain("azblob://acct/");
    }

    [Fact]
    public async Task Resolve_IgnoresNonBlobDataRoles()
    {
        // Owner of the *control plane* role "Contributor" for ARM is a different GUID; use a random one.
        string body = RoleAssignmentsBody(("00000000-0000-0000-0000-000000000000",
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", null));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.Outcome.Should().Be(RbacOutcome.Resolved);
        r.ReadablePrefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_PathCondition_NarrowsPrefix()
    {
        string cond = "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'} AND NOT SubOperationMatches{'Blob.List'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike 'readonly/*'))";
        string body = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs", cond));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Select(p => p.Prefix).Should().Contain("azblob://acct/docs/readonly/");
    }

    [Fact]
    public async Task Resolve_TagCondition_GoesToTagResidue_NotPrefix()
    {
        string cond = "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags:Project<$key_case_sensitive$>] StringEquals 'Cascade'))";
        string body = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs", cond));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Should().BeEmpty();
        r.TagConditioned.Should().ContainSingle().Which.TagValue.Should().Be("Cascade");
    }

    [Fact]
    public async Task Resolve_UnparseableCondition_DropsThatGrantOnly()
    {
        string bad = "((!(ActionMatches{'x'})) OR (@Request[foo] DateTimeGreaterThan '2024-01-01'))";
        string body = RoleAssignmentsBody(
            (ReaderRole, "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/bad", bad),
            (ReaderRole, "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/good", null));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Select(p => p.Prefix).Should().ContainSingle().Which.Should().Be("azblob://acct/good/");
    }

    [Fact]
    public async Task Resolve_NoSubscriptionConfigured_FailsClosed()
    {
        AzureRbacScopes r = await NewReader(RoleAssignmentsBody(), subId: null).ResolveAsync(Oid, CancellationToken.None);
        r.Outcome.Should().Be(RbacOutcome.Failed);
    }

    // (Subscription-scope / no-atScope URL shape is asserted in
    // Resolve_Resolved_IsCached_SecondCallDoesNotHitArm, which captures request URLs.)

    // Build a reader whose deny call returns `denyBody`.
    private static ArmRbacReader NewReaderWithDeny(string rolesBody, string denyBody, string? subId = Sub)
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.OK, denyBody)
                : Json(HttpStatusCode.OK, rolesBody));
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = subId });
        return new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            new MemoryCache(new MemoryCacheOptions()), opts);
    }

    private static string DenyBody(string scope) => $$$"""
    {"value":[{"properties":{"scope":"{{{scope}}}","permissions":[{"dataActions":["Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read"],"notDataActions":[]}]}}]}
    """;

    [Fact]
    public async Task Resolve_DenyCoveringGrant_RemovesIt()
    {
        string acctScope = "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct";
        string roles = RoleAssignmentsBody((ReaderRole, acctScope + "/blobServices/default/containers/docs", null));
        // Deny at the whole account covers the container grant.
        AzureRbacScopes r = await NewReaderWithDeny(roles, DenyBody(acctScope)).ResolveAsync(Oid, CancellationToken.None);

        r.Outcome.Should().Be(RbacOutcome.Resolved);
        r.ReadablePrefixes.Should().BeEmpty(); // deny wins
    }

    [Fact]
    public async Task Resolve_DenyElsewhere_DoesNotRemoveUnrelatedGrant()
    {
        string roles = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs", null));
        string denyOther = DenyBody("/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/other");
        AzureRbacScopes r = await NewReaderWithDeny(roles, denyOther).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Select(p => p.Prefix).Should().ContainSingle().Which.Should().Be("azblob://acct/docs/");
    }

    [Fact]
    public async Task Resolve_DenyCallFails_FailsClosed()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.InternalServerError, "{}")
                : Json(HttpStatusCode.OK, RoleAssignmentsBody((ReaderRole,
                    "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", null))));
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = Sub });
        var reader = new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            new MemoryCache(new MemoryCacheOptions()), opts);

        (await reader.ResolveAsync(Oid, CancellationToken.None)).Outcome.Should().Be(RbacOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_Resolved_IsCached_SecondCallDoesNotHitArm()
    {
        string roles = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", null));
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.OK, EmptyDeny) : Json(HttpStatusCode.OK, roles));
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = Sub });
        var reader = new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            new MemoryCache(new MemoryCacheOptions()), opts);

        await reader.ResolveAsync(Oid, CancellationToken.None);
        int after = handler.Urls.Count;
        await reader.ResolveAsync(Oid, CancellationToken.None);

        handler.Urls.Count.Should().Be(after); // served from cache
        handler.Urls.Should().Contain(u => u.Contains("/roleAssignments") && u.Contains("assignedTo") && !u.Contains("atScope"));
        handler.Urls.Should().Contain(u => u.Contains("/subscriptions/" + Sub + "/"));
    }

    [Fact]
    public async Task Resolve_DenyNarrowerThanGrant_DropsTheWholeGrant()
    {
        // Account-scoped grant with a container-scoped deny beneath it. The prefix model cannot
        // represent "account minus one container", so the whole grant is dropped (deny wins;
        // over-subtraction = under-grant, the fail-closed direction). Never leave the container
        // readable through the broad grant.
        string acctScope = "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct";
        string roles = RoleAssignmentsBody((ReaderRole, acctScope, null)); // whole account
        string deny = DenyBody(acctScope + "/blobServices/default/containers/secret"); // one container
        AzureRbacScopes r = await NewReaderWithDeny(roles, deny).ResolveAsync(Oid, CancellationToken.None);

        r.Outcome.Should().Be(RbacOutcome.Resolved);
        r.ReadablePrefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_ContainerNameCondition_OnBroaderThanAccountScope_DropsGrant()
    {
        // A container-name ABAC condition on a subscription-scoped grant cannot be reduced to a
        // prefix (the account is unknown), so it must drop — not resolve to azblob:// (all accounts).
        string cond = "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEquals 'reports'))";
        string roles = RoleAssignmentsBody((ReaderRole, "/subscriptions/" + Sub, cond)); // subscription scope
        AzureRbacScopes r = await NewReader(roles).ResolveAsync(Oid, CancellationToken.None);

        r.Outcome.Should().Be(RbacOutcome.Resolved);
        r.ReadablePrefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_CompoundCondition_PathAndTag_IsDroppedNotPartiallyHonored()
    {
        // A compound condition (path AND tag) must not be partially honored — the path restriction
        // would be lost if only the tag matched. More than one attribute reference → unparseable → drop.
        string cond = "((!(ActionMatches{'x'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike 'readonly/*' AND @Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags:Secret<$key_case_sensitive$>] StringEquals 'no'))";
        string roles = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs", cond));
        AzureRbacScopes r = await NewReader(roles).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Should().BeEmpty();
        r.TagConditioned.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_RoleAssignmentsCallFails_FailsClosed()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/roleAssignments", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.InternalServerError, "{}")
                : Json(HttpStatusCode.OK, EmptyDeny));
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = Sub });
        var reader = new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            new MemoryCache(new MemoryCacheOptions()), opts);

        (await reader.ResolveAsync(Oid, CancellationToken.None)).Outcome.Should().Be(RbacOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_InternalTimeout_TokenNotCancelled_FailsClosed()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("http timeout"));
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = Sub });
        var reader = new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            new MemoryCache(new MemoryCacheOptions()), opts);

        (await reader.ResolveAsync(Oid, CancellationToken.None)).Outcome.Should().Be(RbacOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_CallerCancellation_IsRethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = Sub });
        var reader = new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            new MemoryCache(new MemoryCacheOptions()), opts);

        Func<Task> act = () => reader.ResolveAsync(Oid, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Resolve_PathCondition_OnAccountScope_IsDropped()
    {
        // A blob-path condition on an account scope can't be expressed as a prefix (it applies
        // inside every container, not a container named for the path) → drop, not azblob://acct/<path>/.
        string cond = "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike 'readonly/*'))";
        string roles = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", cond));
        AzureRbacScopes r = await NewReader(roles).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_ContainerNameCondition_OnAccountScope_NarrowsToNamedContainer()
    {
        string cond = "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEquals 'reports'))";
        string roles = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", cond));
        AzureRbacScopes r = await NewReader(roles).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Select(p => p.Prefix).Should().ContainSingle().Which.Should().Be("azblob://acct/reports/");
    }

    [Fact]
    public async Task Resolve_ContainerNameCondition_NamingDifferentContainerThanScope_IsDropped()
    {
        // Assignment scoped to container "A" but condition names container "B" → Azure grants
        // nothing (A != B); must NOT emit azblob://acct/B/ (would escape the assignment scope).
        string cond = "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEquals 'B'))";
        string roles = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/A", cond));
        AzureRbacScopes r = await NewReader(roles).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_ContainerNameCondition_MatchingScopeContainer_IsKept()
    {
        string cond = "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEquals 'docs'))";
        string roles = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs", cond));
        AzureRbacScopes r = await NewReader(roles).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Select(p => p.Prefix).Should().ContainSingle().Which.Should().Be("azblob://acct/docs/");
    }

    [Fact]
    public async Task Resolve_MalformedDenyPage_NullValue_FailsClosed()
    {
        string roles = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", null));
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.OK, """{"value":null}""")
                : Json(HttpStatusCode.OK, roles));
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = Sub });
        var reader = new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            new MemoryCache(new MemoryCacheOptions()), opts);

        (await reader.ResolveAsync(Oid, CancellationToken.None)).Outcome.Should().Be(RbacOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_DenyWithBlobReadInNotDataActions_DoesNotSubtractGrant()
    {
        // A deny that grants dataActions:["*"] but excludes blob read via notDataActions does NOT
        // deny blob reads → the grant must survive (ignoring notDataActions would over-subtract).
        string acctScope = "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct";
        string roles = RoleAssignmentsBody((ReaderRole, acctScope, null));
        string deny = $$$"""
        {"value":[{"properties":{"scope":"{{{acctScope}}}","permissions":[{"dataActions":["*"],"notDataActions":["Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read"]}]}}]}
        """;
        AzureRbacScopes r = await NewReaderWithDeny(roles, deny).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Select(p => p.Prefix).Should().ContainSingle().Which.Should().Be("azblob://acct/");
    }

    [Fact]
    public async Task Resolve_CacheKey_IncludesSubscription_NotReusedAcrossSubscriptionChange()
    {
        // Same reader + cache, but the subscription changes between calls (settings reload). The
        // second call must NOT reuse the first subscription's result.
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase))
                return Json(HttpStatusCode.OK, EmptyDeny);
            // Grant an account named after whichever subscription is in the request path.
            string sub = req.RequestUri.AbsolutePath.Split("/subscriptions/")[1].Split('/')[0];
            return Json(HttpStatusCode.OK, $$$"""
            {"value":[{"properties":{"roleDefinitionId":"/x/{{{ReaderRole}}}","scope":"/subscriptions/{{{sub}}}/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct-{{{sub}}}","condition":null}}]}
            """);
        });
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(
            new AzureProviderSettings { SubscriptionId = "sub-a" },
            new AzureProviderSettings { SubscriptionId = "sub-b" });
        var reader = new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            new MemoryCache(new MemoryCacheOptions()), opts);

        AzureRbacScopes a = await reader.ResolveAsync(Oid, CancellationToken.None);
        AzureRbacScopes b = await reader.ResolveAsync(Oid, CancellationToken.None);

        a.ReadablePrefixes.Select(p => p.Prefix).Should().ContainSingle().Which.Should().Be("azblob://acct-sub-a/");
        b.ReadablePrefixes.Select(p => p.Prefix).Should().ContainSingle().Which.Should().Be("azblob://acct-sub-b/");
    }
}
