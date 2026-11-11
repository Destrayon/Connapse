using System.Runtime.InteropServices;

namespace Connapse.Eval.Runs;

/// <summary>
/// Cancels a run when the machine's free physical memory falls under a floor, so a run starved of memory stops and
/// tears down its containers instead of logging a failed start for every task. Windows only; elsewhere it never fires.
/// </summary>
public sealed class FreeMemoryFloor : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Timer? _timer;

    private FreeMemoryFloor(CancellationTokenSource cts, Timer? timer)
    {
        _cts = cts;
        _timer = timer;
    }

    /// <summary>The token to run under: cancelled with <paramref name="ct"/>, or when free memory drops under <paramref name="floorMb"/>.</summary>
    public CancellationToken Token => _cts.Token;

    public static FreeMemoryFloor Start(int? floorMb, TextWriter log, CancellationToken ct)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (floorMb is not int floor || !OperatingSystem.IsWindows())
            return new FreeMemoryFloor(cts, null);
        Timer timer = new(_ =>
        {
            if (cts.IsCancellationRequested || FreeMb() is not long free || free >= floor)
                return;
            log.WriteLine($"STOPPED: free memory {free} MB is under the {floor} MB floor; cancelling the run.");
            cts.Cancel();
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        return new FreeMemoryFloor(cts, timer);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts.Dispose();
    }

    private static long? FreeMb()
    {
        MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)(status.AvailablePhysical / (1024 * 1024)) : null;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
