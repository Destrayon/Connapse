using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Ingestion.Parsers;
using Microsoft.Extensions.Logging;
using static Connapse.Ingestion.Isolation.ParserProtocol;

namespace Connapse.Ingestion.Isolation;

/// <summary>
/// The shared inference host (#680): one process that holds the layout, table and OCR models for
/// every parser host, so each host need not load its own copy and pay its own activation peak. It
/// is started on first use, confined and memory-capped like a parser host, serves one image at a
/// time, and is replaced when it dies.
/// </summary>
public sealed partial class ParserProcessPool
{
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private Host? _inference;

    /// <summary>
    /// A parser host's call made before its reply: given the frame, null when it is the reply
    /// itself; else the answer to write back, after reading what follows the call with <c>next</c>.
    /// </summary>
    internal delegate Task<byte[]?> Relay(byte[] frame, Func<int, Task<byte[]>> next, CancellationToken ct);

    /// <summary>Whether parser hosts run the PDF models in the shared inference host.</summary>
    internal bool UseSharedInference { get; set; }

    /// <summary>Answers a parser host's model run by running it in the inference host.</summary>
    private async Task<byte[]?> RelayInferenceAsync(byte[] frame, Func<int, Task<byte[]>> next, UploadSettings settings, CancellationToken ct)
    {
        if (Deserialize<ParseResponse>(frame).Infer is not { } call)
            return null;
        byte[] pixels = await next(InferenceProtocol.MaxPixelFrame);
        return Serialize(await InferAsync(call, pixels, settings, ct));
    }

    /// <summary>The running inference host's process, for tests.</summary>
    internal int? InferenceProcessId => _inference is { HasExited: false } host ? host.ProcessId : null;

    /// <summary>The memory the inference host may use: room for the layout model's peak, the largest of the three.</summary>
    internal int InferenceMemoryLimitMb => Math.Max(LayoutHostMb, _hostMemoryCeilingMb);

    /// <summary>
    /// Runs one model on one image in the inference host. Failures come back as a response with
    /// <see cref="InferenceProtocol.InferResponse.Error"/> or <see cref="InferenceProtocol.InferResponse.OutOfMemory"/>
    /// set, never as an exception, so a caller can read the page another way; cancellation is thrown.
    /// </summary>
    internal async Task<InferenceProtocol.InferResponse> InferAsync(
        InferRequest request, ReadOnlyMemory<byte> pixels, UploadSettings settings, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _inferenceGate.WaitAsync(ct);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                Host host = _inference is { HasExited: false } running ? running : (_inference = StartInference(settings));
                using var kill = ct.Register(() => host.Kill());
                try
                {
                    byte[] reply = await host.ExchangeAsync(Serialize(request), pixels, InferenceProtocol.MaxJsonFrame, ct);
                    var response = Deserialize<InferenceProtocol.InferResponse>(reply);
                    if (response.OutOfMemory)
                        DiscardInference(host);
                    return response;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    string stderr = host.CollectErrors();
                    string exit = host.ExitDescription();
                    DiscardInference(host);
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);
                    if (host.KilledForMemory)
                        return new InferenceProtocol.InferResponse(OutOfMemory: true);

                    // As for parser hosts (#657): one that died before it was ready never saw the image.
                    if (ex is HostNotReadyException && attempt == 1)
                    {
                        _logger.LogWarning(ex, "InferenceHostFailedToStart, {Exit}; retrying on a new host: {Stderr}", exit, stderr);
                        continue;
                    }

                    _logger.LogError(ex, "InferenceHostCrashed running {Model}, {Exit}: {Stderr}",
                        LogSanitizer.Sanitize(request.Model), exit, stderr);
                    return new InferenceProtocol.InferResponse(Error: $"the inference process crashed ({exit})");
                }
            }
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private Host StartInference(UploadSettings settings)
    {
        int limitMb = InferenceMemoryLimitMb;
        ParserSandboxMode sandbox = SandboxModeOf(settings);
        var start = StartInfo(HostPath, limitMb, sandboxMode: sandbox);
        start.ArgumentList.Add(InferenceHostLoop.Argument);
        var host = Host.Start(StartInfoForTests?.Invoke(start) ?? start, limitMb, sandbox);
        _all[host] = 0;
        _logger.LogInformation("InferenceHostStarted with at most {LimitMb} MB", limitMb);
        return host;
    }

    private void DiscardInference(Host host)
    {
        host.Kill();
        _all.TryRemove(host, out _);
        if (ReferenceEquals(_inference, host))
            _inference = null;
    }
}
