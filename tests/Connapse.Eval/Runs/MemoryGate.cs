using System.Runtime.InteropServices;

namespace Connapse.Eval.Runs;

/// <summary>
/// Holds an eval's next agent run while the machine is short of memory, so a long run shares the PC instead of
/// pushing it into swapping or getting killed. Only Windows reports free memory here; elsewhere nothing waits.
/// </summary>
public static class MemoryGate
{
    /// <summary>Waits until <paramref name="available"/> reports at least <paramref name="floor"/> bytes, or can't tell.</summary>
    public static async Task WaitAsync(long floor, Func<long?> available, TimeSpan poll, TextWriter log, CancellationToken ct)
    {
        bool said = false;
        while (available() is long free && free < floor)
        {
            if (!said)
            {
                log.WriteLine($"[memory] {free / (1024 * 1024)} MB free, under {floor / (1024 * 1024)} MB; pausing until more is free.");
                said = true;
            }
            await Task.Delay(poll, ct);
        }
        if (said)
            log.WriteLine("[memory] enough free memory again; continuing.");
    }

    /// <summary>The machine's available physical memory in bytes, or null when it can't be read.</summary>
    public static long? Available()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
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
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
