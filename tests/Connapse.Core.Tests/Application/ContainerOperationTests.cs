using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

[Trait("Category", "Unit")]
public class ContainerOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IContainerStore _containers = Substitute.For<IContainerStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly VectorModelDiscovery _models = Substitute.ForPartsOf<VectorModelDiscovery>(
        Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), Substitute.For<ILogger<VectorModelDiscovery>>());

    public ContainerOperationTests() =>
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);

    private static Container Named(string name, int documents = 0) =>
        new(Guid.NewGuid().ToString(), name, null, Epoch, Epoch, documents);

    private void Deny(string policy) => _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), policy).Returns(false);

    [Fact]
    public async Task List_NotAReader_IsForbiddenAndReadsNothing()
    {
        Deny(Policies.Reader);

        var listed = await new ListContainers(_containers, _authorizer).RunAsync(Caller, new ListContainersQuery(0, 10));

        listed.Status.Should().Be(ContainerStatus.Forbidden);
        await _containers.DidNotReceiveWithAnyArgs().ListAsync();
    }

    [Theory]
    [InlineData(-1, 10, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task List_PageOutOfRange_IsInvalidPage(int skip, int take, string detail)
    {
        var listed = await new ListContainers(_containers, _authorizer).RunAsync(Caller, new ListContainersQuery(skip, take));

        listed.Status.Should().Be(ContainerStatus.InvalidPage);
        listed.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task List_MoreThanAPage_AsksForOneMoreAndSaysMoreFollow()
    {
        _containers.ListAsync(0, 3, Arg.Any<CancellationToken>()).Returns([Named("a"), Named("b"), Named("c")]);

        var listed = await new ListContainers(_containers, _authorizer).RunAsync(Caller, new ListContainersQuery(0, 2));

        listed.Result!.HasMore.Should().BeTrue();
        listed.Result.Items.Select(c => c.Name).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Get_ByName_LooksUpTheLowercasedNameAndQuotesItWhenMissing()
    {
        var read = await new GetContainer(_containers, _authorizer).RunAsync(Caller, ContainerKey.ByName(" Docs "));

        await _containers.Received(1).GetByNameAsync("docs", Arg.Any<CancellationToken>());
        read.Status.Should().Be(ContainerStatus.NotFound);
        read.Detail.Should().Be("Container ' Docs ' not found");
    }

    [Fact]
    public async Task Get_ById_Missing_NamesTheIdBare()
    {
        var id = Guid.NewGuid();

        var read = await new GetContainer(_containers, _authorizer).RunAsync(Caller, ContainerKey.ById(id));

        read.Detail.Should().Be($"Container {id} not found");
    }

    private CreateContainer Create() => new(_containers, _audit, _authorizer);

    [Fact]
    public async Task Create_NotAnEditor_IsForbiddenAndCreatesNothing()
    {
        Deny(Policies.Writer);

        var created = await Create().RunAsync(Caller, new CreateContainerRequest("docs"));

        created.Status.Should().Be(ContainerStatus.Forbidden);
        await _containers.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    [Theory]
    [InlineData("   ", "Container name is required")]
    [InlineData("Not Valid!", "Container name must be 2-128 characters, lowercase alphanumeric and hyphens, cannot start or end with a hyphen")]
    [InlineData("-docs", "Container name must be 2-128 characters, lowercase alphanumeric and hyphens, cannot start or end with a hyphen")]
    public async Task Create_BadName_IsInvalidOnTheNameField(string name, string detail)
    {
        var created = await Create().RunAsync(Caller, new CreateContainerRequest(name));

        created.Status.Should().Be(ContainerStatus.Invalid);
        created.Field.Should().Be("name");
        created.Detail.Should().Be(detail);
        await _containers.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    [Fact]
    public async Task Create_NameTaken_IsAConflict()
    {
        _containers.GetByNameAsync("docs", Arg.Any<CancellationToken>()).Returns(Named("docs"));

        var created = await Create().RunAsync(Caller, new CreateContainerRequest(" docs "));

        created.Status.Should().Be(ContainerStatus.Conflict);
        created.Detail.Should().Be("Container 'docs' already exists");
        await _containers.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    [Fact]
    public async Task Create_Valid_SavesTheTrimmedNameAndDescriptionAndAudits()
    {
        var made = Named("docs");
        _containers.CreateAsync(Arg.Any<CreateContainerRequest>(), Arg.Any<CancellationToken>()).Returns(made);

        var created = await Create().RunAsync(Caller, new CreateContainerRequest(" docs ", " about docs "));

        created.Result.Should().Be(made);
        await _containers.Received(1).CreateAsync(new CreateContainerRequest("docs", "about docs"), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("container.created", "container", made.Id,
            Arg.Is<object>(d => d.ToString() == new { Name = "docs" }.ToString()), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_RacedByAnotherCreate_IsAConflictWithoutAnAuditRow()
    {
        _containers.CreateAsync(Arg.Any<CreateContainerRequest>(), Arg.Any<CancellationToken>())
            .Returns<Container>(_ => throw new InvalidOperationException("Container 'docs' already exists."));

        var created = await Create().RunAsync(Caller, new CreateContainerRequest("docs"));

        created.Status.Should().Be(ContainerStatus.Conflict);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!);
    }

    private RenameContainer Rename() => new(_containers, _audit, _authorizer);

    [Fact]
    public async Task Rename_NothingToChange_IsInvalid()
    {
        var renamed = await Rename().RunAsync(Caller, Guid.NewGuid(), new ContainerChanges());

        renamed.Status.Should().Be(ContainerStatus.Invalid);
        await _containers.DidNotReceiveWithAnyArgs().UpdateAsync(default, default, default);
    }

    [Fact]
    public async Task Rename_ToAnotherContainersName_IsAConflict()
    {
        var current = Named("old");
        var id = Guid.Parse(current.Id);
        _containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(current);
        _containers.GetByNameAsync("taken", Arg.Any<CancellationToken>()).Returns(Named("taken"));

        var renamed = await Rename().RunAsync(Caller, id, new ContainerChanges(Name: "taken"));

        renamed.Status.Should().Be(ContainerStatus.Conflict);
        renamed.Field.Should().Be("name");
        await _containers.DidNotReceiveWithAnyArgs().UpdateAsync(default, default, default);
    }

    [Fact]
    public async Task Rename_Missing_IsNotFound()
    {
        var id = Guid.NewGuid();

        var renamed = await Rename().RunAsync(Caller, id, new ContainerChanges(Name: "new"));

        renamed.Status.Should().Be(ContainerStatus.NotFound);
        renamed.Detail.Should().Be($"Container {id} not found");
    }

    [Fact]
    public async Task Rename_Valid_SavesAndAuditsTheOldAndNewName()
    {
        var current = Named("old");
        var id = Guid.Parse(current.Id);
        var updated = current with { Name = "new" };
        _containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(current);
        _containers.UpdateAsync(id, "new", null, Arg.Any<CancellationToken>()).Returns(updated);

        var renamed = await Rename().RunAsync(Caller, id, new ContainerChanges(Name: " new "));

        renamed.Result.Should().Be(updated);
        await _audit.Received(1).LogAsync("container.updated", "container", current.Id,
            Arg.Is<object>(d => d.ToString() == new { From = "old", Name = "new" }.ToString()), Arg.Any<CancellationToken>());
    }

    private DeleteContainer Delete() => new(_containers, _audit, _authorizer);

    [Fact]
    public async Task Delete_NotEmpty_IsNotEmptyWithTheStoresReasonAndNoAuditRow()
    {
        var container = Named("docs", documents: 2);
        _containers.GetByNameAsync("docs", Arg.Any<CancellationToken>()).Returns(container);
        _containers.DeleteAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("Container 'docs' is not empty (2 documents). Delete all files first."));

        var deleted = await Delete().RunAsync(Caller, ContainerKey.Parse("docs"));

        deleted.Status.Should().Be(ContainerStatus.NotEmpty);
        deleted.Detail.Should().Contain("is not empty");
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!);
    }

    [Fact]
    public async Task Delete_Valid_DeletesByIdAndAudits()
    {
        var container = Named("docs");
        var id = Guid.Parse(container.Id);
        _containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(container);
        _containers.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var deleted = await Delete().RunAsync(Caller, ContainerKey.Parse(container.Id));

        deleted.Succeeded.Should().BeTrue();
        await _audit.Received(1).LogAsync("container.deleted", "container", container.Id,
            Arg.Is<object>(d => d.ToString() == new { Name = "docs" }.ToString()), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NotAnEditor_IsForbiddenAndReadsNothing()
    {
        Deny(Policies.Writer);

        var deleted = await Delete().RunAsync(Caller, ContainerKey.ById(Guid.NewGuid()));

        deleted.Status.Should().Be(ContainerStatus.Forbidden);
        _containers.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Stats_ReturnsTheContainersCountsAndModels()
    {
        var container = Named("docs");
        var id = Guid.Parse(container.Id);
        var stats = new ContainerStats(3, 2, 1, 0, 40, 1024, Epoch);
        _containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(container);
        _documents.GetContainerStatsAsync(id, Arg.Any<CancellationToken>()).Returns(stats);
        _models.GetModelsAsync(id, Arg.Any<CancellationToken>()).Returns(new List<EmbeddingModelInfo> { new("m", 768, 40) });

        var read = await new GetContainerStats(_containers, _documents, _models, _authorizer).RunAsync(Caller, ContainerKey.ById(id));

        read.Result!.Stats.Should().Be(stats);
        read.Result.Models.Should().ContainSingle().Which.ModelId.Should().Be("m");
    }

    [Fact]
    public async Task Stats_NotAReader_IsForbidden()
    {
        Deny(Policies.Reader);

        var read = await new GetContainerStats(_containers, _documents, _models, _authorizer).RunAsync(Caller, ContainerKey.ById(Guid.NewGuid()));

        read.Status.Should().Be(ContainerStatus.Forbidden);
        _documents.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task GetSettings_NoOverrides_IsAnEmptySetSoEverySettingIsGlobal()
    {
        var id = Guid.NewGuid();
        _containers.ExistsAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var read = await new GetContainerSettings(_containers, _authorizer).RunAsync(Caller, id);

        read.Succeeded.Should().BeTrue();
        read.Result.Should().BeEquivalentTo(new ContainerSettingsOverrides());
    }

    [Fact]
    public async Task GetSettings_Missing_IsNotFound()
    {
        var read = await new GetContainerSettings(_containers, _authorizer).RunAsync(Caller, Guid.NewGuid());

        read.Status.Should().Be(ContainerStatus.NotFound);
    }

    [Fact]
    public async Task UpdateSettings_NotAnEditor_IsForbiddenAndSavesNothing()
    {
        Deny(Policies.Writer);

        var saved = await new UpdateContainerSettings(_containers, _audit, _authorizer)
            .RunAsync(Caller, Guid.NewGuid(), new ContainerSettingsOverrides());

        saved.Status.Should().Be(ContainerStatus.Forbidden);
        await _containers.DidNotReceiveWithAnyArgs().SaveSettingsOverridesAsync(default, default!);
    }

    [Fact]
    public async Task UpdateSettings_Valid_SavesTheOverridesAndAudits()
    {
        var id = Guid.NewGuid();
        _containers.ExistsAsync(id, Arg.Any<CancellationToken>()).Returns(true);
        var overrides = new ContainerSettingsOverrides { Search = new SearchSettings { TopK = 7 } };

        var saved = await new UpdateContainerSettings(_containers, _audit, _authorizer).RunAsync(Caller, id, overrides);

        saved.Result.Should().BeSameAs(overrides);
        await _containers.Received(1).SaveSettingsOverridesAsync(id, overrides, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("container.settings_updated", "container", id.ToString(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetSettings_Valid_SavesAnEmptySetAndAudits()
    {
        var id = Guid.NewGuid();
        _containers.ExistsAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var cleared = await new ResetContainerSettings(_containers, _audit, _authorizer).RunAsync(Caller, id);

        cleared.Succeeded.Should().BeTrue();
        await _containers.Received(1).SaveSettingsOverridesAsync(id,
            Arg.Is<ContainerSettingsOverrides>(o => o == new ContainerSettingsOverrides()),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("container.settings_reset", "container", id.ToString(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetSettings_Missing_IsNotFoundAndSavesNothing()
    {
        var cleared = await new ResetContainerSettings(_containers, _audit, _authorizer).RunAsync(Caller, Guid.NewGuid());

        cleared.Status.Should().Be(ContainerStatus.NotFound);
        await _containers.DidNotReceiveWithAnyArgs().SaveSettingsOverridesAsync(default, default!);
    }

    private readonly IReindexService _reindex = Substitute.For<IReindexService>();

    private Guid Existing()
    {
        var id = Guid.NewGuid();
        _containers.ExistsAsync(id, Arg.Any<CancellationToken>()).Returns(true);
        return id;
    }

    [Fact]
    public async Task Reindex_NotAWriter_IsForbiddenAndReindexesNothing()
    {
        Deny(Policies.Writer);

        var ran = await new ReindexContainer(_containers, _reindex, _authorizer).RunAsync(Caller, Existing(), new ContainerReindexQuery());

        ran.Status.Should().Be(ContainerStatus.Forbidden);
        _reindex.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, null, "maxDocuments")]
    [InlineData(-3, null, "maxDocuments")]
    [InlineData(null, "not-an-id", "continueAfter")]
    public async Task Reindex_BadPage_IsInvalidReindexOnItsField(int? max, string? continueAfter, string field)
    {
        var ran = await new ReindexContainer(_containers, _reindex, _authorizer)
            .RunAsync(Caller, Existing(), new ContainerReindexQuery(MaxDocuments: max, ContinueAfter: continueAfter));

        ran.Status.Should().Be(ContainerStatus.InvalidReindex);
        ran.Field.Should().Be(field);
        _reindex.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Reindex_Missing_IsNotFoundBeforeItsPageIsChecked()
    {
        var ran = await new ReindexContainer(_containers, _reindex, _authorizer)
            .RunAsync(Caller, Guid.NewGuid(), new ContainerReindexQuery(MaxDocuments: 0));

        ran.Status.Should().Be(ContainerStatus.NotFound);
    }

    [Fact]
    public async Task Reindex_Valid_AsksTheServiceForThisContainerWithItsDefaults()
    {
        var id = Existing();
        _reindex.ReindexAsync(Arg.Any<ReindexOptions>(), Arg.Any<CancellationToken>()).Returns(new ReindexResult { BatchId = "b1" });

        var ran = await new ReindexContainer(_containers, _reindex, _authorizer).RunAsync(Caller, id, new ContainerReindexQuery(DryRun: true));

        ran.Result!.BatchId.Should().Be("b1");
        await _reindex.Received(1).ReindexAsync(
            Arg.Is<ReindexOptions>(o => o.ContainerId == id.ToString() && !o.Force && o.DetectSettingsChanges && o.DryRun),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryFailed_Valid_EnqueuesAndAuditsTheCount()
    {
        var id = Existing();
        _reindex.RetryFailedAsync(id, Arg.Any<CancellationToken>()).Returns(3);

        var retried = await new RetryFailedContainerDocuments(_containers, _reindex, _audit, _authorizer).RunAsync(Caller, id);

        retried.Result.Should().Be(3);
        await _audit.Received(1).LogAsync("container.retry_failed", "container", id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryFailed_Missing_IsNotFoundAndRetriesNothing()
    {
        var retried = await new RetryFailedContainerDocuments(_containers, _reindex, _audit, _authorizer).RunAsync(Caller, Guid.NewGuid());

        retried.Status.Should().Be(ContainerStatus.NotFound);
        _reindex.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_RegistersAndQueuesOnlyFilesWithNoDocument()
    {
        var container = Named("box");
        var id = Guid.Parse(container.Id);
        _containers.GetAsync(id, Arg.Any<CancellationToken>()).Returns(container);
        var connector = Substitute.For<IConnector>();
        connector.ListFilesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
            [new ConnectorFile("known.md", 1, Epoch, null), new ConnectorFile("/new.pdf", 2, Epoch, null)]);
        var storage = Substitute.For<IManagedStorageProvider>();
        storage.CreateConnector(container.Id).Returns(connector);
        _documents.ListAsync(id, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new Document("d1", container.Id, "known.md", null, "/known.md", 1, Epoch, [])]);
        var queue = Substitute.For<IIngestionQueue>();
        var chunking = Substitute.For<IOptionsMonitor<ChunkingSettings>>();
        chunking.CurrentValue.Returns(new ChunkingSettings());

        var synced = await new ReconcileContainer(_containers, storage, _documents, queue, chunking, _audit, _authorizer).RunAsync(Caller, id);

        synced.Result.Should().BeEquivalentTo(new { TotalFiles = 2, EnqueuedCount = 1, SkippedCount = 1 });
        await _documents.Received(1).StoreAsync(Arg.Is<Document>(d => d.Path == "/new.pdf" && d.ContentType == "application/pdf"), Arg.Any<CancellationToken>());
        await queue.Received(1).EnqueueAsync(Arg.Is<IngestionJob>(j => j.Options.Path == "/new.pdf" && j.BatchId == synced.Result!.BatchId), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("container.synced", "container", id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reconcile_NotAWriter_IsForbiddenAndListsNothing()
    {
        Deny(Policies.Writer);
        var storage = Substitute.For<IManagedStorageProvider>();

        var synced = await new ReconcileContainer(_containers, storage, _documents, Substitute.For<IIngestionQueue>(),
            Substitute.For<IOptionsMonitor<ChunkingSettings>>(), _audit, _authorizer).RunAsync(Caller, Guid.NewGuid());

        synced.Status.Should().Be(ContainerStatus.Forbidden);
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task RegenerateSummaries_NotAnAdmin_IsForbiddenAndQueuesNothing()
    {
        Deny(Policies.Admin);
        var rollup = Substitute.For<ISummaryRollup>();

        var queued = await new RegenerateContainerSummaries(_containers, rollup, _audit, _authorizer).RunAsync(Caller, Existing());

        queued.Status.Should().Be(ContainerStatus.Forbidden);
        rollup.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task RegenerateSummaries_Valid_QueuesTheRollupAndAnswersItsJob()
    {
        var id = Existing();
        var rollup = Substitute.For<ISummaryRollup>();
        rollup.Enqueue(id).Returns("job-7");

        var queued = await new RegenerateContainerSummaries(_containers, rollup, _audit, _authorizer).RunAsync(Caller, id);

        queued.Result.Should().Be(new SummaryRegeneration("job-7"));
        await _audit.Received(1).LogAsync("container.summaries_regenerated", "container", id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearSummaries_Valid_ClearsTheDocumentsAndTheContainerAndAudits()
    {
        var id = Existing();
        _documents.ClearDocumentSummariesAsync(id, Arg.Any<CancellationToken>()).Returns(4);

        var cleared = await new ClearContainerSummaries(_containers, _documents, _audit, _authorizer).RunAsync(Caller, id);

        cleared.Result.Should().Be(4);
        await _containers.Received(1).UpdateSummaryAsync(id, null, null, null, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("container.summaries_cleared", "container", id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearSummaries_Missing_IsNotFoundAndClearsNothing()
    {
        var cleared = await new ClearContainerSummaries(_containers, _documents, _audit, _authorizer).RunAsync(Caller, Guid.NewGuid());

        cleared.Status.Should().Be(ContainerStatus.NotFound);
        _documents.ReceivedCalls().Should().BeEmpty();
    }
}
