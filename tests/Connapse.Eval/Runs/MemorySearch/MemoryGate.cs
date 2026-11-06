using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// Holds new agents while the machine is short of memory and lets them go on its own once there is room, so a long run
/// shares the desktop with whatever else is on it. It never touches other processes: if something else keeps the memory,
/// the run waits. See the guard note in <c>eval/pass-bars.md</c>.
/// </summary>
public sealed class MemoryGate(long floorBytes, Func<long> availableBytes, TextWriter log, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const int DefaultFloorGb = 8;

    /// <summary>Starts one run gets before a failure to start is scored as the run's error.</summary>
    public const int MaxStarts = 5;

    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private int _holds;
    private int _failedStarts;
    private long _heldTicks;

    public int Holds => _holds;

    public int FailedStarts => _failedStarts;

    public TimeSpan Held => TimeSpan.FromTicks(Interlocked.Read(ref _heldTicks));

    public static MemoryGate ForThisMachine(int floorGb, TextWriter log) => new(floorGb * (1L << 30), AvailablePhysicalBytes, log);

    /// <summary>Returns once available memory is at or above the floor, checking every <see cref="CheckEvery"/> until it is.</summary>
    public async Task WaitForRoomAsync(CancellationToken ct)
    {
        long available = availableBytes();
        if (available >= floorBytes)
            return;
        Interlocked.Increment(ref _holds);
        Stopwatch held = Stopwatch.StartNew();
        log.WriteLine($"memory guard: holding at {DateTimeOffset.Now:HH:mm:ss}, {Gb(available)} GB available, floor {Gb(floorBytes)} GB");
        while ((available = availableBytes()) < floorBytes)
            await _delay(CheckEvery, ct);
        Interlocked.Add(ref _heldTicks, held.Elapsed.Ticks);
        log.WriteLine($"memory guard: resumed at {DateTimeOffset.Now:HH:mm:ss}, {Gb(available)} GB available");
    }

    /// <summary>Counts a run that never reached the model, then waits a check interval before it starts again.</summary>
    public async Task RecordFailedStartAsync(int exitCode, CancellationToken ct)
    {
        Interlocked.Increment(ref _failedStarts);
        log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"memory guard: claude failed to start (exit 0x{exitCode:X8}), starting it again"));
        await _delay(CheckEvery, ct);
    }

    public JsonObject Report(int restoredRuns) => new()
    {
        ["floorGb"] = Gb(floorBytes),
        ["holds"] = Holds,
        ["heldSeconds"] = Math.Round(Held.TotalSeconds),
        ["failedStarts"] = FailedStarts,
        ["restoredRuns"] = restoredRuns,
    };

    private static double Gb(long bytes) => Math.Round(bytes / (double)(1L << 30), 1);

    private static long AvailablePhysicalBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            string line = File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal));
            return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
        }
        MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException($"GlobalMemoryStatusEx failed with error {Marshal.GetLastPInvokeError()}.");
        return (long)status.AvailablePhysical;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
