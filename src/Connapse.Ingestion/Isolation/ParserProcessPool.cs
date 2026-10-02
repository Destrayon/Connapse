using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Connapse.Ingestion.Isolation.ParserProtocol;

namespace Connapse.Ingestion.Isolation;

/// <summary>
/// Runs the built-in parsers in Connapse.ParserHost processes (#624), so a parse that spins,
/// exhausts memory or crashes can be killed without taking the web process with it.
/// <para>
/// .NET cannot stop a thread: before this, a parser that ignored its token kept a thread-pool
/// thread and its buffers after its document failed with <c>parse_timeout</c>. A process can be
/// killed. Each host's managed heap is capped with <c>DOTNET_GCHeapHardLimit</c>; at the deadline,
/// on a crash, or after it runs out of memory it is killed and the next request starts a fresh one.
/// Hosts are reused, one file at a time, and recycled after a set number of files -- the pattern
/// of Apache Tika's forked parser -- so most files do not pay for a process start.
/// </para>
/// <para>
/// At most as many hosts run as there are ingestion workers (<c>Hangfire:IngestionWorkerCount</c>),
/// the parses that could be in flight at once -- and fewer, with lower limits, when the memory
/// available to the process (the container's limit, in a container) could not hold that many at
/// their configured limit. Hosts together get at most half of it: the rest is the web process's,
/// and in a container a host that crossed it would get both killed.
/// </para>
/// </summary>
public sealed class ParserProcessPool : IDisposable
{
    /// <summary>The environment variable that tells a host which process to outlive by no more than a second.</summary>
    public const string ParentProcessIdVariable = "CONNAPSE_PARSERHOST_PARENT_PID";

    /// <summary>Room in a reply for its JSON, metadata and the warnings the host keeps.</summary>
    private const long ResponseOverheadBytes = 8L * 1024 * 1024;

    /// <summary>The share of the available memory all hosts together may use.</summary>
    private const double HostShareOfMemory = 0.5;

    /// <summary>The least a host is given; below it OCR and large PDFs cannot run, so fewer hosts run.</summary>
    internal const int MinHostMemoryMb = 512;

    private readonly ILogger<ParserProcessPool> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<Host> _idle = [];
    private readonly ConcurrentDictionary<Host, byte> _all = new();
    private int _missingHostLogged;
    private bool _disposed;

    public ParserProcessPool(
        IConfiguration? configuration = null,
        ILogger<ParserProcessPool>? logger = null,
        string? hostPath = null,
        long? availableMemoryBytes = null)
    {
        _logger = logger ?? NullLogger<ParserProcessPool>.Instance;
        int workers = int.TryParse(configuration?["Hangfire:IngestionWorkerCount"], out int n) && n > 0 ? n : 4;

        // The GC reports the container's memory limit when there is one, else the machine's.
        long available = availableMemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long budgetMb = Math.Max(MinHostMemoryMb, (long)(available * HostShareOfMemory / (1024 * 1024)));
        Slots = (int)Math.Clamp(budgetMb / MinHostMemoryMb, 1, workers);
        _hostMemoryCeilingMb = (int)Math.Min(int.MaxValue, budgetMb / Slots);
        _slots = new SemaphoreSlim(Slots, Slots);
        HostPath = hostPath ?? Path.Combine(AppContext.BaseDirectory, "Connapse.ParserHost.dll");

        _logger.LogInformation(
            "ParserPool runs up to {Slots} parser hosts of at most {CeilingMb} MB each, half of the {AvailableMb} MB available",
            Slots, _hostMemoryCeilingMb, available / (1024 * 1024));
    }

    /// <summary>Hosts that may run at once.</summary>
    internal int Slots { get; }

    private readonly int _hostMemoryCeilingMb;

    /// <summary>
    /// The configured per-host limit, lowered so that every host at it still fits the share of
    /// memory the hosts may use together.
    /// </summary>
    internal int MemoryLimitMb(UploadSettings settings) => Math.Min(Math.Max(64, settings.ParserMemoryLimitMb), _hostMemoryCeilingMb);

