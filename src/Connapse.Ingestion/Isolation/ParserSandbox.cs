using System.Globalization;
using System.Runtime.InteropServices;

namespace Connapse.Ingestion.Isolation;

/// <summary>
/// Confines the parser host on Linux with Landlock (#641) before it reads a single untrusted byte.
/// <para>
/// The host parses hostile files with native code (PDFium, ONNX Runtime, SkiaSharp), and a memory
/// bug in any of them can run an attacker's code. Without this, that code would run as the web
/// app's user: it could read the app's settings, its generated signing key in <c>appdata</c>, the
/// knowledge store, and reach the network. Landlock is a kernel feature an unprivileged process
/// applies to itself -- no root, no extra container -- and cannot undo:
/// </para>
/// <list type="bullet">
/// <item>Files: read-only access to the .NET runtime, the system libraries, and the app's own
/// assemblies, native runtimes and OCR models. Nothing else -- not <c>appsettings*.json</c>, not
/// <c>appdata</c>, not the knowledge store, not the shared temp directory -- and no writes
/// anywhere but <c>/dev/null</c> and a private temp folder the pool makes for each host (ONNX
/// Runtime writes a log to <c>$TMPDIR</c>). The file itself arrives over stdin.</item>
/// <item>Network (Landlock ABI 4+): no TCP connect or bind.</item>
/// <item>Other processes (ABI 6+): no signals to, and no abstract Unix sockets of, processes outside
/// the sandbox. Landlock also denies ptrace and <c>/proc/&lt;pid&gt;/environ</c> or <c>mem</c> of any
/// process outside the sandbox, including the web process.</item>
/// </list>
/// <para>
/// Elsewhere -- Windows, macOS, or a Linux kernel without Landlock -- the host runs unconfined, and
/// <see cref="ParserSandboxMode.Required"/> makes it refuse to parse instead.
/// </para>
/// </summary>
public static class ParserSandbox
{
    /// <summary>The environment variable carrying the mode from the pool to the host.</summary>
    public const string ModeVariable = "CONNAPSE_PARSERHOST_SANDBOX";

    /// <summary>The host's private temp folder, made by the pool: the one place it may write.</summary>
    public const string TempVariable = "CONNAPSE_PARSERHOST_TEMP";

    private const long SysLandlockCreateRuleset = 444;
    private const long SysLandlockAddRule = 445;
    private const long SysLandlockRestrictSelf = 446;
    private const uint CreateRulesetVersion = 1;
    private const int RulePathBeneath = 1;
    private const int PrSetNoNewPrivs = 38;
    private const int OPath = 0x200000;
    private const int OCloexec = 0x80000;

    // Filesystem rights, by the ABI that introduced them.
    private const ulong Execute = 1UL << 0, WriteFile = 1UL << 1, ReadFile = 1UL << 2, ReadDir = 1UL << 3;
    private const ulong AbiOneFs = (1UL << 13) - 1;       // EXECUTE through MAKE_SYM
    private const ulong Refer = 1UL << 13;                 // ABI 2
    private const ulong Truncate = 1UL << 14;              // ABI 3
    private const ulong IoctlDev = 1UL << 15;              // ABI 5
    private const ulong NetBindTcp = 1, NetConnectTcp = 2; // ABI 4
    private const ulong ScopeAbstractUnixSocket = 1, ScopeSignal = 2; // ABI 6

    /// <summary>Directories under the app folder the host may read, besides culture folders of satellite assemblies.</summary>
    private static readonly string[] AppDirectories = ["runtimes", "models"];

    /// <summary>System locations the runtime and native libraries read after start-up.</summary>
    private static readonly string[] SystemPaths =
    [
        "/lib", "/lib64", "/usr/lib", "/usr/lib64", "/usr/local/lib", "/etc/ld.so.cache", "/etc/localtime",
        "/usr/share/zoneinfo", "/usr/share/icu", "/proc", "/sys/fs/cgroup", "/sys/devices/system/cpu",
        "/dev/urandom", "/dev/random", "/dev/zero",
    ];

    /// <summary>
    /// Confines this process as far as the kernel allows, and says how: "landlock (ABI 7)", or why
    /// not ("none: ..."). Called once, before the first request is read.
    /// </summary>
    /// <summary>How this process was confined, once <see cref="Apply"/> has run.</summary>
    public static string? Current { get; private set; }

