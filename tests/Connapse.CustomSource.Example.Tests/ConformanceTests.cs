using System.Text.RegularExpressions;
using Connapse.CustomSource.Conformance;
using FluentAssertions;

namespace Connapse.CustomSource.Example.Tests;

/// <summary>
/// The conformance suite run against the example server, which implements every capability. Every check
/// must pass and none may be skipped, so a change that breaks either the example or the suite is caught here.
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class ConformanceTests
{
    [Fact]
    public async Task ExampleServer_PassesEveryCheck()
    {
        using ExampleServer server = new(files: new Dictionary<string, string>
        {
            ["eng/README.md"] = "# Engineering",
            ["eng/design/architecture.md"] = "# Architecture",
            ["eng/design/secret.md"] = "# Secret",
            ["eng/onboarding.md"] = "# Onboarding",
        });
        HttpClient http = server.Client(authorized: false);

        IReadOnlyList<CheckResult> results = await new ConformanceSuite(http, new ConformanceOptions
        {
            BaseUrl = http.BaseAddress!,
            Token = ExampleServer.Token,
            Scope = "eng",
            UserId = ExampleServer.AliceId,
            LinkSecret = ExampleServer.LinkSecret,
            ProviderId = ExampleServer.ProviderId,
            Issuer = ExampleServer.Issuer,
        }, new ExampleHooks(server)).RunAsync();

        results.Should().NotBeEmpty();
        results.Where(r => r.Outcome != CheckOutcome.Pass)
            .Select(r => $"{r.Outcome} {r.Id}: {r.Detail}")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ContentOnlyMiddleMan_SkipsThePermissionAndLinkingChecks()
    {
        ExampleConfig config = ExampleServer.DefaultConfig() with { PermissionsEnabled = false, LinkSecret = null };
        using ExampleServer server = new(config, new Dictionary<string, string> { ["eng/README.md"] = "# Engineering" });
        HttpClient http = server.Client(authorized: false);

        IReadOnlyList<CheckResult> results = await new ConformanceSuite(http, new ConformanceOptions
        {
            BaseUrl = http.BaseAddress!,
            Token = ExampleServer.Token,
        }).RunAsync();

        results.Should().NotContain(r => r.Outcome == CheckOutcome.Fail);
        results.Where(r => r.Id.StartsWith("check.") || r.Id.StartsWith("keys.") || r.Id.StartsWith("link"))
            .Should().OnlyContain(r => r.Outcome == CheckOutcome.Skip);
    }

    [Fact]
    public async Task MiddleManAcceptingAnyToken_FailsTheAuthChecks()
    {
        using ExampleServer server = new(files: new Dictionary<string, string> { ["eng/README.md"] = "# Engineering" });
        HttpClient http = server.Client(authorized: false);

        IReadOnlyList<CheckResult> results = await new ConformanceSuite(http, new ConformanceOptions
        {
            BaseUrl = http.BaseAddress!,
            Token = "not-the-token",
        }).RunAsync();

        results.Single(r => r.Id == "discovery").Outcome.Should().Be(CheckOutcome.Fail);
    }

    /// <summary>The example server's system is a folder, so its hooks write and delete files, and sign in as alice.</summary>
    private sealed class ExampleHooks(ExampleServer server) : IConformanceHooks
    {
        public Task<bool> CreateDocumentAsync(string scope, CancellationToken ct)
        {
            server.WriteFile($"{scope}/conformance-{Guid.NewGuid():N}.md", "# Created by the conformance suite");
            return Task.FromResult(true);
        }

        public Task<bool> DeleteDocumentAsync(string scope, string documentId, CancellationToken ct)
        {
            DocumentId.TryDecode(documentId, out string path).Should().BeTrue();
            server.DeleteFile($"{scope}/{path}");
            return Task.FromResult(true);
        }

        public async Task<string?> CompleteSignInAsync(Uri linkUrl, string linkToken, Uri returnUrl, CancellationToken ct)
        {
            HttpClient browser = server.Client(authorized: false);
            HttpResponseMessage page = await browser.PostAsync(linkUrl,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = linkToken }), ct);
            string state = StateField().Match(await page.Content.ReadAsStringAsync(ct)).Groups[1].Value;
            HttpResponseMessage done = await browser.PostAsync("/link/complete",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = state, ["user"] = "alice" }), ct);
            return CodeParameter().Match(done.Headers.Location!.ToString()).Groups[1].Value;
        }
    }

    [GeneratedRegex("name=\"state\" value=\"([^\"]+)\"")]
    private static partial Regex StateField();

    [GeneratedRegex(@"[?&]code=([^&]+)")]
    private static partial Regex CodeParameter();
}
