using System.Reflection;
using Connapse.Core;
using Connapse.Web.Components.Providers;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Xunit;

// Tests set component parameters directly and inspect the generated render tree.
#pragma warning disable BL0005 // Component parameter set outside of its component
#pragma warning disable BL0006 // Do not use RenderTree types manually

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The shared step-card shell that every provider setup step (AWS access, directory group,
/// Identity Center, ...) renders through. Verifies the status-to-presentation mapping, the
/// collapse-when-satisfied behavior, and the ARIA wiring that makes the disclosure accessible.
/// </summary>
[Trait("Category", "Unit")]
public class ProviderStepComponentTests
{
    private sealed class TestProviderStepCard : ProviderStepCard
    {
        public IReadOnlyList<RenderTreeFrame> RenderFrames()
        {
            var builder = new RenderTreeBuilder();
            BuildRenderTree(builder);
            var frames = builder.GetFrames();
            return frames.Array.Take(frames.Count).ToArray();
        }
    }

    private sealed class TestProviderSetupGuide : ProviderSetupGuide
    {
        public IReadOnlyList<RenderTreeFrame> RenderFrames()
        {
            var builder = new RenderTreeBuilder();
            BuildRenderTree(builder);
            var frames = builder.GetFrames();
            return frames.Array.Take(frames.Count).ToArray();
        }
    }

    private sealed class TestProviderResetAction : ProviderResetAction
    {
        public IReadOnlyList<RenderTreeFrame> RenderFrames()
        {
            var builder = new RenderTreeBuilder();
            BuildRenderTree(builder);
            var frames = builder.GetFrames();
            return frames.Array.Take(frames.Count).ToArray();
        }
    }

    private static MethodInfo Method(object target, string name)
    {
        for (Type? type = target.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } method)
                return method;
        }

        throw new InvalidOperationException($"Method {name} was not found.");
    }

    private static void Invoke(object target, string name) => Method(target, name).Invoke(target, null);

    private static async Task InvokeAsync(object target, string name) =>
        await (Task)Method(target, name).Invoke(target, null)!;

    private static string Text(IEnumerable<RenderTreeFrame> frames) => string.Concat(
        frames.Where(frame => frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
            .Select(frame => frame.FrameType == RenderTreeFrameType.Text
                ? frame.TextContent
                : frame.MarkupContent));

    private static IReadOnlyList<string> AttributeValues(
        IEnumerable<RenderTreeFrame> frames, string name) => frames
            .Where(frame => frame.FrameType == RenderTreeFrameType.Attribute
                && frame.AttributeName == name)
            .Select(frame => frame.AttributeValue?.ToString() ?? string.Empty)
            .ToList();

    private static string Attribute(IEnumerable<RenderTreeFrame> frames, string name) =>
        AttributeValues(frames, name).First();

    private static IReadOnlyList<string> ElementNames(IEnumerable<RenderTreeFrame> frames) => frames
        .Where(frame => frame.FrameType == RenderTreeFrameType.Element)
        .Select(frame => frame.ElementName)
        .ToList();

    private static TestProviderStepCard Card(RequirementStatus status, bool expanded = false) =>
        new()
        {
            Id = "access",
            Title = "Access",
            Description = "What Connapse reads as.",
            Status = status,
            StatusLabel = status == RequirementStatus.Satisfied ? "Ready" : "Not set up",
            Expanded = expanded,
            ManualContent = builder => builder.AddContent(0, "manual-marker"),
            Summary = builder => builder.AddContent(0, "summary-marker"),
        };

    [Theory]
    [InlineData(RequirementStatus.Satisfied, "provider-step--satisfied", "bi-check-circle-fill")]
    [InlineData(RequirementStatus.Warning, "provider-step--warning", "bi-exclamation-triangle-fill")]
    [InlineData(RequirementStatus.Provisioning, "provider-step--provisioning", "bi-hourglass-split")]
    [InlineData(RequirementStatus.Failed, "provider-step--failed", "bi-x-circle-fill")]
    [InlineData(RequirementStatus.NotConfigured, "provider-step--not-configured", "bi-circle")]
    [InlineData(RequirementStatus.Unknown, "provider-step--unknown", "bi-question-circle")]
    public void Card_MapsEveryRequirementStatusToTextAndSemanticPresentation(
        RequirementStatus status, string cardClass, string iconClass)
    {
        var frames = Card(status).RenderFrames();
        Attribute(frames, "class").Should().Contain(cardClass);
        AttributeValues(frames, "class").Should().Contain(value => value.Contains(iconClass));
        Text(frames).Should().Contain(status == RequirementStatus.Satisfied ? "Ready" : "Not set up");
    }

    [Fact]
    public void SatisfiedCard_CollapsesSetupUntilExpanded()
    {
        Text(Card(RequirementStatus.Satisfied).RenderFrames()).Should().NotContain("manual-marker");
        Text(Card(RequirementStatus.Satisfied, expanded: true).RenderFrames()).Should().Contain("manual-marker");
    }

    [Theory]
    [InlineData(RequirementStatus.Unknown)]
    [InlineData(RequirementStatus.NotConfigured)]
    [InlineData(RequirementStatus.Warning)]
    [InlineData(RequirementStatus.Provisioning)]
    [InlineData(RequirementStatus.Failed)]
    public void NonSatisfiedCard_ShowsManualContentWithoutAnEditClick(RequirementStatus status) =>
        Text(Card(status).RenderFrames()).Should().Contain("manual-marker");

    [Fact]
    public void Card_LabelsTheSectionByItsHeadingAndBodyById()
    {
        var frames = Card(RequirementStatus.NotConfigured).RenderFrames();

        Attribute(frames, "aria-labelledby").Should().Be("access-heading");
        AttributeValues(frames, "id").Should().Contain("access-heading");
        AttributeValues(frames, "id").Should().Contain("access-setup");
    }

    [Fact]
    public void SatisfiedCard_EditButtonAriaControlsMatchesBodyIdAndAriaExpandedReflectsState()
    {
        var collapsedFrames = Card(RequirementStatus.Satisfied).RenderFrames();
        Attribute(collapsedFrames, "aria-controls").Should().Be("access-setup");
        Attribute(collapsedFrames, "aria-expanded").Should().Be("false");

        var expandedFrames = Card(RequirementStatus.Satisfied, expanded: true).RenderFrames();
        Attribute(expandedFrames, "aria-controls").Should().Be("access-setup");
        Attribute(expandedFrames, "aria-expanded").Should().Be("true");
    }

    [Fact]
    public void SetupGuide_StartsClosedAndUsesItsTitle()
    {
        var guide = new TestProviderSetupGuide
        {
            Title = "Easy setup with AWS CloudShell",
            ChildContent = builder => builder.AddContent(0, "guide-marker"),
        };

        var frames = guide.RenderFrames();
        ElementNames(frames).Should().ContainInOrder("details", "summary");
        AttributeValues(frames, "open").Should().BeEmpty();
        Text(frames).Should().Contain("Easy setup with AWS CloudShell").And.Contain("guide-marker");
    }

    [Fact]
    public async Task Reset_RequiresConfirmationAndCanBeCancelled()
    {
        int resets = 0;
        var reset = new TestProviderResetAction
        {
            Label = "Reset this step",
            ConfirmLabel = "Confirm reset",
            HelpText = "Clears saved values only.",
            OnReset = EventCallback.Factory.Create(this, () => { resets++; }),
        };

        Text(reset.RenderFrames()).Should().Contain("Reset this step")
            .And.NotContain("Confirm reset")
            .And.NotContain("Clears saved values only.");
        Invoke(reset, "RequestReset");
        Text(reset.RenderFrames()).Should().Contain("Confirm reset")
            .And.Contain("Cancel")
            .And.Contain("Clears saved values only.");
        Invoke(reset, "CancelReset");
        resets.Should().Be(0);

        Invoke(reset, "RequestReset");
        await InvokeAsync(reset, "ConfirmResetAsync");
        resets.Should().Be(1);
        Text(reset.RenderFrames()).Should().Contain("Reset this step").And.NotContain("Confirm reset");
    }
}
