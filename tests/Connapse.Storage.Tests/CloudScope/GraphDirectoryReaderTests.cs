using System.Net;
using System.Text;
using Azure.Core;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class GraphDirectoryReaderTests
{
    private const string Oid = "11111111-1111-1111-1111-111111111111";
    private const string GroupA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string GroupB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private static readonly AzureIdentityRef Link = new(Oid, "tenant-1");

    // A minimal stub that returns a queued response and counts how many sends it saw, so caching
    // tests can assert the network was hit exactly once.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) =>
            new("stub-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) =>
            new(GetToken(c, ct));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // A well-formed $batch response for the requested user (real oid + GUID group values).
    private static HttpResponseMessage BatchOk(bool accountEnabled, params string[] groups)
    {
        string groupJson = string.Join(",", groups.Select(g => $"\"{g}\""));
        return Json(HttpStatusCode.OK, $$$"""
        {"responses":[
          {"id":"user","status":200,"body":{"id":"{{{Oid}}}","accountEnabled":{{{(accountEnabled ? "true" : "false")}}}}},
          {"id":"groups","status":200,"body":{"value":[{{{groupJson}}}]}}
        ]}
        """);
    }

    private static GraphDirectoryReader NewReader(StubHandler handler, IMemoryCache? cache = null) =>
        new(new HttpClient(handler), new StubTokenCredential(),
            cache ?? new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task Resolve_EnabledUser_ReturnsPrincipalSet_OidPlusGroups()
    {
        var handler = new StubHandler(_ => BatchOk(true, GroupA, GroupB));
        GraphDirectoryReader reader = NewReader(handler);

        AzureIdentitySet set = await reader.ResolveAsync(Link, CancellationToken.None);

        set.Outcome.Should().Be(AzureIdentityOutcome.Resolved);
        set.Enabled.Should().BeTrue();
        set.PrincipalOids.Should().BeEquivalentTo(Oid, GroupA, GroupB);
    }

    [Fact]
    public async Task Resolve_UserNotFound_IsDeprovisioned()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
        {"responses":[
          {"id":"user","status":404,"body":null},
          {"id":"groups","status":200,"body":{"value":[]}}
        ]}
        """));
        (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
            .Outcome.Should().Be(AzureIdentityOutcome.Deprovisioned);
    }

    [Fact]
    public async Task Resolve_AccountDisabled_IsDeprovisioned()
    {
        var handler = new StubHandler(_ => BatchOk(accountEnabled: false, GroupA));
        (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
            .Outcome.Should().Be(AzureIdentityOutcome.Deprovisioned);
    }

    [Fact]
    public async Task Resolve_GroupsCallFailed_FailsClosed()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
        {"responses":[
          {"id":"user","status":200,"body":{"id":"11111111-1111-1111-1111-111111111111","accountEnabled":true}},
          {"id":"groups","status":403,"body":null}
        ]}
        """));
        AzureIdentitySet set = await NewReader(handler).ResolveAsync(Link, CancellationToken.None);
        set.Outcome.Should().Be(AzureIdentityOutcome.Failed);
        set.PrincipalOids.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_WholeBatchUnauthorized_FailsClosed()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
            .Outcome.Should().Be(AzureIdentityOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_TransportThrows_FailsClosed()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("network down"));
        (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
            .Outcome.Should().Be(AzureIdentityOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_MissingSubResponse_FailsClosed()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"responses":[{"id":"user","status":200,"body":{"id":"11111111-1111-1111-1111-111111111111","accountEnabled":true}}]}"""));
        (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
            .Outcome.Should().Be(AzureIdentityOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_UserResponse200_ButAccountEnabledAbsent_FailsClosed_NotDeprovisioned()
    {
        // An anomalous 200 whose body omits accountEnabled is uncertain, not a confirmed
        // deprovision — it must fail as Failed (retried, never cached), not Deprovisioned (cached).
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
        {"responses":[
          {"id":"user","status":200,"body":{"id":"11111111-1111-1111-1111-111111111111"}},
          {"id":"groups","status":200,"body":{"value":[]}}
        ]}
        """));

        AzureIdentitySet set = await NewReader(handler).ResolveAsync(Link, CancellationToken.None);

        set.Outcome.Should().Be(AzureIdentityOutcome.Failed);
        set.PrincipalOids.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_UserBodyMissingId_FailsClosed()
    {
        // 200 + accountEnabled true but no id — the body cannot be confirmed to be the requested
        // user, so the gate must not accept it.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
        {"responses":[
          {"id":"user","status":200,"body":{"accountEnabled":true}},
          {"id":"groups","status":200,"body":{"value":[]}}
        ]}
        """));
        (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
            .Outcome.Should().Be(AzureIdentityOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_UserBodyIdMismatch_FailsClosed()
    {
        // 200 + accountEnabled true but the body is for a DIFFERENT user — never authorize the
        // requested oid off another user's enabled status.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
        {"responses":[
          {"id":"user","status":200,"body":{"id":"99999999-9999-9999-9999-999999999999","accountEnabled":true}},
          {"id":"groups","status":200,"body":{"value":[]}}
        ]}
        """));
        (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
            .Outcome.Should().Be(AzureIdentityOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_GroupValueNull_FailsClosed()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
        {"responses":[
          {"id":"user","status":200,"body":{"id":"11111111-1111-1111-1111-111111111111","accountEnabled":true}},
          {"id":"groups","status":200,"body":{"value":[null,"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"]}}
        ]}
        """));
        (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
            .Outcome.Should().Be(AzureIdentityOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_GroupValueNotGuid_FailsClosed()
    {
        // A non-GUID group value (e.g. a display name) is an anomalous response — fail closed
        // rather than admit a bogus principal into P.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
        {"responses":[
          {"id":"user","status":200,"body":{"id":"11111111-1111-1111-1111-111111111111","accountEnabled":true}},
          {"id":"groups","status":200,"body":{"value":["privileged-name"]}}
        ]}
        """));
        (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
            .Outcome.Should().Be(AzureIdentityOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_PrincipalOids_AreImmutable_CannotBeMutatedToPoisonCache()
    {
        var handler = new StubHandler(_ => BatchOk(true, GroupA));
        AzureIdentitySet set = await NewReader(handler).ResolveAsync(Link, CancellationToken.None);

        Action inject = () => ((IList<string>)set.PrincipalOids).Add("99999999-9999-9999-9999-999999999999");

        inject.Should().Throw<NotSupportedException>();
        set.PrincipalOids.Should().BeEquivalentTo(Oid, GroupA);
    }

    [Fact]
    public async Task Resolve_ConfidentAnswer_IsCached_SecondCallDoesNotHitNetwork()
    {
        var handler = new StubHandler(_ => BatchOk(true, GroupA));
        var cache = new MemoryCache(new MemoryCacheOptions());
        GraphDirectoryReader reader = NewReader(handler, cache);

        await reader.ResolveAsync(Link, CancellationToken.None);
        await reader.ResolveAsync(Link, CancellationToken.None);

        handler.Sends.Should().Be(1); // second answer served from cache
    }

    [Fact]
    public async Task Resolve_Failure_IsNotCached_NextCallRetries()
    {
        bool first = true;
        var handler = new StubHandler(_ =>
        {
            if (first) { first = false; return Json(HttpStatusCode.Unauthorized, "{}"); }
            return BatchOk(true, GroupA);
        });
        GraphDirectoryReader reader = NewReader(handler);

        (await reader.ResolveAsync(Link, CancellationToken.None)).Outcome.Should().Be(AzureIdentityOutcome.Failed);
        AzureIdentitySet second = await reader.ResolveAsync(Link, CancellationToken.None);

        second.Outcome.Should().Be(AzureIdentityOutcome.Resolved); // failure was retried, not cached
        handler.Sends.Should().Be(2);
    }

    [Fact]
    public async Task Resolve_Cancellation_IsRethrown_NotSwallowedAsFailed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        GraphDirectoryReader reader = NewReader(handler);

        Func<Task> act = () => reader.ResolveAsync(Link, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
