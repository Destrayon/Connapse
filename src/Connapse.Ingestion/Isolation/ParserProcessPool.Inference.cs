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

    /// <summary>
    /// What the inference host needs, measured on Linux (#680): 817-1,226 MB at its peak with all
    /// three models loaded, the most on a scan read by OCR and then by layout.
    /// </summary>
    internal const int InferenceHostMb = 1400;

    /// <summary>The least a parser host gets when the models run elsewhere: measured at 115-191 MB.</summary>
    internal const int ThinHostMb = 256;

    /// <summary>Whether parser hosts run the PDF models in the shared inference host: when memory holds both (see Size).</summary>
    internal bool UseSharedInference { get; set; }

    /// <summary>The memory the PDF layout model gets: the inference host's when shared, else a parser host's.</summary>
    internal int LayoutMemoryMb(UploadSettings settings) => UseSharedInference ? InferenceMemoryLimitMb : MemoryLimitMb(settings);

    /// <summary>Answers a parser host's model run by running it in the inference host.</summary>
    private async Task<byte[]?> RelayInferenceAsync(byte[] frame, Func<int, Task<byte[]>> next, UploadSettings settings, CancellationToken ct)
    {
        if (Deserialize<ParseResponse>(frame).Infer is not { } call)
            return null;
        byte[] pixels = await next(InferenceProtocol.MaxPixelFrame);

        // Runs queue for the one inference host, so it gets the cores the parser hosts would have
        // spent on the models themselves: one each, as PdfOcrThreads gives them by default.
        return Serialize(await InferAsync(call with { Threads = Math.Max(call.Threads, Slots) }, pixels, settings, ct));
    }

    /// <summary>The running inference host's process, for tests.</summary>
    internal int? InferenceProcessId => _inference is { HasExited: false } host ? host.ProcessId : null;

    /// <summary>The memory the inference host may use.</summary>
    internal int InferenceMemoryLimitMb => InferenceHostMb;

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
                // Started under another sandbox mode, it is replaced: a stricter setting must not keep
                // running the models in a host confined less than it asks.
                if (_inference is { } previous && (previous.HasExited || previous.SandboxMode != SandboxModeOf(settings)))
                    DiscardInference(previous);
                Host host = _inference ??= StartInference(settings);
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
