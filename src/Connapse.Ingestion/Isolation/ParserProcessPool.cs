using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Ingestion.Parsers;
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
/// the parses that could be in flight at once -- and fewer, with lower limits, when memory could not
/// hold that many at their configured limit (#676). In a container the hosts get its memory limit
/// (read from the cgroup) less <see cref="WebReserveMb"/> for the web process and
/// <see cref="InputBufferMbPerWorker"/> for each worker's file waiting to be parsed; elsewhere half of the
/// machine's memory. Each host is sized for a Layout PDF parse (<see cref="LayoutHostMb"/>): fewer
/// hosts run rather than hosts too small for it, and when even one would be too small, PDFs are read
/// without the layout model rather than failing (IngestionPipeline).
/// </para>
/// </summary>
public sealed class ParserProcessPool : IDisposable
{
    /// <summary>The environment variable that tells a host which process to outlive by no more than a second.</summary>
    public const string ParentProcessIdVariable = "CONNAPSE_PARSERHOST_PARENT_PID";

    /// <summary>Room in a reply for its JSON, metadata and the warnings the host keeps.</summary>
    private const long ResponseOverheadBytes = 8L * 1024 * 1024;

    /// <summary>Outside a container, the share of the machine's memory all hosts together may use.</summary>
    private const double HostShareOfMemory = 0.5;

    /// <summary>In a container, the memory left to the web process; the hosts get the rest of the limit.</summary>
    internal const int WebReserveMb = 512;

    /// <summary>
    /// In a container, the memory also left to the web process per ingestion worker: each holds the
    /// file it is parsing in memory, including while it waits for a free host.
    /// </summary>
    internal const int InputBufferMbPerWorker = 128;

    /// <summary>The least a host is given, however little memory there is.</summary>
    internal const int MinHostMemoryMb = 512;

    /// <summary>
    /// What a host needs to read a PDF with the layout model (#676): measured at about 650-670 MB at its
    /// peak on a text PDF -- about 390 MB of per-page inference memory and 165 MB of weights -- plus
    /// the table model and room for a heap that doesn't give memory straight back.
    /// </summary>
    public const int LayoutHostMb = 900;

    private readonly ILogger<ParserProcessPool> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<Host> _idle = [];
    private readonly ConcurrentDictionary<Host, byte> _all = new();
    private int _missingHostLogged;
    private int _sandboxLogged;
    private bool _disposed;

    public ParserProcessPool(
        IConfiguration? configuration = null,
        ILogger<ParserProcessPool>? logger = null,
        string? hostPath = null,
        long? availableMemoryBytes = null,
        long? containerLimitBytes = null)
    {
        _logger = logger ?? NullLogger<ParserProcessPool>.Instance;
        int workers = int.TryParse(configuration?["Hangfire:IngestionWorkerCount"], out int n) && n > 0 ? n : 4;

        // The cgroup's limit itself: the GC's view of a container is only a fraction of it. A limit
        // above the machine's memory is only a cap, so the machine's memory is what there is.
        long? container = containerLimitBytes;
        if (container is null && availableMemoryBytes is null && OperatingSystem.IsLinux())
        {
            container = ContainerMemoryLimitBytes();
            if (container is null)
                _logger.LogDebug("ParserPool found no cgroup memory limit; sizing hosts from the memory the GC reports");
            else if (MachineMemoryBytes(ReadOrNull("/proc/meminfo")) is { } machine && machine < container)
                container = machine;
        }
        long available = container ?? availableMemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        (Slots, _hostMemoryCeilingMb) = Size(available / (1024 * 1024), container is not null, workers);
        _slots = new SemaphoreSlim(Slots, Slots);
        HostPath = hostPath ?? Path.Combine(AppContext.BaseDirectory, "Connapse.ParserHost.dll");

        _logger.LogInformation(
            "ParserPool runs up to {Slots} parser hosts of at most {CeilingMb} MB each, from the {AvailableMb} MB {Source}",
            Slots, _hostMemoryCeilingMb, available / (1024 * 1024), container is not null ? "container limit" : "available");
        if (_hostMemoryCeilingMb < LayoutHostMb)
            _logger.LogWarning(
                "ParserPool hosts get {CeilingMb} MB, less than the {LayoutMb} MB the PDF layout model needs: PDFs are read without it. "
                + "Give the container more memory to read them by layout.",
                _hostMemoryCeilingMb, LayoutHostMb);
    }

