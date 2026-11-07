using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Vectors;

/// <summary>
/// Makes sure the Ollama that ships with Compose holds the embedding model, so nobody has to pull it by hand. It runs in
/// the app, not in a container that blocks <c>docker compose up</c>, because it can then say how far it has got, and
/// because the model can change on the Settings page.
/// </summary>
public sealed class LocalModelDownloadService(
    OllamaModelClient ollama,
    IOptionsMonitor<EmbeddingSettings> embedding,
    IOptions<LocalModelOptions> options,
    ILogger<LocalModelDownloadService> logger) : BackgroundService, ILocalModelMonitor
{
    /// <summary>Failures in a row shrugged off, as Ollama is still starting when the app first asks.</summary>
    private const int FailuresBeforeReporting = 3;

    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private LocalModelStatus _current = Initial(options.Value, embedding.CurrentValue);
    private CancellationTokenSource _targetChanged = new();
    private Target _target = TargetOf(options.Value, embedding.CurrentValue);

    /// <summary>How often the model is checked once it's ready, in case Ollama lost it.</summary>
    internal TimeSpan RecheckEvery { get; init; } = TimeSpan.FromMinutes(5);

    internal TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public LocalModelStatus Current => Volatile.Read(ref _current);

    public event Action? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using IDisposable? subscription = embedding.OnChange(OnSettingsChanged);

        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            CancellationToken targetChanged;
            Target target;
            lock (_gate)
            {
                targetChanged = _targetChanged.Token;
                target = _target;
            }

            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, targetChanged);
            TimeSpan wait;
            try
            {
                wait = await RunOnceAsync(target, attempt.Token);
                failures = 0;
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            {
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
            {
                failures++;
                logger.LogWarning(ex, "LocalModelDownloadFailed model={Model} attempt={Attempt}", LogSanitizer.Sanitize(target.Model), failures);
                if (failures >= FailuresBeforeReporting)
                    Publish(new LocalModelStatus(LocalModelState.Failed, target.Model));
                wait = TimeSpan.FromTicks(Math.Min(FirstRetryDelay.Ticks * (1L << Math.Min(failures - 1, 10)), LongestRetryDelay.Ticks));
            }

            try
            {
                await Task.Delay(wait, attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            {
                // The target changed or the app is stopping: the loop finds out which.
            }
        }
    }

    /// <summary>Brings one target up to date, and says how long until it next needs looking at.</summary>
    private async Task<TimeSpan> RunOnceAsync(Target target, CancellationToken ct)
    {
        if (target.Address is null)
        {
            Publish(target.Model is null ? LocalModelStatus.NotManaged : new LocalModelStatus(LocalModelState.NoProvider));
            return Timeout.InfiniteTimeSpan;
        }

        if (Current.State is not (LocalModelState.Ready or LocalModelState.Downloading or LocalModelState.Failed) || Current.Model != target.Model)
            Publish(new LocalModelStatus(LocalModelState.Checking, target.Model));

        if (!await ollama.HasModelAsync(target.Address, target.Model!, ct))
        {
            logger.LogInformation("LocalModelDownloadStarted model={Model}", LogSanitizer.Sanitize(target.Model));
            Publish(new LocalModelStatus(LocalModelState.Downloading, target.Model));
            await ollama.PullAsync(target.Address, target.Model!,
                percent => Publish(new LocalModelStatus(LocalModelState.Downloading, target.Model, percent)), ct);
            logger.LogInformation("LocalModelDownloadFinished model={Model}", LogSanitizer.Sanitize(target.Model));
        }

        Publish(new LocalModelStatus(LocalModelState.Ready, target.Model));
        return RecheckEvery;
    }

    private void OnSettingsChanged(EmbeddingSettings settings, string? _)
    {
        CancellationTokenSource? superseded = null;
        lock (_gate)
        {
            Target target = TargetOf(options.Value, settings);
            if (target == _target)
                return;

            _target = target;
            superseded = _targetChanged;
            _targetChanged = new CancellationTokenSource();
        }

        superseded.Cancel();
        superseded.Dispose();
    }

    private void Publish(LocalModelStatus status)
    {
        if (Interlocked.Exchange(ref _current, status) != status)
            Changed?.Invoke();
    }

    /// <summary>What to do, from Compose's word and the settings. No address and a model means nothing can embed.</summary>
    private readonly record struct Target(string? Address, string? Model);

    private static Target TargetOf(LocalModelOptions local, EmbeddingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(local.Address) || !UsesBundledOllama(local.Address, settings))
            return new Target(null, null);

        return local.IsOn ? new Target(local.Address, settings.Model) : new Target(null, settings.Model);
    }

    private static bool UsesBundledOllama(string address, EmbeddingSettings settings) =>
        string.Equals(settings.Provider, "Ollama", StringComparison.OrdinalIgnoreCase)
        && string.Equals(settings.BaseUrl?.TrimEnd('/'), address.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static LocalModelStatus Initial(LocalModelOptions local, EmbeddingSettings settings)
    {
        Target target = TargetOf(local, settings);
        return target switch
        {
            { Address: not null } => new LocalModelStatus(LocalModelState.Checking, target.Model),
            { Model: not null } => new LocalModelStatus(LocalModelState.NoProvider),
            _ => LocalModelStatus.NotManaged,
        };
    }

    public override void Dispose()
    {
        _targetChanged.Dispose();
        base.Dispose();
    }
}
