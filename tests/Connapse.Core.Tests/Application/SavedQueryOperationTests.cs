using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Tables;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using CsCheck;
using FluentAssertions;
using NSubstitute;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-311: admins save queries for agents, with typed parameters, some filled from the caller's login. Saving and
/// deleting are for admins; listing and running are for readers and cover only queries over tables the caller may
/// read, so a query over an unreadable table fails like a missing one. A login value is never taken from the request,
/// and a caller without it is refused. Answers are marked trusted.
/// </summary>
[Trait("Category", "Unit")]
public class SavedQueryOperationTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TableQueryResult Answer = new(["a"], [["x"]], false, null);

    private static OperationContext Person(string? email = "alice@example.com", Guid? id = null)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, (id ?? Guid.NewGuid()).ToString()), new(ClaimTypes.Name, "alice")];
        if (email is not null)
            claims.Add(new Claim(ClaimTypes.Email, email));
        return new OperationContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), OperationSurface.Mcp);
    }

    private static OperationContext Agent(Guid onBehalfOf) => new(new ClaimsPrincipal(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, "bot"),
        new Claim(ClaimTypes.Email, "spoofed@example.com"),
        new Claim(SearchPrincipal.ActorTypeClaim, "agent"),
        new Claim(SearchPrincipal.OnBehalfOfClaim, onBehalfOf.ToString()),
    ], "test")), OperationSurface.Mcp);

    private static JsonElement Json(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>A container with one table and its saved queries, every collaborator faked, set up by the switches given.</summary>
    private sealed class World
    {
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly ITableStore Tables = Substitute.For<ITableStore>();
        public readonly ITableQueryService Queries = Substitute.For<ITableQueryService>();
        public readonly IDocumentStore Documents = Substitute.For<IDocumentStore>();
        public readonly IDocumentAccess Access = Substitute.For<IDocumentAccess>();
        public readonly IMemoryService Memory = Substitute.For<IMemoryService>();
        public readonly FakeSavedQueries Saved = new();
        public readonly PolicyAuthorizer Authorizer;
        public readonly Guid ContainerId = Guid.NewGuid();
        public readonly Guid TableId = Guid.NewGuid();
        public readonly Guid DocumentId = Guid.NewGuid();

        public World(bool reader = true, bool admin = true, ContainerKind? kind = ContainerKind.Documents, bool tableExists = true,
            bool readable = true, bool checkFails = false)
        {
            Authorizer = new PolicyAuthorizer(p => p == Policies.Admin ? admin : p == Policies.Reader ? reader : true);
            if (kind is { } k)
                Containers.GetAsync(ContainerId, Arg.Any<CancellationToken>())
                    .Returns(new Container(ContainerId.ToString(), "c", null, Epoch, Epoch, Kind: k));
            TableInfo table = new(TableId, ContainerId, "sales", null, DocumentId,
                [new TableColumn("region", TableColumnType.Text), new TableColumn("owner", TableColumnType.Text)],
                3, false, [], "tables_x", "t_x", 1, Epoch, Epoch);
            if (tableExists)
            {
                Tables.GetAsync(ContainerId, TableId, Arg.Any<CancellationToken>()).Returns(table);
                Tables.ListAsync(ContainerId, Arg.Any<CancellationToken>()).Returns([table]);
            }
            else
            {
                Tables.ListAsync(ContainerId, Arg.Any<CancellationToken>()).Returns([]);
            }
            Documents.GetAsync(DocumentId.ToString(), Arg.Any<CancellationToken>())
                .Returns(new Document(DocumentId.ToString(), ContainerId.ToString(), "t.csv", "text/csv", "/t.csv", 1, Epoch, []));
            if (checkFails)
                Access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
                    .Returns<IReadOnlySet<string>>(_ => throw new HttpRequestException("down"));
            else
                Access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
                    .Returns(readable ? new HashSet<string> { DocumentId.ToString() } : new HashSet<string>());
            Queries.ReadAsync(default, default!, default!, default).ReturnsForAnyArgs(Answer);
            Queries.AggregateAsync(default, default!, default!, default).ReturnsForAnyArgs(Answer);
        }

        public SavedQueryDefinition Mine(bool login = true) => new(
            "my_sales",
            "Sales of a region that the caller owns.",
            SavedQueryKind.Read,
            $$$$"""
            {"table": "{{{{TableId}}}}", "where": {"and": [{"column": "region", "op": "=", "value": {"param": "region"}}
              {{{{(login ? ", {\"column\": \"owner\", \"op\": \"=\", \"value\": {\"param\": \"me\"}}" : "")}}}}]}}
            """,
            login
                ? [new SavedQueryParameter("region", SavedQueryParameterType.Text, Default: "\"west\""),
                   new SavedQueryParameter("me", SavedQueryParameterType.Text, FromLogin: SavedQueryLoginField.Email)]
                : [new SavedQueryParameter("region", SavedQueryParameterType.Text, Default: "\"west\"")]);

        public void Store(SavedQueryDefinition definition) =>
            Saved.Items[(ContainerId, definition.Name)] = new SavedQuery(ContainerId, definition, "admin", Epoch);

        public SaveTableQuery Save => new(Containers, Tables, Saved, Authorizer);
        public DeleteTableQuery Delete => new(Containers, Saved, Authorizer);
        public ListTableQueries List => new(Containers, Tables, Saved, Documents, Access, Authorizer);
        public RunTableQuery Run => new(Containers, Tables, Queries, Saved, Documents, Access, Authorizer, Memory);
    }

    internal sealed class FakeSavedQueries : ISavedQueryStore
    {
        public readonly Dictionary<(Guid, string), SavedQuery> Items = [];
        public int Saves;

        public Task<IReadOnlyList<SavedQuery>> ListAsync(Guid containerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SavedQuery>>(Items.Values.Where(q => q.ContainerId == containerId).OrderBy(q => q.Name).ToList());

        public Task<SavedQuery?> GetAsync(Guid containerId, string name, CancellationToken ct = default) =>
            Task.FromResult(Items.GetValueOrDefault((containerId, name)));

        public Task<SavedQuery> SaveAsync(SavedQuery query, CancellationToken ct = default)
        {
            Saves++;
            Items[(query.ContainerId, query.Name)] = query;
            return Task.FromResult(query);
        }

        public Task<bool> DeleteAsync(Guid containerId, string name, CancellationToken ct = default) =>
            Task.FromResult(Items.Remove((containerId, name)));
    }

    [Fact]
    public async Task Save_Admin_StoresTheQueryWithWhoSavedIt()
    {
        World world = new();

        var saved = await world.Save.RunAsync(Person(), world.ContainerId, world.Mine());

        saved.Succeeded.Should().BeTrue(saved.Detail);
        saved.Result!.SavedBy.Should().Be("alice");
        world.Saved.Items.Should().ContainKey((world.ContainerId, "my_sales"));
    }

    [Fact]
    public async Task Save_NotAnAdmin_IsForbiddenAndStoresNothing()
    {
        World world = new(admin: false);

        var saved = await world.Save.RunAsync(Person(), world.ContainerId, world.Mine());

        saved.Status.Should().Be(TableStatus.Forbidden);
        world.Saved.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Save_BadDefinition_IsInvalidRequestWithWhatToFix()
    {
        World world = new();

        var saved = await world.Save.RunAsync(Person(), world.ContainerId,
            world.Mine() with { Parameters = [new SavedQueryParameter("region", SavedQueryParameterType.Text)] });

        saved.Status.Should().Be(TableStatus.InvalidRequest);
        saved.Detail.Should().Contain("\"me\", which isn't declared");
        world.Saved.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Save_MissingTableOrMemoryContainer_IsRefused()
    {
        World noTable = new(tableExists: false);
        World memory = new(kind: ContainerKind.Memory);

        (await noTable.Save.RunAsync(Person(), noTable.ContainerId, noTable.Mine())).Status.Should().Be(TableStatus.TableNotFound);
        (await memory.Save.RunAsync(Person(), memory.ContainerId, memory.Mine())).Status.Should().Be(TableStatus.MemoryContainer);
    }

    [Fact]
    public async Task Run_Person_FillsTheirEmailAndMarksTheAnswerTrusted()
    {
        World world = new();
        world.Store(world.Mine());

        var ran = await world.Run.RunAsync(Person("alice@example.com"), world.ContainerId, "my_sales", Json("""{"region": "east"}"""));

        ran.Succeeded.Should().BeTrue(ran.Detail);
        ran.Result!.Trusted.Should().BeTrue();
        ran.Result.Query.Should().Be("my_sales");
        ran.Result.SavedBy.Should().Be("admin");
        ran.Result.Result.Should().Be(Answer);
        await world.Queries.Received(1).ReadAsync(world.ContainerId, Arg.Any<TableAccess>(),
            Arg.Is<TableReadRequest>(r => r.Where!.ToString()!.Length > 0 && Owner(r) == "alice@example.com" && Region(r) == "east"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_SendsTheLoginParameter_IsRefusedAndQueriesNothing()
    {
        World world = new();
        world.Store(world.Mine());

        var ran = await world.Run.RunAsync(Person(), world.ContainerId, "my_sales", Json("""{"me": "bob@example.com"}"""));

        ran.Status.Should().Be(TableStatus.InvalidRequest);
        ran.Detail.Should().Contain("filled from your login");
        world.Queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Run_AgentKeyNeedingAnEmail_IsRefusedEvenWithAnEmailClaim()
    {
        World world = new();
        world.Store(world.Mine());

        var ran = await world.Run.RunAsync(Agent(Guid.NewGuid()), world.ContainerId, "my_sales", null);

        ran.Status.Should().Be(TableStatus.LoginValueMissing);
        ran.Detail.Should().Contain("email address");
        world.Queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Run_AgentKeyWithUserIdParameter_FillsTheUserItActsFor()
    {
        World world = new();
        Guid user = Guid.NewGuid();
        SavedQueryDefinition byId = world.Mine() with
        {
            Parameters = [new SavedQueryParameter("region", SavedQueryParameterType.Text, Default: "\"west\""),
                          new SavedQueryParameter("me", SavedQueryParameterType.Text, FromLogin: SavedQueryLoginField.UserId)],
        };
        world.Store(byId);

        var ran = await world.Run.RunAsync(Agent(user), world.ContainerId, "my_sales", null);

        ran.Succeeded.Should().BeTrue(ran.Detail);
        await world.Queries.Received(1).ReadAsync(world.ContainerId, Arg.Any<TableAccess>(),
            Arg.Is<TableReadRequest>(r => Owner(r) == user.ToString() && Region(r) == "west"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_UnreadableAndMissing_FailAlike()
    {
        World unreadable = new(readable: false);
        unreadable.Store(unreadable.Mine());
        World missing = new();

        var hidden = await unreadable.Run.RunAsync(Person(), unreadable.ContainerId, "my_sales", null);
        var gone = await missing.Run.RunAsync(Person(), missing.ContainerId, "my_sales", null);

        hidden.Status.Should().Be(TableStatus.SavedQueryNotFound);
        gone.Status.Should().Be(TableStatus.SavedQueryNotFound);
        hidden.Detail.Should().Be(gone.Detail);
        unreadable.Queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task List_OnlyQueriesOverReadableTables()
    {
        World readable = new();
        readable.Store(readable.Mine());
        World unreadable = new(readable: false);
        unreadable.Store(unreadable.Mine());

        (await readable.List.RunAsync(Person(), readable.ContainerId)).Result.Should().ContainSingle(q => q.Name == "my_sales");
        (await unreadable.List.RunAsync(Person(), unreadable.ContainerId)).Result.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_Admin_RemovesItAndAMissingOneIsNotFound()
    {
        World world = new();
        world.Store(world.Mine());

        (await world.Delete.RunAsync(Person(), world.ContainerId, "my_sales")).Succeeded.Should().BeTrue();
        (await world.Delete.RunAsync(Person(), world.ContainerId, "my_sales")).Status.Should().Be(TableStatus.SavedQueryNotFound);
    }

    private static string? Value(TableReadRequest request, string column) =>
        ((TableAndFilter)request.Where!).All.OfType<TableCondition>().Where(c => c.Field.Column == column)
            .Select(c => ((JsonElement)c.Values[0]!).GetString()).FirstOrDefault();

    private static string? Owner(TableReadRequest request) => Value(request, "owner");

    private static string? Region(TableReadRequest request) => Value(request, "region");

    // Random inputs: each operation either succeeds only when every rule holds, or says the first rule broken.

    private static readonly Gen<ContainerKind?> Kinds = Gen.OneOfConst<ContainerKind?>(null, ContainerKind.Documents, ContainerKind.Memory);

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(SaveTableQuery))]
    public Task Save_AnyAdminContainerTableAndDefinition_SavesOnlyWhenEveryRuleHolds() =>
        Gen.Select(Gen.Bool, Kinds, Gen.Bool, Gen.Int[0, 3]).SampleAsync(async s =>
        {
            var (admin, kind, tableExists, broken) = s;
            World world = new(admin: admin, kind: kind, tableExists: tableExists);
            SavedQueryDefinition definition = broken switch
            {
                1 => world.Mine() with { Name = "Bad Name" },
                2 => world.Mine() with { Parameters = [] },
                3 => world.Mine() with { Request = "{\"table\": 5}" },
                _ => world.Mine(),
            };

            var saved = await new SaveTableQuery(world.Containers, world.Tables, world.Saved, world.Authorizer).RunAsync(Person(), world.ContainerId, definition);

            TableStatus expected = !admin ? TableStatus.Forbidden
                : kind is null ? TableStatus.ContainerNotFound
                : kind == ContainerKind.Memory ? TableStatus.MemoryContainer
                : broken != 0 ? TableStatus.InvalidRequest
                : !tableExists ? TableStatus.TableNotFound
                : TableStatus.Success;
            saved.Status.Should().Be(expected);
            world.Saved.Saves.Should().Be(expected == TableStatus.Success ? 1 : 0);
        }, iter: 1000);

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(DeleteTableQuery))]
    public Task Delete_AnyAdminContainerAndName_DeletesOnlyAnExistingQueryForAnAdmin() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool, Gen.String).SampleAsync(async s =>
        {
            var (admin, containerExists, stored, junk) = s;
            World world = new(admin: admin, kind: containerExists ? ContainerKind.Documents : null);
            if (stored)
                world.Store(world.Mine());
            string name = stored ? "my_sales" : junk;

            var deleted = await new DeleteTableQuery(world.Containers, world.Saved, world.Authorizer).RunAsync(Person(), world.ContainerId, name);

            TableStatus expected = !admin ? TableStatus.Forbidden
                : !containerExists ? TableStatus.ContainerNotFound
                : stored ? TableStatus.Success
                : TableStatus.SavedQueryNotFound;
            deleted.Status.Should().Be(expected);
            world.Saved.Items.ContainsKey((world.ContainerId, "my_sales")).Should().Be(stored && expected != TableStatus.Success);
        }, iter: 1000);

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(ListTableQueries))]
    public Task List_AnyCallerContainerAndAccess_ListsAQueryOnlyWhenItsTableIsReadable() =>
        Gen.Select(Gen.Bool, Kinds, Gen.Bool, Gen.Bool, Gen.Bool).SampleAsync(async s =>
        {
            var (reader, kind, tableExists, readable, checkFails) = s;
            World world = new(reader: reader, kind: kind, tableExists: tableExists, readable: readable, checkFails: checkFails);
            world.Store(world.Mine());

            var listed = await new ListTableQueries(world.Containers, world.Tables, world.Saved, world.Documents, world.Access, world.Authorizer).RunAsync(Person(), world.ContainerId);

            TableStatus expected = !reader ? TableStatus.Forbidden
                : kind is null ? TableStatus.ContainerNotFound
                : kind == ContainerKind.Memory ? TableStatus.Success
                : checkFails && tableExists ? TableStatus.AccessUnchecked
                : TableStatus.Success;
            listed.Status.Should().Be(expected);
            if (listed.Succeeded)
                listed.Result!.Count.Should().Be(kind == ContainerKind.Documents && tableExists && readable && !checkFails ? 1 : 0);
        }, iter: 1000);

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(RunTableQuery))]
    public Task Run_AnyCallerQueryArgumentsAndAccess_RunsOnlyAReadableQueryWithTheCallersLogin() =>
        Gen.Select(Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool, Gen.Bool), Gen.Select(Gen.Bool, Gen.Bool, Gen.Int[0, 4])).SampleAsync(async s =>
        {
            var ((reader, stored, tableExists, readable), (agent, hasEmail, argumentsKind)) = s;
            World world = new(reader: reader, tableExists: tableExists, readable: readable);
            if (stored)
                world.Store(world.Mine());
            JsonElement? arguments = argumentsKind switch
            {
                0 => null,
                1 => Json("""{"region": "east"}"""),
                2 => Json("""{"region": 7}"""),
                3 => Json("""{"me": "mallory@example.com"}"""),
                _ => Json("""{"unknown": "x"}"""),
            };
            OperationContext caller = agent ? Agent(Guid.NewGuid()) : Person(hasEmail ? "alice@example.com" : null);

            var ran = await new RunTableQuery(world.Containers, world.Tables, world.Queries, world.Saved, world.Documents, world.Access, world.Authorizer, world.Memory).RunAsync(caller, world.ContainerId, "my_sales", arguments);

            TableStatus expected = !reader ? TableStatus.Forbidden
                : !stored || !tableExists || !readable ? TableStatus.SavedQueryNotFound
                : agent || !hasEmail ? TableStatus.LoginValueMissing
                : argumentsKind >= 2 ? TableStatus.InvalidRequest
                : TableStatus.Success;
            ran.Status.Should().Be(expected);
            if (expected == TableStatus.Success)
            {
                ran.Result!.Trusted.Should().BeTrue();
                await world.Queries.Received(1).ReadAsync(world.ContainerId, Arg.Any<TableAccess>(),
                    Arg.Is<TableReadRequest>(r => Owner(r) == "alice@example.com"), Arg.Any<CancellationToken>());
            }
            else
            {
                world.Queries.ReceivedCalls().Should().BeEmpty();
            }
        }, iter: 1000);
}
