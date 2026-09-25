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
/// SyncAllAsync's connection lookup has shapes that must not read alike: a connection-less
/// (Provider-based) source such as public GitHub (epic #508) is synced through its provider,
/// while a source whose ConnectionId points at a deleted Connection is a genuinely dangling
/// reference an operator should notice.
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
        ISourceStore sourceStore, IConnectionStore connectionStore, IConnectorFactory? connectorFactory = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sourceStore);
        services.AddSingleton(connectionStore);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var logger = new RecordingLogger<SourceSyncService>();
        var service = new SourceSyncService(
            scopeFactory,
            connectorFactory ?? Substitute.For<IConnectorFactory>(),
            Substitute.For<IIngestionQueue>(),
            logger);

        return (service, logger);
    }

    [Fact]
    public async Task SyncAllAsync_ConnectionLessSource_SyncsThroughItsProvider()
    {
        var source = MakeSource(connectionId: null, provider: ConnectionProvider.GitHub);

        var sourceStore = Substitute.For<ISourceStore>();
        sourceStore.ListAsync(skip: 0, take: int.MaxValue, ct: Arg.Any<CancellationToken>())
            .Returns([source]);
        var connectionStore = Substitute.For<IConnectionStore>();

        // Fails the cycle straight after the factory call, which is all this test is about;
        // the sync paths themselves are covered by the integration tests.
        var connectorFactory = Substitute.For<IConnectorFactory>();
        connectorFactory.Create(source).Returns(_ => throw new IOException("remote unavailable"));

        var (service, logger) = Build(sourceStore, connectionStore, connectorFactory);

        await service.SyncAllAsync(CancellationToken.None);

        connectorFactory.Received(1).Create(source);
        logger.Entries.Should().NotContain(e => e.Message.Contains("references missing connection"));

        // Never looked up: there is no ConnectionId to look up.
        await connectionStore.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    [Fact]
    public async Task SyncAllAsync_SourceWithNeitherConnectionNorProvider_IsSkippedWithAWarning()
    {
        var source = MakeSource(connectionId: null, provider: null);

        var sourceStore = Substitute.For<ISourceStore>();
        sourceStore.ListAsync(skip: 0, take: int.MaxValue, ct: Arg.Any<CancellationToken>())
            .Returns([source]);
        var connectorFactory = Substitute.For<IConnectorFactory>();

        var (service, logger) = Build(sourceStore, Substitute.For<IConnectionStore>(), connectorFactory);

        await service.SyncAllAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain("neither a connection nor a provider");
        connectorFactory.ReceivedCalls().Should().BeEmpty();
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