    /// <summary>The Connapse.ParserHost assembly the pool starts with the dotnet host.</summary>
    public string HostPath { get; }

    /// <summary>Host processes running now, busy or idle.</summary>
    internal IReadOnlyList<int> HostProcessIds => _all.Keys.Select(h => h.ProcessId).ToList();

    /// <summary>
    /// True for the parsers the host carries -- Connapse's own -- when the host is deployed.
    /// Anything else (a test double, a parser added by a later integration) runs in-process.
    /// </summary>
    public bool CanRun(IDocumentParser parser)
    {
        if (parser.GetType().Assembly != typeof(ParserProcessPool).Assembly)
            return false;
        if (File.Exists(HostPath))
            return true;

        if (Interlocked.Exchange(ref _missingHostLogged, 1) == 0)
        {
            _logger.LogError(
                "ParserHostMissing: {HostPath} is not deployed, so documents are parsed inside the web process without isolation",
                HostPath);
        }

        return false;
    }

    /// <summary>
    /// Parses one file in a host process. A failure the parser reports, a deadline passed, memory
    /// exhausted or a host crash all surface as <see cref="PermanentIngestionException"/> with the
    /// pipeline's reason codes; cancellation by the caller surfaces as cancellation.
    /// </summary>
    public async Task<ParsedDocument> ParseAsync(
        IDocumentParser parser,
        ReadOnlyMemory<byte> content,
        string fileName,
        UploadSettings settings,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string name = Path.GetFileName(fileName);

        // The deadline covers the whole parse, the wait for a free host included: otherwise a file
        // queued behind stuck parses waits out their deadlines before its own starts.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await _slots.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw Timeout(name, timeout, ex);
        }