    /// <summary>Set by the confined process for the image it re-executes into; never by the pool.</summary>
    private const string ConfinedVariable = "CONNAPSE_PARSERHOST_CONFINED";

    /// <summary>
    /// Confines the process and re-executes it, or, in the re-executed process, reports how it was
    /// confined. Returns only when the process will run unconfined, or is already confined.
    /// <para>
    /// Landlock restricts the thread that applies it and the threads that thread creates later --
    /// not threads that already exist, and .NET has a thread pool, a finalizer and more running
    /// before <c>Main</c>. Parsing on any of those would be unconfined, which the first version of
    /// this did, and its tests caught. So the rules are applied on this thread, which then replaces
    /// the whole process with execve: the new image starts as one confined thread, and every thread
    /// the runtime then creates inherits the sandbox. The process id, and the pipes to the pool,
    /// are unchanged.
    /// </para>
    /// </summary>
    public static (bool Applied, string Description) Apply(ParserSandboxMode mode)
    {
        if (mode != ParserSandboxMode.Off && Environment.GetEnvironmentVariable(ConfinedVariable) is { Length: > 0 } confined)
        {
            Current = confined;
            return (true, confined);
        }

        var result = ApplyCore(mode);
        if (result.Applied)
            result = ReExecuteConfined(result.Description);

        Current = result.Description;
        return result;
    }

    /// <summary>Re-executes this process from the now-confined thread; returns only if that fails.</summary>
    private static (bool Applied, string Description) ReExecuteConfined(string description)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
            return (false, "none: could not tell how this process was started, to restart it confined");

        // "dotnet host.dll": the arguments start with the assembly. An apphost: they start with itself.
        string[] args = Environment.GetCommandLineArgs();
        bool viaMuxer = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        string?[] argv = [exe, .. viaMuxer ? args : args.Skip(1), null];

        try
        {
            _ = SetEnv(ConfinedVariable, description, overwrite: 1);
            _ = ExecV(exe, argv);
        }
        catch (Exception ex) when (ex is MarshalDirectiveException or DllNotFoundException or EntryPointNotFoundException)
        {
            return (false, $"none: restarting confined failed ({ex.Message})");
        }