    /// <summary>
    /// Hosts that may run at once and the memory each may use, from the memory there is (MB) and the
    /// ingestion workers: as many hosts as fit at <see cref="LayoutHostMb"/>, at least one.
    /// </summary>
    internal static (int Slots, int HostMb) Size(long availableMb, bool isContainerLimit, int workers)
    {
        long budgetMb = isContainerLimit
            ? availableMb - WebReserveMb - (long)InputBufferMbPerWorker * Math.Max(1, workers)
            : (long)(availableMb * HostShareOfMemory);
        budgetMb = Math.Max(MinHostMemoryMb, budgetMb);
        int slots = (int)Math.Clamp(budgetMb / LayoutHostMb, 1, Math.Max(1, workers));
        return (slots, (int)Math.Min(int.MaxValue, budgetMb / slots));
    }

    /// <summary>The memory limit of the cgroup this process runs in, or null when there is none.</summary>
    private static long? ContainerMemoryLimitBytes() =>
        ContainerMemoryLimitBytes("/sys/fs/cgroup", ReadOrNull("/proc/self/cgroup"));

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The machine's memory from <c>/proc/meminfo</c>'s content (<c>MemTotal</c>), or null.</summary>
    internal static long? MachineMemoryBytes(string? meminfo)
    {
        foreach (string line in (meminfo ?? "").Split('\n'))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0] == "MemTotal:" && long.TryParse(fields[1], out long kb) && kb > 0)
                return kb * 1024;
        }
        return null;
    }

    /// <summary>
    /// The memory limit of the cgroup this process runs in, under the hierarchy mounted at
    /// <paramref name="root"/>: the smallest limit on the path from the process's own cgroup (from
    /// <paramref name="selfCgroup"/>, the content of <c>/proc/self/cgroup</c>) up to the root, since
    /// any of them can kill it. cgroup v2's <c>memory.max</c>, else v1's <c>memory.limit_in_bytes</c>
    /// under <c>memory/</c>. When the process's cgroup isn't visible under the mount, only the
    /// mount's own limit is read. Null when there is no limit.
    /// </summary>
    internal static long? ContainerMemoryLimitBytes(string root, string? selfCgroup)
    {
        try
        {
            string[] lines = (selfCgroup ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string v2Path = lines.Select(l => l.Split(':', 3)).Where(f => f.Length == 3 && f[0] == "0" && f[1] == "")
                .Select(f => f[2]).FirstOrDefault() ?? "/";
            if (File.Exists(Path.Combine(root, "memory.max")) || File.Exists(Path.Combine(root, "cgroup.controllers")))
                return SmallestLimit(root, v2Path, "memory.max");

            string v1Path = lines.Select(l => l.Split(':', 3))
                .Where(f => f.Length == 3 && f[1].Split(',').Contains("memory"))
                .Select(f => f[2]).FirstOrDefault() ?? "/";
            return SmallestLimit(Path.Combine(root, "memory"), v1Path, "memory.limit_in_bytes");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The smallest finite limit in <paramref name="file"/> from <paramref name="cgroup"/> up to <paramref name="mount"/>.</summary>
    private static long? SmallestLimit(string mount, string cgroup, string file)
    {
        string leaf = Path.Combine(mount, cgroup.TrimStart('/'));
        string mountFull = Path.GetFullPath(mount);
        string? dir = Directory.Exists(leaf) ? Path.GetFullPath(leaf) : mountFull;

        long? smallest = null;
        while (dir is not null && dir.StartsWith(mountFull, StringComparison.Ordinal))
        {
            string path = Path.Combine(dir, file);
            if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out long limit)
                && limit > 0 && limit < (1L << 60))
                smallest = smallest is null ? limit : Math.Min(smallest.Value, limit);
            if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), mountFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
                break;
            dir = Path.GetDirectoryName(dir);
        }
        return smallest;
    }

    /// <summary>The memory each host may use at most, whatever the settings ask for.</summary>
    internal int HostMemoryCeilingMb => _hostMemoryCeilingMb;

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

    /// <summary>A host ended, or broke its pipe, before it said it was ready: it never read the file.</summary>
    private sealed class HostNotReadyException(string message, Exception? inner = null) : IOException(message, inner);

    /// <summary>Lets tests change how hosts are started, such as making one fail to start.</summary>
    internal Func<ProcessStartInfo, ProcessStartInfo>? StartInfoForTests { get; set; }

    /// <summary>Hosts started since the pool was made, for tests of the retry.</summary>
    internal int HostsStarted => Volatile.Read(ref _hostsStarted);

    private int _hostsStarted;

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
            ParseResponse response;
            for (int attempt = 1; ; attempt++)
            {
                host = Take(settings);

                // Killing the process is what ends the exchange at the deadline: a pipe read does not
                // reliably observe a token, but it does end when the writer dies.
                Host current = host;
                using var kill = deadline.Token.Register(() => current.Kill());

                try
                {
                    response = await host.ExchangeAsync(new ParseRequest(parser.Name, fileName, settings), content, MaxResponseFrame(settings), deadline.Token);
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Read before the kill: the host's last words say whether it ran out of memory.
                    string stderr = current.CollectErrors();
                    string exit = current.ExitDescription();
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

                    // A host that died before saying it was ready never read the file: it failed to
                    // start -- confining itself, re-executing, loading the runtime (#657). One new host
                    // gets the file, within the same deadline. A host that died after it was ready
                    // died on the file, perhaps killed for memory before the watchdog saw it, and is
                    // not given it again.
                    if (ex is HostNotReadyException && attempt == 1)
                    {
                        _logger.LogWarning(ex, "ParserHostFailedToStart before {Parser} on {FileName}, {Exit}; retrying on a new host: {Stderr}",
                            parser.Name, LogSanitizer.Sanitize(name), exit, stderr);
                        continue;
                    }

                    _logger.LogError(ex, "ParserHostCrashed {Parser} on {FileName}, {Exit}: {Stderr}",
                        parser.Name, LogSanitizer.Sanitize(name), exit, stderr);
                    throw new PermanentIngestionException($"Could not parse {name}: the parser process crashed ({exit}) [parse_crashed]", ex);
                }
            }

            LogSandboxOnce(response.Sandbox);

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

    /// <summary>
    /// Says once how the hosts are confined: a warning when they are not, on Linux, where they
    /// could be.
    /// </summary>
    private void LogSandboxOnce(string? sandbox)
    {
        if (sandbox is null || Interlocked.Exchange(ref _sandboxLogged, 1) == 1)
            return;

        if (sandbox.StartsWith("none", StringComparison.Ordinal) && OperatingSystem.IsLinux())
            _logger.LogWarning("ParserSandbox parser hosts run unconfined: {Sandbox}", sandbox);
        else
            _logger.LogInformation("ParserSandbox parser hosts are confined by {Sandbox}", sandbox);
    }

    private static PermanentIngestionException Timeout(string name, TimeSpan timeout, Exception? inner) =>
        new($"Could not parse {name}: parsing did not finish within {timeout.TotalSeconds:0} seconds [parse_timeout]", inner);

    private static PermanentIngestionException OutOfMemory(string name, int limitMb, Exception? inner) =>
        new($"Could not parse {name}: it needed more than the {limitMb:N0} MB the parser may use [parse_out_of_memory]", inner);

    /// <summary>An idle host started under the same memory limit, or a new one.</summary>
    private Host Take(UploadSettings settings)
    {
        while (_idle.TryTake(out var idle))
        {
            if (idle.MemoryLimitMb == MemoryLimitMb(settings) && idle.SandboxMode == SandboxModeOf(settings) && !idle.HasExited)
                return idle;
            idle.Kill();
            _all.TryRemove(idle, out _);
        }

        int limitMb = MemoryLimitMb(settings);
        ParserSandboxMode sandbox = SandboxModeOf(settings);
        ProcessStartInfo start = StartInfo(HostPath, limitMb, sandboxMode: sandbox);
        var host = Host.Start(StartInfoForTests?.Invoke(start) ?? start, limitMb, sandbox);
        Interlocked.Increment(ref _hostsStarted);
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
    private static ParserSandboxMode SandboxModeOf(UploadSettings settings) =>
        Enum.TryParse(settings.ParserSandbox, ignoreCase: true, out ParserSandboxMode mode) ? mode : ParserSandboxMode.Auto;

    internal static ProcessStartInfo StartInfo(
        string hostPath, int memoryLimitMb, int? parentProcessId = null, ParserSandboxMode sandboxMode = ParserSandboxMode.Auto)
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
        // process's own environment. On Linux the host also confines itself with Landlock
        // (ParserSandbox, #641), which closes those.
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
        start.Environment[ParserSandbox.ModeVariable] = sandboxMode.ToString();

        // ONNX Runtime's Linux build sends telemetry to Microsoft unless this is set before it
        // loads (see PdfOcr.DisableOnnxRuntimeTelemetry).
        start.Environment[PdfOcr.TelemetryVariable] = "1";

        // The runtime's diagnostics server opens a Unix socket at start-up; the sandbox allows no
        // sockets, and nothing should be attaching to a parser host anyway.
        start.Environment["DOTNET_EnableDiagnostics"] = "0";

        // A temp folder of the host's own, the only place the sandbox lets it write: ONNX Runtime
        // writes a log to $TMPDIR and crashes when it cannot. The shared temp folder stays closed,
        // because it holds other users' uploads in flight.
        string temp = Path.Combine(Path.GetTempPath(), $"connapse-parserhost-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(temp);
        else
            Directory.CreateDirectory(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        start.Environment["TMPDIR"] = temp;
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        start.Environment[ParserSandbox.TempVariable] = temp;
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
        private bool _ready;

        /// <summary>A ready frame is a few dozen bytes; anything larger is not one.</summary>
        private const int MaxReadyFrame = 64 * 1024;

        private Host(Process process, int memoryLimitMb, ParserSandboxMode sandboxMode)
        {
            _process = process;
            ProcessId = process.Id;
            MemoryLimitMb = memoryLimitMb;
            SandboxMode = sandboxMode;
        }

        public ParserSandboxMode SandboxMode { get; }

        /// <summary>The host's private temp folder, removed when the host ends.</summary>
        private string? TempDirectory { get; init; }

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

        /// <summary>
        /// How the process ended, for the crash report: its exit code, and on Linux and macOS the
        /// signal a code above 128 stands for (.NET reports a signalled process as 128 + signal).
        /// Call after <see cref="CollectErrors"/>, which waits for the process to end.
        /// </summary>
        public string ExitDescription()
        {
            try
            {
                if (!_process.HasExited)
                    return "still running";
                int code = _process.ExitCode;
                return !OperatingSystem.IsWindows() && code > 128 && code < 128 + 65
                    ? $"exit code {code}, signal {code - 128}{SignalName(code - 128)}"
                    : $"exit code {code}";
            }
            catch (InvalidOperationException)
            {
                return "exit code unknown";
            }
        }

        private static string SignalName(int signal) => signal switch
        {
            4 => " (SIGILL)",
            6 => " (SIGABRT)",
            7 => " (SIGBUS)",
            9 => " (SIGKILL)",
            11 => " (SIGSEGV)",
            31 => " (SIGSYS, a blocked system call)",
            _ => "",
        };

        public static Host Start(ProcessStartInfo start, int memoryLimitMb, ParserSandboxMode sandboxMode)
        {
            var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.ArgumentList[0]}.");
            var host = new Host(process, memoryLimitMb, sandboxMode)
            {
                TempDirectory = start.Environment.TryGetValue(ParserSandbox.TempVariable, out string? temp) ? temp : null,
            };
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

            // The first exchange waits for the host to say it started; the file is only written
            // once it has, so a host that fails to start never sees it.
            if (!_ready)
            {
                try
                {
                    byte[] ready = await ReadFrameAsync(_process.StandardOutput.BaseStream, MaxReadyFrame, ct)
                        ?? throw new HostNotReadyException("The parser host exited before it was ready.");
                    Deserialize<Ready>(ready);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or HostNotReadyException))
                {
                    throw new HostNotReadyException("The parser host broke off before it was ready.", ex);
                }
                _ready = true;
            }

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
                DeleteTemp();
            }
        }

        private void DeleteTemp()
        {
            if (TempDirectory is null)
                return;
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file still open on Windows, or already gone: temp is cleaned eventually anyway.
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
