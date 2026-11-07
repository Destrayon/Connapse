using System.Reflection;
using Bunit;
using Connapse.Application.Extensions;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using Connapse.Web.Components.Pages;
using Connapse.Web.Components.Shared;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>The page's side of a folder upload: wwwroot/js/fileDrop.js runs it and calls these back (#394).</summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class FileBrowserFolderUploadTests : IDisposable
{
    private const string Module = "./js/fileDrop.js";
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly BunitContext ctx = new();
    private readonly IContainerStore containers = ContainerStoreFakes.Create();
    private readonly IDocumentStore documents = Substitute.For<IDocumentStore>();
    private readonly IFileTypeValidator fileTypes = Substitute.For<IFileTypeValidator>();
    private readonly IngestionProgressNotifier notifier = new();
    private readonly BunitJSModuleInterop module;
    private readonly JSRuntimeInvocationHandler<bool> pickerSupport;
    private readonly Container container = new(Guid.NewGuid().ToString(), "docs", null, Epoch, Epoch, 0, Kind: ContainerKind.Documents);

    public FileBrowserFolderUploadTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        module = ctx.JSInterop.SetupModule(Module);
        module.Mode = JSRuntimeMode.Loose;
        pickerSupport = module.Setup<bool>("supportsFolderPicker");

        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(default!, default!).ReturnsForAnyArgs(true);
        var models = Substitute.For<VectorModelDiscovery>(
            Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), NullLogger<VectorModelDiscovery>.Instance);
        models.GetModelsAsync(default, default).ReturnsForAnyArgs(new List<EmbeddingModelInfo>());
        documents.GetContainerStatsAsync(default, default).ReturnsForAnyArgs(new ContainerStats(0, 0, 0, 0, 0, 0, null));
        containers.GetAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>()).Returns(container);
        containers.GetByNameAsync("docs", Arg.Any<CancellationToken>()).Returns(container);

        ctx.Services.AddSingleton(containers);
        ctx.Services.AddSingleton(documents);
        ctx.Services.AddSingleton(fileTypes);
        ctx.Services.AddSingleton(authorizer);
        ctx.Services.AddSingleton(models);
        ctx.Services.AddSingleton(notifier);
        ctx.Services.AddOptions();
        ctx.Services.AddApplication();
        // The page injects every container and file operation; the ones these tests don't reach get bare stand-ins.
        foreach (var port in typeof(IContainerStore).Assembly.GetTypes().Concat(typeof(IOperationAuthorizer).Assembly.GetTypes())
                     .Where(t => t is { IsInterface: true, IsGenericTypeDefinition: false, IsPublic: true }
                         && t.Namespace is "Connapse.Core.Interfaces" or "Connapse.Application.Ports"))
            ctx.Services.TryAddSingleton(port, _ => Substitute.For([port], []));
    }

    public void Dispose() => ctx.Dispose();

    private void Notify(DocumentStatusChangedEvent ev)
        => typeof(IngestionProgressNotifier).GetMethod("NotifyStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(notifier, [ev]);

    private IRenderedComponent<FileBrowser> RenderPage(bool pickerWorks = true, string? role = null)
    {
        var auth = ctx.AddAuthorization().SetAuthorized("pat");
        if (role is not null) auth.SetRoles(role);
        pickerSupport.SetResult(pickerWorks);
        var cut = ctx.Render<FileBrowser>(p => p.Add(b => b.ContainerId, container.Id));
        cut.WaitForAssertion(() => cut.Find("#fileDropZone"));
        return cut;
    }

    private static FolderUploadRun Run(IRenderedComponent<FileBrowser> cut) => cut.FindComponent<FolderUploadRun>().Instance;

    private static string Finished(int total, int succeeded, int failed)
        => $$"""{"stage":"finished","found":{{total}},"total":{{total}},"succeeded":{{succeeded}},"failed":{{failed}},"waiting":false,"cancelled":false,"stoppedBecause":null}""";

    [Fact]
    public void Page_BrowserHasAFolderPicker_OffersUploadFolder()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.Find("label[for=folderUploadInput]").TextContent.Should().Contain("Upload folder"));
        cut.Find("#folderUploadInput").HasAttribute("webkitdirectory").Should().BeTrue();
    }

    [Fact]
    public void Page_BrowserHasNoFolderPicker_LeavesTheControlOutButKeepsUpload()
    {
        var cut = RenderPage(pickerWorks: false);

        cut.WaitForState(() => module.Invocations["supportsFolderPicker"].Count > 0);
        cut.FindAll("#folderUploadInput").Should().BeEmpty();
        cut.Find("#fileUploadInput");
    }

    [Fact]
    public void Page_Viewer_IsNotOfferedUploadFolder()
    {
        var cut = RenderPage(role: "Viewer");

        cut.FindAll("#folderUploadInput").Should().BeEmpty();
    }

    [Fact]
    public void GetSupportedExtensions_AnswersWhatTheServerCanRead()
    {
        fileTypes.SupportedExtensions.Returns(new HashSet<string> { ".md", ".pdf" });
        var cut = RenderPage();

        Run(cut).GetSupportedExtensions().Should().BeEquivalentTo(".md", ".pdf");
    }

    [Fact]
    public async Task UpdateFolderUpload_Uploading_ShowsThePanel()
    {
        var cut = RenderPage();

        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload("""{"stage":"uploading","total":10,"succeeded":4,"failed":1}"""));

        cut.Find("#folder-upload-panel").TextContent.Should().Contain("5 of 10 files");
    }

    [Fact]
    public async Task UpdateFolderUpload_Finishes_ReReadsTheContainerOnce()
    {
        var cut = RenderPage();
        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload("""{"stage":"uploading","total":2}"""));
        documents.ClearReceivedCalls();

        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload(Finished(2, 2, 0)));
        await documents.ReceivedWithAnyArgs(1).GetContainerStatsAsync(default, default);

        documents.ClearReceivedCalls();
        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload(Finished(2, 2, 0)));
        await documents.DidNotReceiveWithAnyArgs().GetContainerStatsAsync(default, default);
    }

    [Fact]
    public async Task AddFolderUploadFailures_FinishedReportedAgain_ShowsEachFailureOnce()
    {
        var cut = RenderPage();
        const string failures = """[{"path":"/p/tool.exe","error":"Connapse can't read .exe files."}]""";

        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload(Finished(1, 0, 1)));
        await cut.InvokeAsync(() => Run(cut).AddFolderUploadFailures(failures));
        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload(Finished(1, 0, 1)));
        await cut.InvokeAsync(() => Run(cut).AddFolderUploadFailures(failures));

        cut.FindAll(".folder-upload-failures li").Should().ContainSingle().Which.TextContent.Should().Contain("/p/tool.exe");
    }

    [Fact]
    public async Task ShowFolderUploadBusy_ThenTheNextReport_ClearsTheNotice()
    {
        var cut = RenderPage();

        await cut.InvokeAsync(() => Run(cut).ShowFolderUploadBusy());
        cut.Find("#folder-upload-busy");

        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload(Finished(1, 1, 0)));
        cut.FindAll("#folder-upload-busy").Should().BeEmpty();
    }

    [Fact]
    public async Task Panel_Dismiss_ForgetsTheRunHereAndInTheBrowser()
    {
        var cut = RenderPage();
        cut.WaitForState(() => module.Invocations["attachFolderUploadPanel"].Count > 0);
        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload(Finished(1, 1, 0)));

        cut.Find("#folder-upload-dismiss").Click();

        cut.FindAll("#folder-upload-panel").Should().BeEmpty();
        module.VerifyInvoke("dismissFolderUpload");
    }

    [Theory]
    [InlineData("reading", "#folder-upload-cancel", "cancelFolderUpload")]
    [InlineData("confirm", "#folder-upload-start", "confirmFolderUpload")]
    public async Task Panel_Button_ReachesTheRunInTheBrowser(string stage, string button, string function)
    {
        var cut = RenderPage();
        cut.WaitForState(() => module.Invocations["attachFolderUploadPanel"].Count > 0);
        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload($$"""{"stage":"{{stage}}","total":6000}"""));

        cut.Find(button).Click();

        module.VerifyInvoke(function);
    }

    [Fact]
    public async Task Panel_Download_ReachesTheFullListInTheBrowser()
    {
        var cut = RenderPage();
        cut.WaitForState(() => module.Invocations["attachFolderUploadPanel"].Count > 0);
        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload(Finished(1, 0, 1)));

        cut.Find("#folder-upload-download").Click();

        module.VerifyInvoke("downloadFolderUploadFailures");
    }

    [Fact]
    public async Task StatusChanged_ForAFileWithNoRowDuringAFolderUpload_ReReadsTheCounts()
    {
        var cut = RenderPage();
        await cut.InvokeAsync(() => Run(cut).UpdateFolderUpload("""{"stage":"uploading","total":2}"""));
        documents.ClearReceivedCalls();

        Notify(new DocumentStatusChangedEvent("in-a-subfolder", DocumentStatus.Ready));

        cut.WaitForAssertion(() => documents.ReceivedCalls().Should().NotBeEmpty());
    }

    [Fact]
    public async Task StatusChanged_ForAFileWithNoRowAndNoFolderUpload_LeavesTheCounts()
    {
        var cut = RenderPage();
        await cut.InvokeAsync(() => { });
        documents.ClearReceivedCalls();

        Notify(new DocumentStatusChangedEvent("elsewhere", DocumentStatus.Ready));
        await cut.InvokeAsync(() => { });

        documents.ReceivedCalls().Should().BeEmpty();
    }
}