        // Still here: the restart failed. This thread is confined and the others are not, which is
        // no sandbox at all.
        return (false, $"none: restarting confined failed (errno {Marshal.GetLastPInvokeError()})");
    }

    private static (bool Applied, string Description) ApplyCore(ParserSandboxMode mode)
    {
        if (mode == ParserSandboxMode.Off)
            return (false, "none: turned off");
        if (!OperatingSystem.IsLinux())
            return (false, $"none: Landlock is Linux-only ({RuntimeInformation.OSDescription})");

        try
        {
            long abi = Syscall(SysLandlockCreateRuleset, IntPtr.Zero, 0, CreateRulesetVersion);
            if (abi < 1)
                return (false, $"none: this kernel has no Landlock (errno {Marshal.GetLastPInvokeError()})");

            ulong handledFs = AbiOneFs | (abi >= 2 ? Refer : 0) | (abi >= 3 ? Truncate : 0) | (abi >= 5 ? IoctlDev : 0);
            var attr = new RulesetAttr
            {
                HandledAccessFs = handledFs,
                HandledAccessNet = abi >= 4 ? NetBindTcp | NetConnectTcp : 0,
                Scoped = abi >= 6 ? ScopeAbstractUnixSocket | ScopeSignal : 0,
            };
            nuint size = abi >= 6 ? 24u : abi >= 4 ? 16u : 8u;
            int ruleset = (int)CreateRuleset(SysLandlockCreateRuleset, ref attr, size, 0);
            if (ruleset < 0)
                return (false, $"none: landlock_create_ruleset failed (errno {Marshal.GetLastPInvokeError()})");

            try
            {
                foreach (string path in ReadablePaths())
                    Allow(ruleset, path, Execute | ReadFile | ReadDir);
                Allow(ruleset, "/dev/null", ReadFile | WriteFile | (abi >= 3 ? Truncate : 0));

                // Its own temp folder, and only when the pool made one: a host started any other
                // way gets no writable folder at all, rather than the shared one.
                if (Environment.GetEnvironmentVariable(TempVariable) is { Length: > 0 } temp && Directory.Exists(temp))
                    Allow(ruleset, temp, handledFs);

                if (Prctl(PrSetNoNewPrivs, 1, 0, 0, 0) != 0)
                    return (false, $"none: no_new_privs failed (errno {Marshal.GetLastPInvokeError()})");
                if (Syscall(SysLandlockRestrictSelf, (IntPtr)ruleset, 0, 0) != 0)
                    return (false, $"none: landlock_restrict_self failed (errno {Marshal.GetLastPInvokeError()})");
            }
            finally
            {
                Close(ruleset);
            }

            return (true, $"landlock (ABI {abi})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            // Nothing is restricted until landlock_restrict_self succeeds, so the process is
            // unconfined, not half-confined.
            return (false, $"none: {ex.Message}");
        }
    }

    /// <summary>The mode the pool asked for, Auto when it said nothing or something unknown.</summary>
    public static ParserSandboxMode ModeFromEnvironment() =>
        Enum.TryParse(Environment.GetEnvironmentVariable(ModeVariable), ignoreCase: true, out ParserSandboxMode mode)
            ? mode
            : ParserSandboxMode.Auto;

    /// <summary>
    /// The .NET installation, the system paths, and the app folder's own files and code folders --
    /// not its settings files and not any data folder.
    /// </summary>
    internal static IEnumerable<string> ReadablePaths()
    {
        if (DotnetRoot() is { } dotnet)
            yield return dotnet;
        foreach (string path in SystemPaths)
            yield return path;

        string app = AppContext.BaseDirectory;
        foreach (string file in Directory.EnumerateFiles(app))
        {
            if (!Path.GetFileName(file).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }

        foreach (string directory in Directory.EnumerateDirectories(app))
        {
            string name = Path.GetFileName(directory);
            if (AppDirectories.Contains(name, StringComparer.OrdinalIgnoreCase) || IsCultureFolder(directory))
                yield return directory;
        }
    }

    /// <summary>A folder of satellite resource assemblies: named for a culture, holding only them.</summary>
    private static bool IsCultureFolder(string directory)
    {
        try
        {
            _ = CultureInfo.GetCultureInfo(Path.GetFileName(directory), predefinedOnly: true);
            return Directory.EnumerateFileSystemEntries(directory)
                .All(f => f.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>.../dotnet/shared/Microsoft.NETCore.App/&lt;version&gt;/ -> .../dotnet</summary>
    private static string? DotnetRoot()
    {
        string runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('/');
        return Directory.GetParent(runtime)?.Parent?.Parent?.FullName;
    }

    /// <summary>
    /// Allows <paramref name="access"/> beneath one path. Directory-only rights are dropped for a
    /// file, which the kernel would refuse; a path that does not exist is skipped.
    /// </summary>
    private static void Allow(int ruleset, string path, ulong access)
    {
        int fd = Open(path, OPath | OCloexec);
        if (fd < 0)
            return;

        try
        {
            if (!Directory.Exists(path))
                access &= Execute | ReadFile | WriteFile | Truncate | IoctlDev;

            var rule = new PathBeneathAttr { AllowedAccess = access, ParentFd = fd };
            if (AddRule(SysLandlockAddRule, ruleset, RulePathBeneath, ref rule, 0) != 0)
            {
                throw new InvalidOperationException(
                    $"landlock_add_rule failed for {path} (errno {Marshal.GetLastPInvokeError()})");
            }
        }
        finally
        {
            Close(fd);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RulesetAttr
    {
        public ulong HandledAccessFs;
        public ulong HandledAccessNet;
        public ulong Scoped;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PathBeneathAttr
    {
        public ulong AllowedAccess;
        public int ParentFd;
    }

    // syscall(2) is variadic; on Linux x64 and arm64 integer arguments are passed the same way
    // either way, so fixed signatures per call are safe.
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, IntPtr a, nuint b, uint c);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long CreateRuleset(long number, ref RulesetAttr attr, nuint size, uint flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long AddRule(long number, int ruleset, int type, ref PathBeneathAttr attr, uint flags);

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int Prctl(int option, ulong a, ulong b, ulong c, ulong d);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int ExecV(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        // LPStr is UTF-8 on Linux; the array marshaller does not take LPUTF8Str.
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv);

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnv(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);
}

/// <summary>Whether the parser host confines itself (see <see cref="ParserSandbox"/>).</summary>
public enum ParserSandboxMode
{
    /// <summary>Confine where the kernel allows; run unconfined elsewhere, with a warning.</summary>
    Auto,

    /// <summary>Confine, or refuse to parse at all.</summary>
    Required,

    /// <summary>Never confine.</summary>
    Off,
}
