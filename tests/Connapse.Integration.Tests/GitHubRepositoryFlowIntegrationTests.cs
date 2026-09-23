using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Storage.Connectors;
using Connapse.Web.Components.Settings;
using Connapse.Web.Services;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// What the New source dialog does for a public GitHub repository, minus the dialog: the form's
/// requests go through the real source store, and one scheduled cycle syncs both sources. GitHub is
/// a local git repository for docs and <see cref="FakeGitHubApi"/> for issues.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class GitHubRepositoryFlowIntegrationTests(SharedWebAppFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-flow-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHubApi _api = new();

    public void Dispose()
    {
        _api.Dispose();
        if (!Directory.Exists(_root)) return;
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The real factory's parsing, with the two network endpoints swapped for local stand-ins —
    /// so a scope key the form writes and the factory does not read would fail here.
    /// </summary>
    private sealed class LocalGitHubFactory(IConnectorFactory real, string upstream, FakeGitHubApi api) : IConnectorFactory
    {
        public IConnector Create(Source source, Connection connection, string? secret = null) =>
            throw new InvalidOperationException("a public GitHub source has no connection");

        public IConnector Create(Source source)
        {
            var config = ((GitHubConnector)real.Create(source)).Config;
            return config.Kind == GitHubContentKind.Docs
                ? new GitHubConnector(config with { RemoteUrl = upstream })
                : new GitHubConnector(config with { ApiBaseUrl = FakeGitHubApi.BaseUrl }, api.CreateClient());
        }
    }

    [Fact]
    public async Task AddRepository_CreatesBothSources_AndOneCycleSyncsThem()
    {
        string upstream = Path.Combine(_root, "upstream");
        Repository.Init(upstream);
        using (var repo = new Repository(upstream))
        {
            System.IO.File.WriteAllText(Path.Combine(upstream, "README.md"), "# Readme");
            Commands.Stage(repo, "README.md");
            var who = new Signature("t", "t@example.com", DateTimeOffset.UtcNow);
            repo.Commit("init", who, who);
        }
        _api.UpsertIssue(1, "Crash");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<ISourceStore>();

        // A unique owner, because the shared database outlives this test and names are unique.
        string owner = "o" + Guid.NewGuid().ToString("N")[..12];
        var form = new GitHubRepositoryForm { Repository = $"https://github.com/{owner}/hello" };
        form.Validate().Should().BeNull();

        var created = new List<Source>();
        foreach (var request in form.ToRequests())
            created.Add(await store.CreateAsync(request));

        var mirrors = Options(sp, Path.Combine(_root, "mirrors"));
        var queue = new RecordingIngestionQueue();
        var service = new SourceSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new LocalGitHubFactory(mirrors, upstream, _api),
            queue,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SourceSyncService>());

        foreach (var source in created)
            (await service.SyncSourceAsync(source, connection: null, CancellationToken.None)).Error.Should().BeNull();

        queue.Jobs.Select(j => j.Path).Should().Contain(["/README.md", "/issues/1.md"]);

        var issues = (await store.GetAsync(created[1].Id))!;
        issues.SyncIntervalSeconds.Should().Be(GitHubRepositoryForm.IssuesSyncIntervalSeconds);
        SourceSyncService.IsDue(issues, DateTime.UtcNow).Should().BeFalse(
            "the issues source just synced and asked for a 15-minute interval");
    }

    /// <summary>The real factory, with mirrors under this test's directory rather than the app's.</summary>
    private static IConnectorFactory Options(IServiceProvider sp, string mirrorDirectory)
    {
        var settings = Microsoft.Extensions.Options.Options.Create(new GitHubSourceSettings { MirrorDirectory = mirrorDirectory });
        return ActivatorUtilities.CreateInstance<ConnectorFactory>(sp, new StaticMonitor<GitHubSourceSettings>(settings.Value));
    }

    private sealed class StaticMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
