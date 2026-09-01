using Connapse.Core;
using Connapse.Web.Components.Providers;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;

#pragma warning disable BL0006 // Test-only inspection of the component's generated render frames.

namespace Connapse.Integration.Tests;

/// <summary>
/// The AWS steps render as <see cref="ProviderStepCard"/> instances now, so what an administrator
/// sees is decided by the card's own visibility rules rather than by markup the page duplicated per
/// step. These tests read the parameters the page hands each card and render the card from them,
/// without a Blazor circuit, to prove the two rules that matter: a completed step shows its summary
/// and hides its setup form, and an incomplete one shows the manual form while keeping the optional
/// guided walkthrough tucked inside its disclosure.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ProvidersPageRenderingTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task CompletedAccess_ShowsSummaryAndHidesTheManualFormUntilOpened()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var page = new StaticProviders();
        page.Inject(scope.ServiceProvider);
        page.SetKey("aws");
        page.SetSetups(CompletedAwsAccessReader.Setups);

        var parameters = page.RenderStepParameters("access");

        parameters["Status"].Should().Be(RequirementStatus.Satisfied);
        parameters["Expanded"].Should().Be(false);

        var card = RenderCard(parameters);

        // The compact summary is always visible; the manual setup form is not, because the step is
        // done and the card is collapsed.
        card.Text.Should().Contain("Connapse can read S3.");
        card.Text.Should().NotContain("Manual values");
    }

    [Fact]
    public async Task IncompleteAccess_ShowsTheManualFormAndKeepsGuideInstructionsInsideTheGuide()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var page = new StaticProviders();
        page.Inject(scope.ServiceProvider);
        page.SetKey("aws");
        page.SetSetups(IncompleteAwsAccessReader.Setups);

        var parameters = page.RenderStepParameters("access");

        parameters["Status"].Should().Be(RequirementStatus.NotConfigured);

        var card = RenderCard(parameters);

        card.Text.Should().Contain("Connapse has no AWS identity yet.");
        card.Text.Should().Contain("Manual values");

        // The easy path is wrapped in a ProviderSetupGuide the card renders itself. Its title is
        // visible, but the instructions inside it stay in the guide's ChildContent fragment rather
        // than leaking into the card body as direct text.
        card.GuideTitles.Should().Contain("Easy setup with AWS CloudShell");
        card.Text.Should().NotContain("Paste what it printed");
    }

    /// <summary>
    /// Renders a <see cref="ProviderStepCard"/> from the parameters the page supplied it, and reports
    /// the card's direct text plus the titles of any <see cref="ProviderSetupGuide"/> it contains.
    /// </summary>
    private static RenderedCard RenderCard(IReadOnlyDictionary<string, object?> parameters)
    {
        var card = new TestableProviderStepCard();
        card.ApplyParameters(parameters);

        var builder = new RenderTreeBuilder();
        card.Render(builder);
        var frames = builder.GetFrames();

        var text = new StringBuilder();
        var guideTitles = new List<string>();

        for (int i = 0; i < frames.Count; i++)
        {
            ref readonly var frame = ref frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Text)
            {
                text.Append(frame.TextContent);
            }
            else if (frame.FrameType == RenderTreeFrameType.Markup)
            {
                text.Append(frame.MarkupContent);
            }
            else if (frame.FrameType == RenderTreeFrameType.Component
                     && frame.ComponentType == typeof(ProviderSetupGuide))
            {
                int end = i + frame.ComponentSubtreeLength;
                for (int j = i + 1; j < end; j++)
                {
                    ref readonly var child = ref frames.Array[j];
                    if (child.FrameType == RenderTreeFrameType.Attribute
                        && child.AttributeName == "Title"
                        && child.AttributeValue is string title)
                        guideTitles.Add(title);
                }
            }
        }

        return new RenderedCard(text.ToString(), guideTitles);
    }

    private sealed record RenderedCard(string Text, IReadOnlyList<string> GuideTitles);

    /// <summary>A <see cref="ProviderStepCard"/> whose parameters can be set and whose render tree
    /// can be built directly, so a step can be exercised without a renderer.</summary>
    private sealed class TestableProviderStepCard : ProviderStepCard
    {
        public void ApplyParameters(IReadOnlyDictionary<string, object?> parameters)
        {
            var view = ParameterView.FromDictionary(new Dictionary<string, object?>(parameters));
            view.SetParameterProperties(this);
        }

        public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder);
    }

    private sealed class StaticProviders : Connapse.Web.Components.Pages.Providers
    {
        public void Inject(IServiceProvider services)
        {
            foreach (var property in typeof(Connapse.Web.Components.Pages.Providers)
                         .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null))
                property.SetValue(this, services.GetRequiredService(property.PropertyType));
        }

        public void SetKey(string value) =>
            typeof(Connapse.Web.Components.Pages.Providers)
                .GetProperty("Key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(this, value);

        public void SetSetups(IReadOnlyList<ProviderSetup> value) =>
            typeof(Connapse.Web.Components.Pages.Providers)
                .GetField("setups", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(this, value);

        /// <summary>
        /// Reads back the parameters the page passed the <see cref="ProviderStepCard"/> with the given
        /// id, by walking the page's own render frames.
        /// </summary>
        public IReadOnlyDictionary<string, object?> RenderStepParameters(string id)
        {
            var builder = new RenderTreeBuilder();
            BuildRenderTree(builder);
            var frames = builder.GetFrames();

            for (int i = 0; i < frames.Count; i++)
            {
                ref readonly var frame = ref frames.Array[i];
                if (frame.FrameType != RenderTreeFrameType.Component
                    || frame.ComponentType != typeof(ProviderStepCard))
                    continue;

                var parameters = new Dictionary<string, object?>();
                int end = i + frame.ComponentSubtreeLength;
                for (int j = i + 1; j < end; j++)
                {
                    ref readonly var child = ref frames.Array[j];
                    if (child.FrameType == RenderTreeFrameType.Attribute)
                        parameters[child.AttributeName] = child.AttributeValue;
                }

                if (parameters.TryGetValue("Id", out object? value) && Equals(value, id))
                    return parameters;
            }

            throw new InvalidOperationException($"Provider step {id} was not rendered.");
        }
    }

    private static class CompletedAwsAccessReader
    {
        public static IReadOnlyList<ProviderSetup> Setups { get; } =
            [
                new ProviderSetup(
                    "aws",
                    "AWS",
                    [
                        new ProviderRequirement(
                            "Access",
                            "What Connapse reads as when it syncs an S3 source.",
                            RequirementStatus.Satisfied,
                            "arn:aws:iam::123456789012:user/connapse-reader"),
                        new ProviderRequirement(
                            "IAM Identity Center",
                            "The directory used for per-user permissions.",
                            RequirementStatus.NotConfigured),
                        new ProviderRequirement(
                            "Per-user permissions",
                            "The application people use to connect their AWS identity.",
                            RequirementStatus.NotConfigured),
                    ],
                    InUse: true),
            ];
    }

    private static class IncompleteAwsAccessReader
    {
        public static IReadOnlyList<ProviderSetup> Setups { get; } =
            [
                new ProviderSetup(
                    "aws",
                    "AWS",
                    [
                        new ProviderRequirement(
                            "Access",
                            "What Connapse reads as when it syncs an S3 source.",
                            RequirementStatus.NotConfigured),
                        new ProviderRequirement(
                            "IAM Identity Center",
                            "The directory used for per-user permissions.",
                            RequirementStatus.NotConfigured),
                        new ProviderRequirement(
                            "Per-user permissions",
                            "The application people use to connect their AWS identity.",
                            RequirementStatus.NotConfigured),
                    ],
                    InUse: false),
            ];
    }
}

#pragma warning restore BL0006
