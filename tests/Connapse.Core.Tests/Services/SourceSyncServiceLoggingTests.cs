using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Connapse.Core.Tests.Services;

/// <summary>
/// SyncAllAsync's connection lookup has two failure shapes that must not read alike: a
/// connection-less (Provider-based) source such as public GitHub (epic #508) is an expected
/// interim state pending Phase 2 wiring, while a source whose ConnectionId points at a
/// deleted Connection is a genuinely dangling reference an operator should notice.
/// </summary>
[Trait("Category", "Unit")]
public class SourceSyncServiceLoggingTests
{
    /// <summary>
    /// Captures log entries so the two branches can be told apart. Written by hand rather than
    /// substituted because ILogger.Log is generic, which makes the mock-based assertion far
    /// harder to read than the thing it is checking (mirrors SourceConnectorFactoryTests).
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Source MakeSource(Guid? connectionId, ConnectionProvider? provider = null) => new(
        Id: Guid.NewGuid(),
        Name: "src",
        Description: null,
        ConnectionId: connectionId,
        ScopeJson: "{}",
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        Provider: provider);

    private static (SourceSyncService Service, RecordingLogger<SourceSyncService> Logger) Build(
        ISourceStore sourceStore, IConnectionStore connectionStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sourceStore);
        services.AddSingleton(connectionStore);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var logger = new RecordingLogger<SourceSyncService>();
        var service = new SourceSyncService(
            scopeFactory,
            Substitute.For<IConnectorFactory>(),
            Substitute.For<IIngestionQueue>(),
            logger);

        return (service, logger);
    }

    [Fact]
    public async Task SyncAllAsync_ConnectionLessSource_LogsDebugWithoutMissingConnectionWarning()
    {
        var source = MakeSource(connectionId: null, provider: ConnectionProvider.GitHub);

        var sourceStore = Substitute.For<ISourceStore>();
        sourceStore.ListAsync(skip: 0, take: int.MaxValue, ct: Arg.Any<CancellationToken>())
            .Returns([source]);
        var connectionStore = Substitute.For<IConnectionStore>();

        var (service, logger) = Build(sourceStore, connectionStore);

        await service.SyncAllAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Debug,
            "a connection-less source is an expected interim state, not an alarm");
        logger.Entries[0].Message.Should().NotContain("references missing connection");
        logger.Entries[0].Message.Should().Contain("#508");

        // Never looked up: there is no ConnectionId to look up.
        await connectionStore.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [Fact]
    public async Task SyncAllAsync_DanglingConnectionReference_StillLogsMissingConnectionWarning()
    {
        var connectionId = Guid.NewGuid();
        var source = MakeSource(connectionId);

        var sourceStore = Substitute.For<ISourceStore>();
        sourceStore.ListAsync(skip: 0, take: int.MaxValue, ct: Arg.Any<CancellationToken>())
            .Returns([source]);
        var connectionStore = Substitute.For<IConnectionStore>();
        connectionStore.GetAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns((Connection?)null);

        var (service, logger) = Build(sourceStore, connectionStore);

        await service.SyncAllAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Warning,
            "a ConnectionId that resolves to nothing is a real dangling reference");
        logger.Entries[0].Message.Should().Contain("references missing connection");
    }
}
