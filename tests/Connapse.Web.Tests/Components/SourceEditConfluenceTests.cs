using System.Text.Json;
using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SourceEditPage = Connapse.Web.Components.Pages.SourceEdit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// A Confluence source's edit page: it names the site and space it reads, lets the attachment
/// settings change under the New source dialog's rules, and keeps the space itself read-only.
/// </summary>
[Trait("Category", "Unit")]
public class SourceEditConfluenceTests : IDisposable
{
    private readonly BunitContext ctx = new();
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IConnectionStore connections = Substitute.For<IConnectionStore>();
    private readonly List<UpdateSourceRequest> updates = [];

    private static readonly Connection Site = new(
        Guid.NewGuid(), "acme.atlassian.net", ConnectionProvider.Atlassian,
        """{"siteUrl":"https://acme.atlassian.net","cloudId":"11111111-2222-3333-4444-555555555555","clientId":"client-1"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);

    private readonly Source source = new(
        Guid.NewGuid(), "acme.atlassian.net / Engineering", null, Site.Id,
        """{"kind":"confluence-space","spaceId":"100","spaceKey":"ENG","spaceName":"Engineering","includeAttachments":true,"maxAttachmentMb":25}""",
        DateTime.UtcNow, DateTime.UtcNow);

    public SourceEditConfluenceTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
        sources.UpdateAsync(source.Id, Arg.Any<UpdateSourceRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<UpdateSourceRequest>();
                updates.Add(request);
                return Task.FromResult<Source?>(source with { ScopeJson = request.ScopeJson ?? source.ScopeJson });
            });
        connections.GetAsync(Site.Id, Arg.Any<CancellationToken>()).Returns(Site);

        ctx.Services.AddSingleton(sources);
        ctx.Services.AddSingleton(connections);
        ctx.Services.AddSingleton(Substitute.For<IConnectorFactory>());
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
        ctx.Services.AddLogging();
    }

    public void Dispose() => ctx.Dispose();

    [Fact]
    public void Save_UnexpectedFailure_ShowsAPlainMessageAndNotTheExceptionText()
    {
        sources.UpdateAsync(source.Id, Arg.Any<UpdateSourceRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<Source?>>(_ => throw new InvalidOperationException("Npgsql: connection to 10.0.0.5:5432 refused"));
        var cut = Render();

        cut.Find("#confluence-attachments").Change(false);
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Find(".alert-danger").TextContent.Should().Contain("Could not save the source, so nothing was changed."));
        cut.Markup.Should().NotContain("Npgsql").And.NotContain("10.0.0.5");
    }

    [Fact]
    public void Load_UnexpectedFailure_ShowsAPlainMessageAndNotTheExceptionText()
    {
        sources.GetAsync(source.Id, Arg.Any<CancellationToken>())
            .Returns<Task<Source?>>(_ => throw new InvalidOperationException("Npgsql: connection to 10.0.0.5:5432 refused"));

        var cut = ctx.Render<SourceEditPage>(p => p.Add(x => x.Id, source.Id));

        cut.WaitForAssertion(() => cut.Find(".alert-danger").TextContent.Should().Contain("Could not load the source."));
        cut.Markup.Should().NotContain("Npgsql").And.NotContain("10.0.0.5");
    }

    private IRenderedComponent<SourceEditPage> Render()
    {
        var cut = ctx.Render<SourceEditPage>(p => p.Add(x => x.Id, source.Id));
        cut.WaitForAssertion(() => cut.Find("#confluence-attachments"));
        return cut;
    }

    [Fact]
    public void Page_ShowsTheSiteSpaceAndAttachmentSettingsAndWhyTheSpaceIsFixed()
    {
        var cut = Render();

        cut.Markup.Should().Contain("acme.atlassian.net / Engineering (ENG) · attachments up to 25 MB");
        cut.Markup.Should().Contain("A source covers one space; add a new source for another space.");
        cut.Find("#confluence-attachments").HasAttribute("checked").Should().BeTrue();
        cut.Find("#confluence-max-attachment-mb").GetAttribute("value").Should().Be("25");
        cut.Find("fieldset#confluence-attachments-group legend").TextContent.Trim().Should().Be("Attachments");
        cut.Find("label[for=confluence-max-attachment-mb]").TextContent.Should().Be("Max attachment size (MB)");
    }

    [Fact]
    public void Save_AttachmentsTurnedOff_SendsTheScopeWithTheSpaceUnchanged()
    {
        var cut = Render();

        cut.Find("#confluence-attachments").Change(false);
        cut.Find("#confluence-max-attachment-mb").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => updates.Should().ContainSingle());
        using var doc = JsonDocument.Parse(updates[0].ScopeJson!);
        doc.RootElement.GetProperty("spaceId").GetString().Should().Be("100");
        doc.RootElement.GetProperty("includeAttachments").GetBoolean().Should().BeFalse();
        cut.Markup.Should().Contain("Saved.");
    }

    [Fact]
    public void MaxAttachmentSize_OutOfRange_IsFlaggedBesideTheFieldAndBlocksSave()
    {
        var cut = Render();

        cut.Find("#confluence-max-attachment-mb").Change("500");

        var size = cut.Find("#confluence-max-attachment-mb");
        size.GetAttribute("aria-invalid").Should().Be("true");
        size.GetAttribute("aria-describedby").Should().Contain("confluence-max-attachment-mb-error");
        cut.Find("#confluence-max-attachment-mb-error").TextContent.Should().Contain("between 1 and 100 MB");

        cut.Find("button.btn-primary").Click();
        updates.Should().BeEmpty();

        cut.Find("#confluence-attachments").Change(false);
        cut.FindAll("#confluence-max-attachment-mb-error").Should().BeEmpty();
    }

    [Fact]
    public void ConfluenceSection_OpensWithTheSetupGuideLink_AndKeepsAttachmentHelpToTwoSentences()
    {
        var cut = Render();

        cut.Find("#confluence-setup-guide a").GetAttribute("href").Should().Be(
            "https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#4-confluence-sources");
        cut.Markup.IndexOf("id=\"confluence-setup-guide\"", StringComparison.Ordinal)
            .Should().BeLessThan(cut.Markup.IndexOf("id=\"confluence-attachments-group\"", StringComparison.Ordinal));

        var help = cut.Find("#confluence-attachments-help");
        string text = System.Text.RegularExpressions.Regex.Replace(help.TextContent, @"\s+", " ").Trim();
        text.Should().StartWith("Index files attached to pages, such as PDFs and Word documents. Attachments need the read:attachment:confluence permission");
        text.Should().NotContain("Comments and blog posts");
        // Only the specific troubleshooting link stays inline.
        help.QuerySelectorAll("a").Select(a => a.GetAttribute("href"))
            .Should().Equal("https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#missing-scope");
    }

    [Fact]
    public void Name_Blank_IsFlaggedBesideTheFieldOnBlur_AndBlocksSave()
    {
        var cut = Render();

        cut.Find("#source-name").Change("  ");
        cut.Find("#source-name").Blur();

        var name = cut.Find("#source-name");
        name.GetAttribute("aria-invalid").Should().Be("true");
        name.GetAttribute("aria-describedby").Should().Be("source-name-error");
        cut.Find("#source-name-error").TextContent.Should().Contain("A source name is required.");

        cut.Find("button.btn-primary").Click();
        updates.Should().BeEmpty();
        cut.FindAll(".alert-danger").Should().BeEmpty("the error is shown beside the field, not repeated at the bottom");

        cut.Find("#source-name").Change("Renamed");
        cut.FindAll("#source-name-error").Should().BeEmpty();
        cut.Find("#source-name").HasAttribute("aria-invalid").Should().BeFalse();
    }

    [Fact]
    public void SyncInterval_TooShort_IsFlaggedBesideTheFieldOnSubmit()
    {
        var cut = Render();

        cut.Find("#source-interval").Change("30");
        cut.Find("button.btn-primary").Click();

        var interval = cut.Find("#source-interval");
        interval.GetAttribute("aria-invalid").Should().Be("true");
        interval.GetAttribute("aria-describedby").Should().Be("source-interval-error source-interval-help");
        cut.Find("#source-interval-error").TextContent.Should().Contain("at least 60 seconds");
        updates.Should().BeEmpty();

        cut.Find("#source-interval").Change("120");
        cut.FindAll("#source-interval-error").Should().BeEmpty();
        cut.Find("#source-interval").GetAttribute("aria-describedby").Should().Be("source-interval-help");
    }

    [Fact]
    public void Save_NothingChanged_SendsNothing()
    {
        var cut = Render();

        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nothing changed."));
        updates.Should().BeEmpty();
    }
}