        Host? host = null;
        try
        {
            host = Take(settings);

            // Killing the process is what ends the exchange at the deadline: a pipe read does not
            // reliably observe a token, but it does end when the writer dies.
            Host current = host;
            using var kill = deadline.Token.Register(() => current.Kill());

            ParseResponse response;
            try
            {
                response = await host.ExchangeAsync(new ParseRequest(parser.Name, fileName, settings), content, MaxResponseFrame(settings), deadline.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Read before the kill: the host's last words say whether it ran out of memory.
                string stderr = current.CollectErrors();
                Discard(ref host);
                if (current.KilledForMemory)
                    throw OutOfMemory(name, MemoryLimitMb(settings), ex);
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                if (deadline.IsCancellationRequested)
                {
                    _logger.LogWarning("ParserHostKilled {Parser} on {FileName} at the {TimeoutSeconds} s deadline",
                        parser.Name, LogSanitizer.Sanitize(name), timeout.TotalSeconds);
                    throw Timeout(name, timeout, ex);
                }

                if (stderr.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase))
                    throw OutOfMemory(name, MemoryLimitMb(settings), ex);

                _logger.LogError(ex, "ParserHostCrashed {Parser} on {FileName}: {Stderr}",
                    parser.Name, LogSanitizer.Sanitize(name), stderr);
                throw new PermanentIngestionException($"Could not parse {name}: the parser process crashed [parse_crashed]", ex);
            }

            if (response.OutOfMemory)
            {
                Discard(ref host);
                throw OutOfMemory(name, MemoryLimitMb(settings), null);
            }

            if (response.PermanentError is { } permanent)
                throw new PermanentIngestionException(permanent);
            if (response.Error is { } error)
                throw new PermanentIngestionException($"Could not parse {name}: {error}");

            return new ParsedDocument(response.Content ?? string.Empty, response.Metadata ?? [], response.Warnings ?? []);
        }
        finally
        {
            Return(host, settings);
            _slots.Release();
        }
    }

    /// <summary>
    /// The host refuses text over MaxExtractedCharacters before it replies, so a reply is at most
    /// that many characters -- six bytes each if every one is JSON-escaped -- plus the rest.
    /// </summary>
    private static int MaxResponseFrame(UploadSettings settings) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, settings.MaxExtractedCharacters) * 6L + ResponseOverheadBytes);

    private static PermanentIngestionException Timeout(string name, TimeSpan timeout, Exception? inner) =>
        new($"Could not parse {name}: parsing did not finish within {timeout.TotalSeconds:0} seconds [parse_timeout]", inner);

    private static PermanentIngestionException OutOfMemory(string name, int limitMb, Exception? inner) =>
        new($"Could not parse {name}: it needed more than the {limitMb:N0} MB the parser may use [parse_out_of_memory]", inner);

    /// <summary>An idle host started under the same memory limit, or a new one.</summary>
    private Host Take(UploadSettings settings)
    {
        while (_idle.TryTake(out var idle))
        {
            if (idle.MemoryLimitMb == MemoryLimitMb(settings) && !idle.HasExited)
                return idle;
            idle.Kill();
            _all.TryRemove(idle, out _);
        }

        int limitMb = MemoryLimitMb(settings);
        var host = Host.Start(StartInfo(HostPath, limitMb), limitMb);
        _all[host] = 0;
        return host;
    }

    private void Return(Host? host, UploadSettings settings)
    {
        if (host is null)
            return;

        if (_disposed || host.HasExited || ++host.FilesParsed >= Math.Max(1, settings.ParserFilesPerProcess))
        {
            host.Kill();
            _all.TryRemove(host, out _);
            return;
        }

        _idle.Add(host);
    }

    private void Discard(ref Host? host)
    {
        if (host is null)
            return;
        host.Kill();
        _all.TryRemove(host, out _);
        host = null;
    }

    /// <summary>
    /// How a host is started: by the dotnet host, with its heap capped and told which process is
    /// its parent, so that it exits if the web process dies mid-parse instead of spinning on.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string hostPath, int memoryLimitMb, int? parentProcessId = null)
    {
        var start = new ProcessStartInfo(Host.DotnetMuxer())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(hostPath);

        // The host parses untrusted files with native code (PDFium, ONNX Runtime). It inherits
        // nothing from the web process's environment beyond what running .NET needs, so secrets
        // are not handed to it. That narrows a compromise, but is not a sandbox: the host runs
        // as the same user, can read what that user can, and on Linux may be able to read the web
        // process's own environment. A separate identity is #641.
        var inherited = start.Environment.ToList();
        start.Environment.Clear();
        foreach (var (key, value) in inherited)
        {
            if (value is not null && InheritedVariables.Contains(key))
                start.Environment[key] = value;
        }

        // Three quarters of the process's limit for the managed heap: past it, allocations throw
        // OutOfMemoryException in the host. The rest is for native memory, which the watchdog
        // counts along with everything else (see Host.ExchangeAsync).
        long bytes = Math.Max(64, memoryLimitMb) * 1024L * 1024L * 3 / 4;
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x" + bytes.ToString("X");
        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment[ParentProcessIdVariable] = (parentProcessId ?? Environment.ProcessId).ToString();
        return start;
    }

    /// <summary>
    /// The environment a host is given: what locates the runtime, the temp directory and the
    /// culture, and what Windows needs to start a process at all. Nothing that configures Connapse.
    /// </summary>
    private static readonly HashSet<string> InheritedVariables = new(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "TZ", "TMPDIR", "TEMP", "TMP",
        "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "DOTNET_ROOT(x86)",
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
    };

    public void Dispose()
    {
        _disposed = true;
        foreach (var host in _all.Keys)
            host.Kill();
        _all.Clear();
    }

    /// <summary>One Connapse.ParserHost process and its pipes.</summary>
    private sealed class Host
    {
        private const int ErrorTailLines = 40;

        private readonly Process _process;
        private readonly ConcurrentQueue<string> _errorTail = new();
        private int _killed;

        private Host(Process process, int memoryLimitMb)
        {
            _process = process;
            ProcessId = process.Id;
            MemoryLimitMb = memoryLimitMb;
        }

        public int ProcessId { get; }
        public int MemoryLimitMb { get; }

        /// <summary>Set when the watchdog killed the host for using more memory than its limit.</summary>
        public bool KilledForMemory { get; private set; }

        /// <summary>How often the watchdog reads the host's memory while it parses.</summary>
        private static readonly TimeSpan MemoryCheckInterval = TimeSpan.FromMilliseconds(250);
        public int FilesParsed { get; set; }
        public bool HasExited => Volatile.Read(ref _killed) == 1 || _process.HasExited;

        /// <summary>
        /// The last lines the host wrote to stderr, after giving a dying process a moment to finish:
        /// the async reader only has them all once the process has exited.
        /// </summary>
        public string CollectErrors()
        {
            try
            {
                if (Volatile.Read(ref _killed) == 0 && _process.WaitForExit(TimeSpan.FromSeconds(2)))
                    _process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                // Disposed by a concurrent kill.
            }

            return string.Join('\n', _errorTail);
        }

        public static Host Start(ProcessStartInfo start, int memoryLimitMb)
        {
            var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.ArgumentList[0]}.");
            var host = new Host(process, memoryLimitMb);
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data is null)
                    return;
                host._errorTail.Enqueue(e.Data);
                while (host._errorTail.Count > ErrorTailLines)
                    host._errorTail.TryDequeue(out _);
            };
            process.BeginErrorReadLine();
            return host;
        }

        public async Task<ParseResponse> ExchangeAsync(ParseRequest request, ReadOnlyMemory<byte> content, int maxResponse, CancellationToken ct)
        {
            // The heap limit covers managed memory only. ONNX Runtime, PDFium and Skia allocate
            // natively, and in a container that memory counts against the limit the web process
            // shares: a host that grew past it would get both killed. So the whole process is
            // watched while it parses, and killed past its limit.
            long limit = Math.Max(64, MemoryLimitMb) * 1024L * 1024L;
            using var watchdog = new Timer(_ =>
            {
                try
                {
                    _process.Refresh();
                    if (_process.WorkingSet64 > limit)
                    {
                        KilledForMemory = true;
                        Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited or disposed between checks.
                }
            }, null, MemoryCheckInterval, MemoryCheckInterval);

            Stream input = _process.StandardInput.BaseStream;
            await WriteJsonAsync(input, request, ct);
            await WriteFrameAsync(input, content, ct);
            await input.FlushAsync(ct);

            byte[] reply = await ReadFrameAsync(_process.StandardOutput.BaseStream, maxResponse, ct)
                ?? throw new EndOfStreamException("The parser host exited without replying.");
            return Deserialize<ParseResponse>(reply);
        }

        public void Kill()
        {
            if (Interlocked.Exchange(ref _killed, 1) == 1)
                return;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);

                    // On Linux the kill is a signal that returns before the process is gone; waiting
                    // reaps it, so a killed host has left by the time the pool reports it killed.
                    _process.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                _process.Dispose();
            }
        }

        /// <summary>
        /// The dotnet host to run the host assembly with: this process's own when it is the dotnet
        /// host (production runs "dotnet Connapse.Web.dll"), else the one that launched the build,
        /// else the one beside the running shared framework.
        /// </summary>
        internal static string DotnetMuxer()
        {
            string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
            if (Environment.ProcessPath is { } self && Path.GetFileName(self).Equals(exe, StringComparison.OrdinalIgnoreCase))
                return self;
            if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } fromBuild && File.Exists(fromBuild))
                return fromBuild;

            // .../dotnet/shared/Microsoft.NETCore.App/<version>/ -> .../dotnet/
            string runtime = RuntimeEnvironment.GetRuntimeDirectory();
            string? root = Directory.GetParent(runtime.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Parent?.Parent?.FullName;
            return root is not null && File.Exists(Path.Combine(root, exe)) ? Path.Combine(root, exe) : exe;
        }
    }
}
