using System.Reflection;
using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Application.Links;
using Connapse.Application.Ports;
using Connapse.Application.Sources;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Connapse.Storage.Connectors.Custom;
using Connapse.Storage.Connectors.GitHub;
using CsCheck;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        public readonly FixedVisibility Visibility = new();
        public readonly PolicyAuthorizer Authorizer;

        // Built on first use: a sample uses few of them, and each costs a proxy.
        private ISourceStore? sources;
        private IConnectionStore? connections;
        private IAuditLogger? audit;
        private ISourceScopeRules? updateRules;
        private ISourceCreateRules? createRules;
        private ISourceSync? sync;
        private IReindexService? reindex;
        private IMcpApprovalStore? approvals;
        private IDocumentStore? documents;

        public ISourceStore Sources => sources ??= Substitute.For<ISourceStore>();
        public IConnectionStore Connections => connections ??= Substitute.For<IConnectionStore>();
        public IAuditLogger Audit => audit ??= Substitute.For<IAuditLogger>();
        public ISourceScopeRules UpdateRules => updateRules ??= Substitute.For<ISourceScopeRules>();
        public ISourceCreateRules CreateRules => createRules ??= Substitute.For<ISourceCreateRules>();
        public ISourceSync Sync => sync ??= Substitute.For<ISourceSync>();
        public IReindexService Reindex => reindex ??= Substitute.For<IReindexService>();
        public IMcpApprovalStore Approvals => approvals ??= Substitute.For<IMcpApprovalStore>();
        public IDocumentStore Documents => documents ??= Substitute.For<IDocumentStore>();

        public World(bool viewer, bool admin)
        {
            // One stub for every policy: each sample builds a World, so a stub per policy costs CI's small tier.
            Authorizer = new PolicyAuthorizer(policy => policy switch
            {
                Policies.Viewer => viewer,
                // The reads that agent keys may also make check Reader; a sample's caller passes both or neither.
                Policies.Reader => viewer,
                Policies.Admin => admin,
                // Source changes check the sources policy: an administrator signed in, or their token with admin:sources.
                Policies.SourcesAdmin => admin,
                _ => false,
            });
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
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Int[-3, 3], Gen.Int[-2, 205].Nullable(), Gen.Guid.Nullable(), AnySource.Array[0, 6], Gen.Bool.Array[6])
            .SampleAsync(async t =>
            {
                var (caller, viewer, admin, skip, take, connectionId, stored, visibleMask) = t;
                bool byConnection = connectionId is not null;
                var world = new World(viewer, admin);
                var visible = stored.Where((_, i) => visibleMask[i]).ToHashSet();
                world.Visibility.Sees = s => visible.Contains(s);
                world.Sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(c => (IReadOnlyList<Source>)stored.Take(c.ArgAt<int>(1)).ToList());
                // Another connection's sources, so listing the wrong connection shows up as the wrong items.
                world.Sources.ListByConnectionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns([new Source(Guid.NewGuid(), "elsewhere", null, null, "{}", Epoch, Epoch)]);
                if (connectionId is Guid requested)
                    world.Sources.ListByConnectionAsync(requested, Arg.Any<CancellationToken>()).Returns(stored);
                var query = new ListSourcesQuery(skip, take, connectionId);

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
                if (connectionId is Guid listedConnection)
                {
                    await world.Sources.Received(1).ListByConnectionAsync(listedConnection, Arg.Any<CancellationToken>());
                    await world.Sources.DidNotReceiveWithAnyArgs().ListAsync();
                }
                else
                {
                    await world.Sources.Received(1).ListAsync(skip, take is int n ? n + 1 : int.MaxValue, Arg.Any<CancellationToken>());
                    await world.Sources.DidNotReceiveWithAnyArgs().ListByConnectionAsync(default);
                }
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
                world.Visibility.Sees = _ => visible;

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

    // ── sources.stats ───────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(GetSourceStats))]
    public Task Stats_AnyIdOrName_CountsOnlyAVisibleSourceItResolves() =>
        Gen.Select(AnyCaller, Gen.Bool, AnySource, Gen.Int[0, 3], Gen.Bool, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, reader, source, keyKind, exists, visible) = t;
                var world = new World(reader, admin: false);
                string stored = source.Name.Trim().ToLowerInvariant();
                world.Sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(exists ? source : null);
                world.Sources.GetByNameAsync(stored, Arg.Any<CancellationToken>()).Returns(exists ? source : null);
                world.Visibility.Sees = _ => visible;
                var counts = new ContainerStats(3, 2, 1, 0, 12, 4096, null);
                world.Documents.GetContainerStatsAsync(source.Id, Arg.Any<CancellationToken>()).Returns(counts);
                // By id, by its name as typed, by its stored name, or by a name nothing has.
                string key = keyKind switch { 0 => source.Id.ToString(), 1 => source.Name, 2 => stored, _ => "zz-none" };
                bool resolves = keyKind != 3 && (keyKind == 0 || !string.IsNullOrWhiteSpace(source.Name)) && exists;

                var read = await new GetSourceStats(world.Sources, world.Documents, world.Visibility, world.Authorizer).RunAsync(caller, key);

                if (!reader)
                {
                    read.Status.Should().Be(SourceReadStatus.Forbidden);
                    world.Sources.ReceivedCalls().Should().BeEmpty();
                    world.Documents.ReceivedCalls().Should().BeEmpty();
                }
                else if (!resolves || !visible)
                {
                    // An invisible source reads exactly as a missing one, and its counts are never read.
                    read.Status.Should().Be(SourceReadStatus.NotFound);
                    read.Detail.Should().Be($"Source '{key}' not found");
                    world.Documents.ReceivedCalls().Should().BeEmpty();
                }
                else
                {
                    read.Status.Should().Be(SourceReadStatus.Success);
                    read.Result.Should().Be(new SourceStatsView(source, counts));
                }
            }, iter: 2000);

    /// <summary>Each group of these tests is its own class, so xUnit runs the groups side by side instead of one after another (CON-145).</summary>
    [Trait("Category", "Unit")]
    public class Creating
    {
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
                    var expected = ExpectCreate(admin, request, connectionExists, check, nameTaken, storeOutcome);

                    created.Status.Should().Be(expected.Status);
                    if (expected.Field is not null)
                        created.Field.Should().Be(expected.Field);
                    if (!admin)
                        world.Sources.ReceivedCalls().Should().BeEmpty();
                    // A secret or a custom provider is refused before any store is read; a missing connection before its scope is checked.
                    if (admin && (request.Secret is not null || request.Provider is ConnectionProvider.Custom))
                        world.Connections.ReceivedCalls().Should().BeEmpty();
                    if (request.ConnectionId is not null && !connectionExists)
                        world.CreateRules.ReceivedCalls().Should().BeEmpty();

                    // Exactly one create reaches the store for a valid request, with the trimmed name and the checked scope.
                    if (expected.Saved is null)
                        creates.Should().BeEmpty();
                    else
                        creates.Should().ContainSingle().Which.Should().Be(expected.Saved);

                    world.AuditCalls.Should().Be(created.Succeeded ? 1 : 0);
                    if (created.Succeeded)
                    {
                        created.Result!.Source.Name.Should().Be(expected.Saved!.Name);
                        created.Result.Warning.Should().Be(request.ConnectionId is null ? null : check.Warning);
                    }
                }, iter: 2000);

        /// <summary>
        /// What creating <paramref name="r"/> should do, worked out from the request and the fakes' answers
        /// alone: the status, the field at fault, and the request the store should receive, if any.
        /// </summary>
        private static (SourceWriteStatus Status, string? Field, CreateSourceRequest? Saved) ExpectCreate(
            bool admin, NewSource r, bool connectionExists, ScopeCheck check, bool nameTaken, int storeOutcome)
        {
            if (!admin)
                return (SourceWriteStatus.Forbidden, null, null);
            if (r.Secret is not null)
                return (SourceWriteStatus.Invalid, "secret", null);
            if (r.Provider is ConnectionProvider.Custom)
                return (SourceWriteStatus.Invalid, "connectionId", null);

            string name = r.Name?.Trim() ?? "";
            string? scope = r.ScopeJson;
            if (name.Length == 0 && r.ConnectionId is null)
                return (SourceWriteStatus.Invalid, "name", null);
            if (r.ConnectionId is not null && r.Provider is not null)
                return (SourceWriteStatus.Invalid, null, null);
            if (r.ConnectionId is not null)
            {
                if (!connectionExists)
                    return (SourceWriteStatus.Invalid, null, null);
                if (!check.Ok)
                {
                    var refused = check.Refusal switch
                    {
                        ScopeRefusal.Duplicate => SourceWriteStatus.Conflict,
                        ScopeRefusal.Unavailable => SourceWriteStatus.Unavailable,
                        _ => SourceWriteStatus.Invalid,
                    };
                    return (refused, check.Field, null);
                }
                scope = check.ScopeJson;
                if (name.Length == 0)
                    name = check.DefaultName?.Trim() ?? "";
                if (name.Length == 0)
                    return (SourceWriteStatus.Invalid, "name", null);
            }
            else if (r.Provider is null or ConnectionProvider.GitHub)
            {
                return (SourceWriteStatus.Invalid, null, null);
            }

            if (!string.IsNullOrWhiteSpace(scope) && !IsJson(scope))
                return (SourceWriteStatus.Invalid, null, null);
            if (nameTaken)
                return (SourceWriteStatus.Conflict, null, null);

            var saved = new CreateSourceRequest(name, r.ConnectionId, scope ?? "{}", r.Description, r.SyncIntervalSeconds, r.Provider);
            return storeOutcome switch
            {
                0 => (SourceWriteStatus.Success, null, saved),
                1 => (SourceWriteStatus.Invalid, null, saved),
                2 => (SourceWriteStatus.Conflict, "scopeId", saved),
                _ => (SourceWriteStatus.Conflict, "name", saved),
            };
        }

        private static bool IsJson(string text)
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(text);
                return true;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }
    }

    // ── sources.scopeSchema ─────────────────────────────────────────────────────────────────────

    private static readonly Gen<string> AnyProviderText = Gen.OneOf(
        Gen.Enum<ConnectionProvider>().Select(p => p.ToString()),
        Gen.Enum<ConnectionProvider>().Select(p => p.ToString().ToUpperInvariant()),
        Gen.Enum<ConnectionProvider>().Select(p => ((int)p).ToString()),
        Gen.Int[-3, 12].Select(n => n.ToString()),
        Gen.String[Gen.Char[" aAbBsS3-"], 0, 6]);

    [Fact]
    [RandomInputTestOf(typeof(GetScopeSchema))]
    public Task ScopeSchema_AnyProviderText_AnswersOnlyForAProviderName() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyProviderText)
            .SampleAsync(async t =>
            {
                var (caller, viewer, provider) = t;
                var world = new World(viewer, admin: false);

                var read = await new GetScopeSchema(world.Authorizer).RunAsync(caller, provider);

                // A name only, never a number: "3" would otherwise parse as the provider numbered 3.
                bool named = !int.TryParse(provider, out _)
                    && Enum.GetNames<ConnectionProvider>().Contains(provider, StringComparer.OrdinalIgnoreCase);
                ScopeSchemaStatus expected = !viewer ? ScopeSchemaStatus.Forbidden
                    : named ? ScopeSchemaStatus.Success
                    : ScopeSchemaStatus.UnknownProvider;
                read.Status.Should().Be(expected);
                if (expected == ScopeSchemaStatus.Success)
                    read.Result!.Provider.ToString().Should().BeEquivalentTo(provider);
            });

    /// <summary>
    /// The seed CI found " S3" with (CON-75): Enum.TryParse trims, so a padded name read as S3. The same
    /// property as the test above, pinned so the case that found it always runs.
    /// </summary>
    [Fact]
    public Task ScopeSchema_TheSeedThatFoundAPaddedName_AnswersOnlyForAProviderName() =>
        Gen.Select(AnyCaller, Gen.Bool, AnyProviderText).SampleAsync(ScopeSchemaAnswersOnlyForAName, seed: "5lVM0oQMaFF3");

    [Theory]
    [InlineData(" S3")]
    [InlineData("S3 ")]
    [InlineData("S3, AzureBlob")]
    [InlineData("S3,S3")]
    public async Task ScopeSchema_ANameWithAnythingMore_IsAnUnknownProvider(string provider)
    {
        var world = new World(viewer: true, admin: false);

        var read = await new GetScopeSchema(world.Authorizer).RunAsync(new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest), provider);

        read.Status.Should().Be(ScopeSchemaStatus.UnknownProvider);
    }

    private static async Task ScopeSchemaAnswersOnlyForAName((OperationContext Caller, bool Viewer, string Provider) t)
    {
        var (caller, viewer, provider) = t;
        var world = new World(viewer, admin: false);

        var read = await new GetScopeSchema(world.Authorizer).RunAsync(caller, provider);

        // A name only, never a number: "3" would otherwise parse as the provider numbered 3.
        bool named = !int.TryParse(provider, out _)
            && Enum.GetNames<ConnectionProvider>().Contains(provider, StringComparer.OrdinalIgnoreCase);
        ScopeSchemaStatus expected = !viewer ? ScopeSchemaStatus.Forbidden
            : named ? ScopeSchemaStatus.Success
            : ScopeSchemaStatus.UnknownProvider;
        read.Status.Should().Be(expected);
        if (expected == ScopeSchemaStatus.Success)
            read.Result!.Provider.ToString().Should().BeEquivalentTo(provider);
    }

    [Trait("Category", "Unit")]
    public class Changing
    {
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

                    var updated = await new UpdateSource(world.Sources, world.Connections, world.UpdateRules, world.Audit, world.Authorizer).RunAsync(caller, source.Id, request);

                    bool jsonOk = string.IsNullOrWhiteSpace(request.ScopeJson) || request.ScopeJson.StartsWith('{') && request.ScopeJson.EndsWith('}');
                    bool refusedScope = request.ScopeJson is not null && jsonOk && exists && !scopeAllowed;
                    bool blankName = request.Name is not null && request.Name.Trim().Length == 0;
                    SourceWriteStatus expected =
                        !admin ? SourceWriteStatus.Forbidden
                        : blankName || !jsonOk || refusedScope || request.Secret is not null ? SourceWriteStatus.Invalid
                        : !exists ? SourceWriteStatus.NotFound
                        : SourceWriteStatus.Success;
                    updated.Status.Should().Be(expected);

                    if (!admin)
                        world.Sources.ReceivedCalls().Should().BeEmpty();
                    if (expected is SourceWriteStatus.Forbidden or SourceWriteStatus.Invalid)
                        world.Wrote.Should().BeFalse();
                    if (admin && blankName)
                        updated.Field.Should().Be("name");
                    else if (request.Secret is not null && admin && jsonOk && !refusedScope)
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
    }

    [Trait("Category", "Unit")]
    public class Running
    {
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
    }

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

    [Trait("Category", "Unit")]
    public class AccountLinks
    {
        // ── links.list / links.unlink ───────────────────────────────────────────────────────────────

        /// <summary>A caller the account policy admits or not, with a user id, junk, or none.</summary>
        private sealed record LinkCaller(bool Allowed, string? NameId)
        {
            public OperationContext Context => new(new ClaimsPrincipal(new ClaimsIdentity(
                NameId is null ? [] : [new Claim(ClaimTypes.NameIdentifier, NameId)], "test")), OperationSurface.Rest);

            public Guid? UserId => Allowed && Guid.TryParse(NameId, out var id) ? id : null;
        }

        private static readonly Gen<LinkCaller> AnyLinkCaller = Gen.Select(
            Gen.Bool, Gen.OneOf(Gen.Const((string?)null), Gen.Const((string?)"not-a-guid"), Gen.Guid.Select(g => (string?)g.ToString())),
            (allowed, nameId) => new LinkCaller(allowed, nameId));

        /// <summary>What a custom provider's source server answers about a link: linked, not linked, or a failure.</summary>
        public enum CustomAnswer { Linked, NotLinked, Down, Refused, Missing }

        private static CustomProvider Custom(Guid id) => new(
            id, "bridge", "https://bridge.test", true, 100, true, "https://bridge.test/link", Epoch, Epoch, Epoch, HasLinkSecret: true);

        private static Task<CustomLinkStatus> Answer(CustomAnswer answer) => answer switch
        {
            CustomAnswer.Linked => Task.FromResult(new CustomLinkStatus(true, "alice")),
            CustomAnswer.NotLinked => Task.FromResult(new CustomLinkStatus(false, null)),
            CustomAnswer.Down => Task.FromException<CustomLinkStatus>(new HttpRequestException("down")),
            CustomAnswer.Refused => Task.FromException<CustomLinkStatus>(new CustomSourceUnauthorizedException("refused")),
            _ => Task.FromException<CustomLinkStatus>(new CustomProviderNotFoundException("gone")),
        };

        /// <summary>An in-memory identity database per sample, as the link stores read and write it.</summary>
        private static IDbContextFactory<ConnapseIdentityDbContext> IdentityDb() =>
            new InMemoryIdentityDb(new DbContextOptionsBuilder<ConnapseIdentityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        private sealed class InMemoryIdentityDb(DbContextOptions<ConnapseIdentityDbContext> options) : IDbContextFactory<ConnapseIdentityDbContext>
        {
            public ConnapseIdentityDbContext CreateDbContext() => new(options);
        }

        private static IOptionsMonitor<T> Unconfigured<T>() where T : class, new()
        {
            var monitor = Substitute.For<IOptionsMonitor<T>>();
            monitor.CurrentValue.Returns(new T());
            return monitor;
        }

        [Fact]
        [RandomInputTestOf(typeof(ListAccountLinks))]
        public Task ListLinks_AnyCaller_ReadsOnlyTheCallersOwnLinksAndNeverFailsOnABrokenProvider() =>
            Gen.Select(AnyLinkCaller, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Enum<CustomAnswer>().Array[0, 3])
                .SampleAsync(async t =>
                {
                    var (caller, customStatus, gitHubLinked, atlassianOffered, answers) = t;
                    var aws = Substitute.For<IAwsIdentityLinkService>();
                    var azure = Substitute.For<IAzureIdentityLinkService>();
                    var custom = Substitute.For<ICustomAccountLinks>();
                    var credentials = Substitute.For<IProviderCredentialStore>();
                    var authorizer = new PolicyAuthorizer(policy => policy == Policies.Account && caller.Allowed);
                    var db = IdentityDb();
                    var gitHub = new GitHubIdentityLinkStore(db, TimeProvider.System);
                    var providers = answers.Select(_ => Custom(Guid.NewGuid())).ToList();
                    custom.LinkableAsync(Arg.Any<CancellationToken>()).Returns(providers);
                    for (int i = 0; i < providers.Count; i++)
                    {
                        var answer = answers[i];
                        custom.StatusAsync(providers[i].Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => Answer(answer));
                    }
                    credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
                        .Returns(atlassianOffered ? new AtlassianLinkAppRegistration("client") { SignInEnabled = true } : null);
                    if (caller.UserId is Guid owner && gitHubLinked)
                        await gitHub.SaveAsync(owner, 42, "octo");
                    // Someone else's link, which must never come back.
                    await gitHub.SaveAsync(Guid.NewGuid(), 7, "other");
                    var app = new ConnapseGitHubApp(
                        new ServiceCollection().AddSingleton(credentials).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                        Substitute.For<IHttpClientFactory>(), NullLogger<ConnapseGitHubApp>.Instance);

                    var read = await new ListAccountLinks(aws, azure, gitHub, new AtlassianIdentityLinkStore(db, TimeProvider.System), custom, app,
                        credentials, Unconfigured<SamlSignInSettings>(), Unconfigured<AzureAdSignInSettings>(), authorizer)
                        .RunAsync(caller.Context, new ListAccountLinksQuery(customStatus));

                    if (caller.UserId is not Guid userId)
                    {
                        read.Status.Should().Be(AccountLinkReadStatus.Forbidden);
                        aws.ReceivedCalls().Should().BeEmpty();
                        custom.ReceivedCalls().Should().BeEmpty();
                        return;
                    }
                    read.Status.Should().Be(AccountLinkReadStatus.Success);
                    var links = read.Result!;
                    await aws.Received(1).GetAsync(userId, Arg.Any<CancellationToken>());
                    await azure.Received(1).GetAsync(userId, Arg.Any<CancellationToken>());
                    links.GitHub?.Login.Should().Be("octo");
                    (links.GitHub is not null).Should().Be(gitHubLinked);
                    links.GitHubAvailable.Should().BeFalse("no GitHub App is stored");
                    links.Atlassian.Should().BeNull();
                    links.AtlassianAvailable.Should().Be(atlassianOffered);

                    // Every provider is listed; its status only when asked, and unknown when its server failed.
                    links.Custom.Select(c => c.Provider.Id).Should().Equal(providers.Select(p => p.Id));
                    links.Custom.Select(c => c.Linked).Should().Equal(answers.Select(a => !customStatus ? null
                        : a == CustomAnswer.Linked ? true : a == CustomAnswer.NotLinked ? (bool?)false : null));
                    if (!customStatus)
                        await custom.DidNotReceiveWithAnyArgs().StatusAsync(default, default, default);
                    else
                        foreach (var provider in providers)
                            await custom.Received(1).StatusAsync(provider.Id, userId, Arg.Any<CancellationToken>());
                }, iter: 500);

        /// <summary>Provider text: each name in any case, numbers, flag lists, "custom", and junk.</summary>
        private static readonly Gen<string> AnyLinkProvider = Gen.OneOf(
            Gen.OneOf(Gen.Const("AWS"), Gen.Const("aws"), Gen.Const("Azure"), Gen.Const("AZURE"), Gen.Const("GitHub"), Gen.Const("github")),
            Gen.Int[-1, 4].Select(n => n.ToString()),
            Gen.OneOf(Gen.Const("AWS,Azure"), Gen.Const("Azure,GitHub"), Gen.Const(" aws "), Gen.Const("")),
            Gen.OneOf(Gen.Const("custom"), Gen.Const("Custom")),
            Gen.String[Gen.Char[" aAbBsS-,"], 0, 6]);

        /// <summary>
        /// links.unlink over random provider text. Atlassian is left out: its delete takes a Postgres row lock
        /// the in-memory database can't, so its unlink is tested against Postgres in the integration suite.
        /// </summary>
        [Fact]
        [RandomInputTestOf(typeof(UnlinkAccount))]
        public Task Unlink_AnyProviderText_RemovesOnlyTheNamedLinkOfTheCaller() =>
            Gen.Select(AnyLinkCaller, AnyLinkProvider, Gen.Guid.Nullable(), Gen.Bool, Gen.Enum<CustomAnswer>(),
                    Gen.Bool, Gen.Bool)
                .SampleAsync(async t =>
                {
                    var (caller, provider, customId, linked, answer, awsChanged, gitHubLinked) = t;
                    var aws = Substitute.For<IAwsIdentityLinkService>();
                    var azure = Substitute.For<IAzureIdentityLinkService>();
                    var custom = Substitute.For<ICustomAccountLinks>();
                    var audit = Substitute.For<IAuditLogger>();
                    var authorizer = new PolicyAuthorizer(policy => policy == Policies.Person && caller.Allowed);
                    aws.DisconnectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                        .Returns(new AwsIdentityLinkDisconnectResult(linked && !awsChanged, awsChanged));
                    azure.DisconnectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(linked);
                    custom.UnlinkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => answer switch
                    {
                        CustomAnswer.Down => Task.FromException(new HttpRequestException("down")),
                        CustomAnswer.Refused => Task.FromException(new CustomSourceUnauthorizedException("refused")),
                        CustomAnswer.Missing => Task.FromException(new CustomProviderNotFoundException("gone")),
                        _ => Task.CompletedTask,
                    });
                    var db = IdentityDb();
                    var gitHub = new GitHubIdentityLinkStore(db, TimeProvider.System);
                    if (caller.UserId is Guid owner && gitHubLinked)
                        await gitHub.SaveAsync(owner, 42, "octo");

                    var unlinked = await new UnlinkAccount(aws, azure, gitHub, new GitHubLinkFlow(new MemoryCache(new MemoryCacheOptions())),
                        new AtlassianIdentityLinkStore(db, TimeProvider.System), new AtlassianLinkFlow(new MemoryCache(new MemoryCacheOptions())),
                        custom, Substitute.For<IConnectorScopeCache>(), Substitute.For<ISourceStore>(), Substitute.For<IConnectionStore>(), audit, authorizer)
                        .RunAsync(caller.Context, new UnlinkAccountRequest(provider, customId));

                    string? named = new[] { "AWS", "Azure", "GitHub" }.FirstOrDefault(n => string.Equals(n, provider, StringComparison.OrdinalIgnoreCase));
                    bool toCustom = string.Equals(provider, "custom", StringComparison.OrdinalIgnoreCase) && customId is not null;
                    AccountUnlinkStatus expected =
                        caller.UserId is null ? AccountUnlinkStatus.Forbidden
                        : toCustom ? answer switch
                        {
                            CustomAnswer.Missing => AccountUnlinkStatus.NotFound,
                            CustomAnswer.Down or CustomAnswer.Refused => AccountUnlinkStatus.Unavailable,
                            _ => AccountUnlinkStatus.Success,
                        }
                        : named is null ? AccountUnlinkStatus.InvalidProvider
                        : named == "AWS" ? (awsChanged ? AccountUnlinkStatus.Changed : linked ? AccountUnlinkStatus.Success : AccountUnlinkStatus.NotFound)
                        : named == "Azure" ? (linked ? AccountUnlinkStatus.Success : AccountUnlinkStatus.NotFound)
                        : gitHubLinked ? AccountUnlinkStatus.Success : AccountUnlinkStatus.NotFound;
                    unlinked.Status.Should().Be(expected);

                    // Only the named provider is touched, and only for the caller.
                    var userId = caller.UserId;
                    if (named == "AWS" && userId is Guid a)
                        await aws.Received(1).DisconnectAsync(a, Arg.Any<CancellationToken>());
                    else
                        await aws.DidNotReceiveWithAnyArgs().DisconnectAsync(default);
                    if (named == "Azure" && userId is Guid z)
                        await azure.Received(1).DisconnectAsync(z, Arg.Any<CancellationToken>());
                    else
                        await azure.DidNotReceiveWithAnyArgs().DisconnectAsync(default);
                    if (toCustom && userId is Guid c)
                        await custom.Received(1).UnlinkAsync(customId!.Value, c, Arg.Any<CancellationToken>());
                    else
                        await custom.DidNotReceiveWithAnyArgs().UnlinkAsync(default, default, default);
                    if (named == "GitHub" && userId is Guid g)
                        (await gitHub.GetAsync(g)).Should().BeNull();
                    else if (userId is Guid kept && gitHubLinked)
                        (await gitHub.GetAsync(kept)).Should().NotBeNull();

                    if (expected != AccountUnlinkStatus.Success)
                        audit.ReceivedCalls().Should().BeEmpty();
                }, iter: 1000);
    }

    // ── completeness ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryOperation_HasARandomInputTest()
    {
        var operations = typeof(OperationAttribute).Assembly.GetTypes()
            .Where(t => t.IsDefined(typeof(OperationAttribute), inherit: false))
            .ToHashSet();
        var marked = typeof(OperationRandomInputTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(AllDeclared))
            .SelectMany(m => m.GetCustomAttributes<RandomInputTestOfAttribute>().Select(a => (Method: m, a.Operation)))
            .ToList();

        // A marker counts only on a test that runs, samples generated inputs with CsCheck, and builds the
        // operation it names, so an empty, skipped or fixed-input test can't stand in for one.
        marked.Select(x => Problem(x.Method, x.Operation) is { } problem ? $"{x.Method.Name} ({x.Operation.Name}): {problem}" : null)
            .OfType<string>().Should().BeEmpty();

        var tested = marked.Select(x => x.Operation).ToHashSet();
        operations.Should().NotBeEmpty();
        operations.Except(tested).Select(t => t.Name).Should().BeEmpty("every operation needs a [RandomInputTestOf] test of its rules");
        tested.Except(operations).Select(t => t.Name).Should().BeEmpty("[RandomInputTestOf] must name an [Operation] class");
    }

    private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static string? Problem(MethodInfo test, Type operation)
    {
        if (test.GetCustomAttribute<FactAttribute>(inherit: true) is not { } fact)
            return "not a [Fact]";
        if (fact.Skip is not null)
            return "skipped";
        if (!Called(test).Any(c => c.DeclaringType == typeof(Check) && c.Name.StartsWith("Sample", StringComparison.Ordinal)))
            return "doesn't call CsCheck's Sample or SampleAsync";

        // The sampled body is a lambda, and an async lambda's code is in its state machine; the compiler
        // names both after the test.
        string tag = $"<{test.Name}>";
        var bodies = Nested(test.DeclaringType!).SelectMany(t => t.GetMethods(AllDeclared)
            .Where(m => t.Name.Contains(tag, StringComparison.Ordinal) || m.Name.Contains(tag, StringComparison.Ordinal)));
        return bodies.SelectMany(Called).Any(c => c is ConstructorInfo && c.DeclaringType == operation)
            ? null
            : $"its sampled body never constructs {operation.Name}";
    }

    private static IEnumerable<Type> Nested(Type type) =>
        type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(n => Nested(n).Prepend(n));

    /// <summary>The methods and constructors a method's IL calls; a token that doesn't resolve is skipped.</summary>
    private static IEnumerable<MethodBase> Called(MethodInfo method)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        for (int i = 0; i + 4 < il.Length; i++)
        {
            // call, callvirt and newobj, each followed by a four-byte method token.
            if (il[i] is not (0x28 or 0x6F or 0x73))
                continue;
            MethodBase? target = null;
            try
            {
                target = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1),
                    method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
            }
            catch (ArgumentException)
            {
            }
            if (target is not null)
                yield return target;
        }
    }
}
