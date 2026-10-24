using System.Reflection;
using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Application.Ports;
using Connapse.Application.Sources;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using CsCheck;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// Each operation's rules over random inputs (epic #783 Phase 2.5): who is refused, what is never
/// written on a refusal, and what a success records. Every sample builds its own fakes, because CsCheck
/// runs samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
public class OperationRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Gen<T?> Maybe<T>(Gen<T> gen) where T : class =>
        Gen.Frequency((1, Gen.Const((T?)null)), (3, gen.Select(v => (T?)v)));

    // Names with spaces at either end and mixed case, so trimming and ordinal comparison both matter.
    private static readonly Gen<string> Name = Gen.String[Gen.Char[" aAbB-é"], 0, 6];

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), s));

    private static readonly Gen<Source> AnySource = Gen.Select(
        Gen.Guid, Name, Gen.Bool, Gen.Guid.Nullable(), Gen.Enum<ConnectionProvider>().Nullable(), Gen.Int[0, 5],
        (id, name, enabled, connectionId, provider, documents) =>
            new Source(id, name, null, connectionId, "{}", Epoch, Epoch, Enabled: enabled, DocumentCount: documents) with { Provider = provider });

    private static readonly Gen<string?> AnyScopeJson = Gen.OneOf(
        Gen.Const((string?)null), Gen.Const((string?)""), Gen.Const((string?)"  "), Gen.Const((string?)"{}"),
        Gen.Const((string?)"{\"scopeId\":\"a\"}"), Gen.Const((string?)"{not json"), Gen.Const((string?)"[1,"));

    /// <summary>A world of fakes for one sample: the caller's roles, and the stores and ports the operations call.</summary>
    private sealed class World
    {
        public readonly ISourceStore Sources = Substitute.For<ISourceStore>();
        public readonly IConnectionStore Connections = Substitute.For<IConnectionStore>();
        public readonly ISourceVisibility Visibility = Substitute.For<ISourceVisibility>();
        public readonly IOperationAuthorizer Authorizer = Substitute.For<IOperationAuthorizer>();
        public readonly IAuditLogger Audit = Substitute.For<IAuditLogger>();
        public readonly ISourceScopeRules UpdateRules = Substitute.For<ISourceScopeRules>();
        public readonly ISourceCreateRules CreateRules = Substitute.For<ISourceCreateRules>();
        public readonly ISourceSync Sync = Substitute.For<ISourceSync>();
        public readonly IReindexService Reindex = Substitute.For<IReindexService>();
        public readonly IMcpApprovalStore Approvals = Substitute.For<IMcpApprovalStore>();

        public World(bool viewer, bool admin)
        {
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Viewer).Returns(viewer);
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Admin).Returns(admin);
            Visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => true);
        }

        public IServiceScopeFactory Scopes =>
            new ServiceCollection().AddSingleton(Reindex).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        public int AuditCalls => Audit.ReceivedCalls().Count();

        public bool Wrote => Sources.ReceivedCalls().Any(c => c.GetMethodInfo().Name is "CreateAsync" or "UpdateAsync" or "DeleteAsync");
    }

    // ── sources.list ────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(ListSources))]
    public Task List_AnyQuery_PagesWithinBoundsAndShowsOnlyVisibleSources() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Int[-3, 3], Gen.Int[-2, 205].Nullable(), Gen.Bool, AnySource.Array[0, 6], Gen.Bool.Array[6])
            .SampleAsync(async t =>
            {
                var (caller, viewer, admin, skip, take, byConnection, stored, visibleMask) = t;
                var world = new World(viewer, admin);
                var visible = stored.Where((_, i) => visibleMask[i]).ToHashSet();
                world.Visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => s => visible.Contains(s));
                world.Sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(c => (IReadOnlyList<Source>)stored.Take(c.ArgAt<int>(1)).ToList());
                world.Sources.ListByConnectionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(stored);
                var query = new ListSourcesQuery(skip, take, byConnection ? Guid.NewGuid() : null);

                var listed = await new ListSources(world.Sources, world.Visibility, world.Authorizer).RunAsync(caller, query);

                if (!viewer)
                {
                    listed.Status.Should().Be(SourceReadStatus.Forbidden);
                    world.Sources.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                bool validPage = skip >= 0 && take is null or (>= 1 and <= ListSources.MaxTake);
                listed.Status.Should().Be(validPage ? SourceReadStatus.Success : SourceReadStatus.InvalidPage);
                if (!validPage)
                {
                    world.Sources.ReceivedCalls().Should().BeEmpty();
                    return;
                }

                // Asks for one more than the page, unless every source was asked for.
                if (!byConnection)
                    await world.Sources.Received(1).ListAsync(skip, take is int n ? n + 1 : int.MaxValue, Arg.Any<CancellationToken>());
                var returned = byConnection ? stored : stored.Take(take is int m ? m + 1 : int.MaxValue).ToArray();
                bool hasMore = take is int size && returned.Length > size;
                var page = hasMore ? returned.Take(take!.Value) : returned;
                listed.Result!.HasMore.Should().Be(hasMore);
                listed.Result.Items.Should().Equal(page.Where(visible.Contains));
                listed.Result.Diagnostics.Should().Be(admin);
            }, iter: 2000);

    // ── sources.get ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(GetSource))]
    public Task Get_AnySource_IsFoundOnlyWhenItExistsAndIsVisible() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, AnySource, Gen.Bool, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, viewer, admin, source, exists, visible) = t;
                var world = new World(viewer, admin);
                world.Sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(exists ? source : null);
                world.Visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => _ => visible);

                var read = await new GetSource(world.Sources, world.Visibility, world.Authorizer).RunAsync(caller, source.Id);

                if (!viewer)
                {
                    read.Status.Should().Be(SourceReadStatus.Forbidden);
                    world.Sources.ReceivedCalls().Should().BeEmpty();
                }
                else if (!exists || !visible)
                {
                    // An invisible source reads exactly as a missing one, so its existence isn't revealed.
                    read.Status.Should().Be(SourceReadStatus.NotFound);
                    read.Detail.Should().Be($"Source {source.Id} not found");
                    read.Result.Should().BeNull();
                }
                else
                {
                    read.Status.Should().Be(SourceReadStatus.Success);
                    read.Result.Should().Be(new SourceView(source, admin));
                }
            }, iter: 2000);

    // ── sources.create ──────────────────────────────────────────────────────────────────────────

    private static readonly Gen<ScopeCheck> AnyCreateCheck = Gen.OneOf(
        Gen.Select(Maybe(Name), Maybe(Name), (name, warning) => new ScopeCheck("{\"checked\":true}") { DefaultName = name, Warning = warning }),
        Gen.Select(Gen.Enum<ScopeRefusal>(), Maybe(Name), (refusal, field) => ScopeCheck.Refuse("refused", field, refusal)));

    // Weighted toward the requests that get past the early refusals, so the later rules are reached often.
    private static readonly Gen<NewSource> AnyNewSource = Gen.Select(
        Maybe(Name), Gen.Guid.Nullable(), AnyScopeJson,
        Gen.Frequency((3, Gen.Const((ConnectionProvider?)null)), (1, Gen.Enum<ConnectionProvider>().Select(p => (ConnectionProvider?)p))),
        Gen.Frequency((4, Gen.Const((string?)null)), (1, Gen.String[Gen.Char.AlphaNumeric, 0, 4].Select(s => (string?)s))),
        (name, connectionId, scope, provider, secret) => new NewSource(name, connectionId, scope, Provider: provider, Secret: secret));

    [Fact]
    [RandomInputTestOf(typeof(CreateSource))]
    public Task Create_AnyRequest_SavesOnlyAValidTrimmedNamedSourceAndAuditsOnlyThat() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyNewSource, Gen.Bool, AnyCreateCheck, Gen.Bool, Gen.Frequency((3, Gen.Const(0)), (1, Gen.Int[1, 3])))
            .SampleAsync(async t =>
            {
                var (caller, admin, request, connectionExists, check, nameTaken, storeOutcome) = t;
                var world = new World(viewer: true, admin);
                var connection = new Connection(request.ConnectionId ?? Guid.Empty, "c", ConnectionProvider.S3, "{}", null, Epoch, Epoch);
                world.Connections.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(connectionExists ? connection : null);
                world.CreateRules.CheckCreateAsync(connection, request.ScopeJson, Arg.Any<CancellationToken>()).Returns(check);
                world.Sources.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(c => nameTaken ? new Source(Guid.NewGuid(), c.Arg<string>(), null, null, "{}", Epoch, Epoch) : null);
                world.Sources.CreateAsync(Arg.Any<CreateSourceRequest>(), Arg.Any<CancellationToken>()).Returns(c => storeOutcome switch
                {
                    1 => throw new ArgumentException("bad"),
                    2 => throw new DuplicateSourceScopeException("dup"),
                    3 => throw new InvalidOperationException("raced"),
                    _ => new Source(Guid.NewGuid(), c.Arg<CreateSourceRequest>().Name, null, null, "{}", Epoch, Epoch),
                });

                var created = await new CreateSource(world.Sources, world.Connections, world.CreateRules, world.Audit, world.Authorizer)
                    .RunAsync(caller, request);

                var creates = world.Sources.ReceivedCalls().Where(c => c.GetMethodInfo().Name == "CreateAsync")
                    .Select(c => (CreateSourceRequest)c.GetArguments()[0]!).ToList();
                if (!admin)
                {
                    created.Status.Should().Be(SourceWriteStatus.Forbidden);
                    world.Sources.ReceivedCalls().Should().BeEmpty();
                    world.Connections.ReceivedCalls().Should().BeEmpty();
                }
                if (request.Secret is not null || request.Provider is ConnectionProvider.Custom)
                {
                    // Refused before any store is read.
                    created.Succeeded.Should().BeFalse();
                    world.Connections.ReceivedCalls().Should().BeEmpty();
                    creates.Should().BeEmpty();
                }
                if (request.ConnectionId is not null && request.Provider is not null)
                    created.Succeeded.Should().BeFalse();
                if (request.ConnectionId is not null && !connectionExists)
                {
                    created.Succeeded.Should().BeFalse();
                    world.CreateRules.ReceivedCalls().Should().BeEmpty();
                }
                if (admin && request.Secret is null && request.Provider is null && request.ConnectionId is not null && connectionExists && !check.Ok)
                {
                    created.Status.Should().Be(check.Refusal switch
                    {
                        ScopeRefusal.Duplicate => SourceWriteStatus.Conflict,
                        ScopeRefusal.Unavailable => SourceWriteStatus.Unavailable,
                        _ => SourceWriteStatus.Invalid,
                    });
                    created.Field.Should().Be(check.Field);
                }

                // At most one create reaches the store, with a trimmed, non-empty, untaken name and no secret.
                creates.Should().HaveCountLessThanOrEqualTo(1);
                foreach (var saved in creates)
                {
                    saved.Name.Should().NotBeEmpty().And.Be(saved.Name.Trim());
                    saved.Secret.Should().BeNull();
                    nameTaken.Should().BeFalse();
                    if (request.ConnectionId is not null)
                        saved.ScopeJson.Should().Be(check.ScopeJson);
                }

                // Success exactly when the store saved it, and only a success is audited.
                created.Succeeded.Should().Be(creates.Count == 1 && storeOutcome == 0);
                world.AuditCalls.Should().Be(created.Succeeded ? 1 : 0);
                if (created.Succeeded)
                {
                    created.Result!.Source.Name.Should().Be(creates[0].Name);
                    created.Result.Warning.Should().Be(request.ConnectionId is null ? null : check.Warning);
                }
            }, iter: 2000);

    // ── sources.update ──────────────────────────────────────────────────────────────────────────

    private static readonly Gen<UpdateSourceRequest> AnyUpdate = Gen.Select(
        Maybe(Name), AnyScopeJson, Gen.Bool.Nullable(), Maybe(Gen.String[Gen.Char.AlphaNumeric, 0, 4]),
        (name, scope, enabled, secret) => new UpdateSourceRequest(Name: name, ScopeJson: scope, Enabled: enabled, Secret: secret));

    [Fact]
    [RandomInputTestOf(typeof(UpdateSource))]
    public Task Update_AnyRequest_SavesOnlyAnAllowedChangeAndAuditsWhatChanged() =>
        Gen.Select(AnyCaller, Gen.Bool, AnySource, Gen.Bool, AnyUpdate, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, admin, source, exists, request, scopeAllowed) = t;
                var world = new World(viewer: true, admin);
                world.Sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(exists ? source : null);
                world.Sources.UpdateAsync(source.Id, Arg.Any<UpdateSourceRequest>(), Arg.Any<CancellationToken>()).Returns(exists ? source : null);
                world.UpdateRules.CheckUpdateAsync(source, Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(scopeAllowed ? new ScopeCheck("{\"rewritten\":true}") : ScopeCheck.Refuse("fixed", "scope"));

                var updated = await new UpdateSource(world.Sources, world.UpdateRules, world.Audit, world.Authorizer).RunAsync(caller, source.Id, request);

                bool jsonOk = string.IsNullOrWhiteSpace(request.ScopeJson) || request.ScopeJson.StartsWith('{') && request.ScopeJson.EndsWith('}');
                bool refusedScope = request.ScopeJson is not null && jsonOk && exists && !scopeAllowed;
                SourceWriteStatus expected =
                    !admin ? SourceWriteStatus.Forbidden
                    : !jsonOk || refusedScope || request.Secret is not null ? SourceWriteStatus.Invalid
                    : !exists ? SourceWriteStatus.NotFound
                    : SourceWriteStatus.Success;
                updated.Status.Should().Be(expected);

                if (!admin)
                    world.Sources.ReceivedCalls().Should().BeEmpty();
                if (expected is SourceWriteStatus.Forbidden or SourceWriteStatus.Invalid)
                    world.Wrote.Should().BeFalse();
                if (request.Secret is not null && admin && jsonOk && !refusedScope)
                    updated.Field.Should().Be("secret");

                // A scope the provider rewrote is the one saved.
                if (expected == SourceWriteStatus.Success && request.ScopeJson is not null)
                    await world.Sources.Received(1).UpdateAsync(source.Id, Arg.Is<UpdateSourceRequest>(r => r.ScopeJson == "{\"rewritten\":true}"), Arg.Any<CancellationToken>());

                world.AuditCalls.Should().Be(expected == SourceWriteStatus.Success ? 1 : 0);
                if (expected == SourceWriteStatus.Success)
                {
                    object details = request.Enabled is bool enabled ? new { source.Name, Enabled = enabled } : new { source.Name };
                    await world.Audit.Received(1).LogAsync("source.updated", "source", source.Id.ToString(),
                        Arg.Is<object>(d => d.ToString() == details.ToString()), Arg.Any<CancellationToken>());
                }
            }, iter: 2000);

    // ── sources.delete ──────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(DeleteSource))]
    public Task Delete_AnyConfirmation_DeletesOnlyWhenTheExactNameIsTyped() =>
        Gen.Select(AnyCaller, Gen.Bool, AnySource, Gen.Bool, Gen.Bool, Gen.OneOf(Maybe(Name), Gen.Const((string?)null)), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, admin, source, exists, typeTheName, other, storeDeletes) = t;
                var world = new World(viewer: true, admin);
                world.Sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(exists ? source : null);
                world.Sources.DeleteAsync(source.Id, Arg.Any<CancellationToken>()).Returns(storeDeletes);
                // Either the name with surrounding spaces, or anything else at all.
                string? confirm = typeTheName ? $"  {source.Name} " : other;

                var deleted = await new DeleteSource(world.Sources, world.Audit, world.Authorizer).RunAsync(caller, source.Id, confirm);

                bool confirmed = string.Equals(confirm?.Trim(), source.Name, StringComparison.Ordinal);
                if (!admin)
                {
                    deleted.Status.Should().Be(SourceWriteStatus.Forbidden);
                    world.Sources.ReceivedCalls().Should().BeEmpty();
                }
                else if (!exists)
                    deleted.Status.Should().Be(SourceWriteStatus.NotFound);
                else if (!confirmed)
                {
                    deleted.Status.Should().Be(SourceWriteStatus.Invalid);
                    deleted.Field.Should().Be("confirmName");
                }
                else
                    deleted.Status.Should().Be(storeDeletes ? SourceWriteStatus.Success : SourceWriteStatus.NotFound);

                world.Sources.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "DeleteAsync").Should().Be(admin && exists && confirmed ? 1 : 0);
                world.AuditCalls.Should().Be(deleted.Succeeded ? 1 : 0);
                if (deleted.Succeeded)
                    deleted.Result.Should().Be(source);
            }, iter: 2000);

    // ── sources.sync ────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(SyncSource))]
    public Task Sync_AnySourceState_RunsOnlyAnEnabledReachableSourceAndAuditsOnlyACompletedCycle() =>
        Gen.Select(AnyCaller, Gen.Bool, AnySource, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Int[0, 9])
            .SampleAsync(async t =>
            {
                var (caller, admin, source, exists, connectionExists, applyWithheld, alreadyRunning, upserted) = t;
                var world = new World(viewer: true, admin);
                var connection = new Connection(source.ConnectionId ?? Guid.Empty, "c", ConnectionProvider.S3, "{}", null, Epoch, Epoch);
                world.Sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(exists ? source : null);
                world.Connections.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(connectionExists ? connection : null);
                var result = new SourceSyncResult(upserted, 0, false, false, null, AlreadyRunning: alreadyRunning);
                world.Sync.SyncNowAsync(Arg.Any<Source>(), Arg.Any<Connection?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(result);

                var synced = await new SyncSource(world.Sources, world.Connections, world.Sync, world.Audit, world.Authorizer)
                    .RunAsync(caller, source.Id, applyWithheld);

                bool reachable = source.ConnectionId is not null ? connectionExists : source.Provider is not null;
                bool runs = admin && exists && source.Enabled && reachable;
                SourceWriteStatus expected =
                    !admin ? SourceWriteStatus.Forbidden
                    : !exists ? SourceWriteStatus.NotFound
                    : !runs ? SourceWriteStatus.Invalid
                    : alreadyRunning ? SourceWriteStatus.Busy
                    : SourceWriteStatus.Success;
                synced.Status.Should().Be(expected);

                if (runs)
                    await world.Sync.Received(1).SyncNowAsync(source, source.ConnectionId is null ? null : connection, applyWithheld, Arg.Any<CancellationToken>());
                else
                    world.Sync.ReceivedCalls().Should().BeEmpty();

                world.AuditCalls.Should().Be(expected == SourceWriteStatus.Success ? 1 : 0);
                if (expected == SourceWriteStatus.Success)
                {
                    await world.Audit.Received(1).LogAsync(applyWithheld ? "source.deletions_applied" : "source.synced", "source",
                        source.Id.ToString(), Arg.Any<object>(), Arg.Any<CancellationToken>());
                    synced.Result.Should().Be(new SourceSynced(source, result));
                }
            }, iter: 2000);

    // ── sources.retryFailed ─────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(RetryFailedDocuments))]
    public Task RetryFailed_AnySource_RequeuesOnlyForAnAdminAndReportsTheCount() =>
        Gen.Select(AnyCaller, Gen.Bool, AnySource, Gen.Bool, Gen.Int[0, 50])
            .SampleAsync(async t =>
            {
                var (caller, admin, source, exists, enqueued) = t;
                var world = new World(viewer: true, admin);
                world.Sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(exists ? source : null);
                world.Reindex.RetryFailedAsync(source.Id, Arg.Any<CancellationToken>()).Returns(enqueued);

                var retried = await new RetryFailedDocuments(world.Sources, world.Scopes, world.Audit, world.Authorizer).RunAsync(caller, source.Id);

                retried.Status.Should().Be(!admin ? SourceWriteStatus.Forbidden : !exists ? SourceWriteStatus.NotFound : SourceWriteStatus.Success);
                world.Reindex.ReceivedCalls().Count().Should().Be(retried.Succeeded ? 1 : 0);
                world.AuditCalls.Should().Be(retried.Succeeded ? 1 : 0);
                if (retried.Succeeded)
                    retried.Result.Should().Be(enqueued);
            }, iter: 2000);

    // ── approvals.list and approvals.decide ─────────────────────────────────────────────────────

    /// <summary>A caller as the approval rules see them: how they signed in, who they say they are, and their roles.</summary>
    private sealed record ApprovalCaller(OperationSurface Surface, bool Browser, string? AuthMethod, string? NameId, bool Owner, bool Admin)
    {
        public OperationContext Context
        {
            get
            {
                var claims = new List<Claim>();
                if (NameId is not null)
                    claims.Add(new Claim(ClaimTypes.NameIdentifier, NameId));
                if (AuthMethod is not null)
                    claims.Add(new Claim("auth_method", AuthMethod));
                if (Owner)
                    claims.Add(new Claim(ClaimTypes.Role, "Owner"));
                if (Admin)
                    claims.Add(new Claim(ClaimTypes.Role, "Admin"));
                var identity = new ClaimsIdentity(claims, Browser ? IdentityConstants.ApplicationScheme : "Bearer");
                return new OperationContext(new ClaimsPrincipal(identity), Surface);
            }
        }

        /// <summary>Who may decide: a person signed in to the UI in a browser, never a token, with a user id.</summary>
        public (Guid UserId, bool IsAdmin)? Decider =>
            Surface == OperationSurface.Ui && Browser && AuthMethod is not ("api_key" or "agent_api_key") && Guid.TryParse(NameId, out var id)
                ? (id, Owner || Admin)
                : null;
    }

    private static readonly Gen<ApprovalCaller> AnyApprovalCaller = Gen.Select(
        Gen.Enum<OperationSurface>(), Gen.Bool,
        Gen.OneOf(Gen.Const((string?)null), Gen.Const((string?)"api_key"), Gen.Const((string?)"agent_api_key"), Gen.Const((string?)"password")),
        Gen.OneOf(Gen.Const((string?)null), Gen.Const((string?)"not-a-guid"), Gen.Guid.Select(g => (string?)g.ToString())),
        Gen.Bool, Gen.Bool,
        (surface, browser, method, nameId, owner, admin) => new ApprovalCaller(surface, browser, method, nameId, owner, admin));

    private static McpApprovalOutcome Refusal(ApprovalCaller approver) =>
        approver.Surface != OperationSurface.Ui ? McpApprovalOutcome.NotFromUi : McpApprovalOutcome.NotSignedIn;

    [Fact]
    [RandomInputTestOf(typeof(ListMcpApprovals))]
    public Task ListApprovals_AnyCaller_ListsOnlyForABrowserSignInAndOnlyWhatTheStoreAllows() =>
        Gen.Select(AnyApprovalCaller, Gen.Int[0, 3])
            .SampleAsync(async t =>
            {
                var (approver, count) = t;
                var world = new World(viewer: true, admin: true);
                var pending = Enumerable.Range(0, count).Select(i => new McpApprovalEntity
                {
                    Id = Guid.NewGuid(),
                    ToolName = $"tool{i}",
                    ArgumentsSummary = "args",
                    TokenKey = "pat:1",
                    CreatedAt = Epoch,
                    ExpiresAt = Epoch.AddMinutes(15),
                }).ToList();
                world.Approvals.ListPendingAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(pending);

                var listed = await new ListMcpApprovals(world.Approvals).ExecuteAsync(approver.Context);

                if (approver.Decider is not var (userId, isAdmin))
                {
                    listed.Status.Should().Be(Refusal(approver));
                    world.Approvals.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                listed.Status.Should().Be(McpApprovalOutcome.Success);
                await world.Approvals.Received(1).ListPendingAsync(userId, isAdmin, Arg.Any<CancellationToken>());
                listed.Result.Should().Equal(pending.Select(a => new McpApprovalItem(a.Id, a.ToolName, a.ArgumentsSummary, a.TokenKey, a.CreatedAt, a.ExpiresAt)));
            }, iter: 2000);

    [Fact]
    [RandomInputTestOf(typeof(DecideMcpApproval))]
    public Task DecideApproval_AnyCaller_DecidesOnlyFromABrowserSignInAndHidesOthersRequests() =>
        Gen.Select(AnyApprovalCaller, Gen.Guid, Gen.Bool, Gen.Enum<McpApprovalDecision>())
            .SampleAsync(async t =>
            {
                var (approver, id, approve, decision) = t;
                var world = new World(viewer: true, admin: true);
                world.Approvals.DecideAsync(Arg.Is(id), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Is(approve), Arg.Any<CancellationToken>()).Returns(decision);

                var decided = await new DecideMcpApproval(world.Approvals).ExecuteAsync(approver.Context, id, approve);

                if (approver.Decider is not var (userId, isAdmin))
                {
                    decided.Status.Should().Be(Refusal(approver));
                    world.Approvals.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                await world.Approvals.Received(1).DecideAsync(id, userId, isAdmin, approve, Arg.Any<CancellationToken>());
                // Someone else's request (Forbidden in the store) reads as not found.
                decided.Status.Should().Be(decision switch
                {
                    McpApprovalDecision.Decided => McpApprovalOutcome.Success,
                    McpApprovalDecision.NotPending => McpApprovalOutcome.NotPending,
                    _ => McpApprovalOutcome.NotFound,
                });
                if (decided.Succeeded)
                    decided.Result.Should().Be(approve);
            }, iter: 2000);

    // ── completeness ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryOperation_HasARandomInputTest()
    {
        var operations = typeof(OperationAttribute).Assembly.GetTypes()
            .Where(t => t.IsDefined(typeof(OperationAttribute), inherit: false))
            .ToHashSet();
        var tested = typeof(OperationRandomInputTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.IsDefined(typeof(FactAttribute), inherit: true))
            .SelectMany(m => m.GetCustomAttributes<RandomInputTestOfAttribute>())
            .Select(a => a.Operation)
            .ToHashSet();

        operations.Should().NotBeEmpty();
        operations.Except(tested).Select(t => t.Name).Should().BeEmpty("every operation needs a [RandomInputTestOf] test of its rules");
        tested.Except(operations).Select(t => t.Name).Should().BeEmpty("[RandomInputTestOf] must name an [Operation] class");
    }
}
