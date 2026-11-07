using Bunit;
using Connapse.Web.Components.Shared;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

[Trait("Category", "Unit")]
public class FolderUploadPanelTests : IDisposable
{
    private readonly BunitContext ctx = new();
    private readonly List<string> clicked = [];

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<FolderUploadPanel> Render(FolderUploadProgress progress, params FolderUploadFailure[] failures)
        => ctx.Render<FolderUploadPanel>(p => p
            .Add(c => c.Progress, progress)
            .Add(c => c.Failures, failures)
            .Add(c => c.OnCancel, () => clicked.Add("cancel"))
            .Add(c => c.OnConfirm, () => clicked.Add("confirm"))
            .Add(c => c.OnDismiss, () => clicked.Add("dismiss"))
            .Add(c => c.OnDownload, () => clicked.Add("download")));

    private static FolderUploadProgress Progress(string stage, int found = 0, int total = 0, int succeeded = 0, int failed = 0,
        bool waiting = false, bool cancelled = false, string? stoppedBecause = null)
        => new(stage, found, total, succeeded, failed, waiting, cancelled, stoppedBecause);

    [Fact]
    public void Render_Reading_CountsFilesFoundAndCanBeCancelled()
    {
        var cut = Render(Progress(FolderUploadProgress.Reading, found: 1234));

        cut.Markup.Should().Contain("1,234 files found");
        cut.Find("#folder-upload-cancel").Click();
        clicked.Should().Equal("cancel");
    }

    [Fact]
    public void Render_LargeFolder_WaitsForAnAnswerWithTheCount()
    {
        var cut = Render(Progress(FolderUploadProgress.Confirm, total: 12000));

        cut.Markup.Should().Contain("12,000 files");
        cut.Find("#folder-upload-start").Click();
        cut.Find("#folder-upload-cancel").Click();
        clicked.Should().Equal("confirm", "cancel");
    }

    [Fact]
    public void Render_Uploading_CountsSucceededAndFailedAsDone()
    {
        var cut = Render(Progress(FolderUploadProgress.Uploading, total: 200, succeeded: 90, failed: 10));

        cut.Markup.Should().Contain("100 of 200 files").And.Contain("10 failed");
        cut.Find(".folder-upload-fill").GetAttribute("style").Should().Contain("width: 50%");
        cut.Find("[role=progressbar]").GetAttribute("aria-valuenow").Should().Be("50");
    }

    [Fact]
    public void Render_UploadingWithNothingFailed_ShowsNoFailureCount()
    {
        var cut = Render(Progress(FolderUploadProgress.Uploading, total: 2, succeeded: 1));

        cut.FindAll(".folder-upload-failed").Should().BeEmpty();
    }

    [Fact]
    public void Render_UploadingWithoutAKnownTotal_ShowsAnEmptyBar()
    {
        var cut = Render(Progress(FolderUploadProgress.Uploading));

        cut.Find(".folder-upload-fill").GetAttribute("style").Should().Contain("width: 0%");
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Render_Uploading_SaysWhenTheServerMadeItWait(bool waiting, int notes)
    {
        var cut = Render(Progress(FolderUploadProgress.Uploading, total: 2, waiting: waiting));

        cut.FindAll(".folder-upload-note").Should().HaveCount(notes);
    }

    [Fact]
    public void Render_FinishedClean_SaysHowManyAndOffersNoDownload()
    {
        var cut = Render(Progress(FolderUploadProgress.Finished, total: 3, succeeded: 3));

        cut.Find("#folder-upload-result").TextContent.Should()
            .Be("3 of 3 files uploaded. They can be searched once they finish processing.");
        cut.FindAll("#folder-upload-download").Should().BeEmpty();
        cut.FindAll(".folder-upload-failures").Should().BeEmpty();
        cut.Find("#folder-upload-dismiss").Click();
        clicked.Should().Equal("dismiss");
    }

    [Fact]
    public void Render_FinishedWithFailures_NamesEachFileAndWhy()
    {
        var cut = Render(Progress(FolderUploadProgress.Finished, total: 3, succeeded: 2, failed: 1),
            new FolderUploadFailure("/proj/tool.exe", "Connapse can't read .exe files."));

        cut.Find("#folder-upload-result").TextContent.Should().Contain("2 of 3 files uploaded, 1 failed.");
        cut.Find(".folder-upload-failures code").TextContent.Should().Be("/proj/tool.exe");
        cut.Markup.Should().Contain("read .exe files.");
        cut.Find("#folder-upload-download").Click();
        clicked.Should().Equal("download");
    }

    [Fact]
    public void Render_MoreFailuresThanShown_PointsAtTheDownload()
    {
        var cut = Render(Progress(FolderUploadProgress.Finished, total: 500, failed: 500),
            new FolderUploadFailure("/a.exe", "No."));

        cut.Markup.Should().Contain("Showing the first 1 of 500");
    }

    [Fact]
    public void Render_AllFailuresShown_SaysNothingAboutMore()
    {
        var cut = Render(Progress(FolderUploadProgress.Finished, total: 1, failed: 1),
            new FolderUploadFailure("/a.exe", "No."));

        cut.FindAll(".folder-upload-note").Should().BeEmpty();
    }

    [Fact]
    public void Render_NothingUploaded_DoesNotPromiseSearch()
    {
        var cut = Render(Progress(FolderUploadProgress.Finished, total: 1, failed: 1));

        cut.Find("#folder-upload-result").TextContent.Should().Be("0 of 1 files uploaded, 1 failed.");
    }

    [Fact]
    public void Render_Cancelled_SaysSo()
    {
        var cut = Render(Progress(FolderUploadProgress.Finished, total: 4, succeeded: 1, cancelled: true));

        cut.Find("#folder-upload-result").TextContent.Should().StartWith("Cancelled: 1 of 4 files uploaded");
    }

    [Fact]
    public void Render_StoppedEarly_ShowsWhy()
    {
        var cut = Render(Progress(FolderUploadProgress.Finished, total: 4, stoppedBecause: "Your sign-in ended."));

        cut.Find("#folder-upload-stopped").TextContent.Should().Be("Your sign-in ended.");
    }
}
