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
/// <item>Network: no sockets at all -- a seccomp filter refuses socket(2) for every family, so no
/// TCP, UDP, DNS, netlink or Unix-socket connection can carry a document out. Landlock's own TCP
/// rules (ABI 4+) are kept as a second layer; it has no rules for UDP.</item>
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

    /// <summary>The oldest Landlock ABI <see cref="ParserSandboxMode.Required"/> accepts: the first that can deny truncation.</summary>
    internal const long MinRequiredAbi = 3;

    /// <summary>Directories under the app folder the host may read, besides culture folders of satellite assemblies.</summary>
    private static readonly string[] AppDirectories = ["runtimes", "models"];

    /// <summary>System locations the runtime and native libraries read after start-up.</summary>
    private static readonly string[] SystemPaths =
    [
        "/lib", "/lib64", "/usr/lib", "/usr/lib64", "/usr/local/lib", "/etc/ld.so.cache", "/etc/localtime",
        "/usr/share/zoneinfo", "/usr/share/icu", "/proc", "/sys/fs/cgroup", "/sys/devices/system/cpu",
        "/dev/urandom", "/dev/random", "/dev/zero",
    ];

    /// <summary>How this process was confined, once <see cref="Apply"/> has run.</summary>
    public static string? Current { get; private set; }

    /// <summary>Set by the confined process for the image it re-executes into; never by the pool.</summary>
    private const string ConfinedVariable = "CONNAPSE_PARSERHOST_CONFINED";

    /// <summary>Set before a restart that follows an unverifiable marker, so a failure cannot loop.</summary>
    private const string RestartedVariable = "CONNAPSE_PARSERHOST_RESTARTED";

    /// <summary>
    /// True when this process demonstrably runs inside the sandbox: listing <c>/</c>, which no rule
    /// allows, is refused, and so is opening an internet socket.
    /// </summary>
    internal static bool IsConfined()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        int root = Open("/", 0); // O_RDONLY: a directory needs READ_DIR, which "/" is not granted.
        if (root >= 0)
        {
            Close(root);
            return false;
        }

        int socket = Socket(2, 2, 0); // AF_INET, SOCK_DGRAM
        if (socket >= 0)
        {
            Close(socket);
            return false;
        }

        return true;
    }

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int Socket(int domain, int type, int protocol);

    /// <summary>
    /// Confines this process as far as the kernel allows, and says how ("landlock (ABI 7) and
    /// seccomp (no sockets)") or why not ("none: ..."). Called once, before the first request is
    /// read. Confining ends in a re-exec; in the re-executed process this reports how it was
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
            // The marker only says the previous image meant to confine this one. Anything that can
            // set an environment variable could forge it, so the sandbox is checked, not trusted.
            if (IsConfined())
            {
                Current = confined;
                return (true, confined);
            }

            // Forged, or the restart lost the sandbox. Confine now -- once: a second unverified
            // restart means the kernel is not doing what it reports, and the host says so.
            if (Environment.GetEnvironmentVariable(RestartedVariable) is { Length: > 0 })
            {
                Current = "none: confinement could not be verified after restarting";
                return (false, Current);
            }
            _ = SetEnv(RestartedVariable, "1", overwrite: 1);
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

        var gaps = new List<string>();
        try
        {
            long abi = Syscall(SysLandlockCreateRuleset, IntPtr.Zero, 0, CreateRulesetVersion);
            if (abi < 1)
                return (false, $"none: this kernel has no Landlock (errno {Marshal.GetLastPInvokeError()})");

            // Below ABI 3 a file outside the allow-list can still be truncated -- emptied -- so
            // Required, which promises the rest of the system is out of reach, refuses it.
            if (mode == ParserSandboxMode.Required && abi < MinRequiredAbi)
            {
                return (false, $"none: Landlock ABI {abi} cannot stop files being truncated; " +
                               $"Required needs ABI {MinRequiredAbi} (Linux 6.2) or later");
            }

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

                // Like Landlock, a seccomp filter binds this thread and what it starts, so it is
                // installed here, before the re-exec that carries both into every thread.
                if (!InstallNoSocketsFilter(out string? filterError))
                {
                    if (mode == ParserSandboxMode.Required)
                        return (false, $"none: the no-network filter could not be installed ({filterError})");
                    gaps.Add($"network not blocked ({filterError})");
                }

                if (Syscall(SysLandlockRestrictSelf, (IntPtr)ruleset, 0, 0) != 0)
                    return (false, $"none: landlock_restrict_self failed (errno {Marshal.GetLastPInvokeError()})");
            }
            finally
            {
                Close(ruleset);
            }

            if (abi < 3)
                gaps.Add("files outside it can still be truncated (ABI 3+)");
            if (abi < 6)
                gaps.Add("signals to other processes are not blocked (ABI 6+)");

            string description = $"landlock (ABI {abi}) and seccomp (no sockets)";
            if (gaps.Count > 0)
                description = $"landlock (ABI {abi}); not covered: {string.Join("; ", gaps)}";
            return (true, description);
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

    /// <summary>
    /// A seccomp filter that fails socket(2) with EACCES for every address family, and io_uring_setup
    /// with it (io_uring can open sockets without the syscall). Calls made through another
    /// architecture's syscall table -- 32-bit or x32 on x86-64 -- are refused outright, since that
    /// is how a filter on one table is otherwise sidestepped.
    /// </summary>
    private static bool InstallNoSocketsFilter(out string? error)
    {
        (uint arch, uint socketNr) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (0xC000003Eu, 41u),
            Architecture.Arm64 => (0xC00000B7u, 198u),
            _ => (0u, 0u),
        };
        if (arch == 0)
        {
            error = $"no filter for {RuntimeInformation.ProcessArchitecture}";
            return false;
        }

        const ushort LoadWord = 0x20, JumpIfEqual = 0x15, JumpIfAtLeast = 0x35, Return = 0x06;
        const uint Allow = 0x7FFF0000, DenyEacces = 0x00050000 | 13;
        const uint IoUringSetup = 425, X32Bit = 0x40000000;

        SockFilter[] program =
        [
            new(LoadWord, 0, 0, 4),                 // 0: architecture
            new(JumpIfEqual, 0, 5, arch),           // 1: another syscall table -> deny
            new(LoadWord, 0, 0, 0),                 // 2: syscall number
            new(JumpIfAtLeast, 3, 0, X32Bit),       // 3: x32 call -> deny
            new(JumpIfEqual, 2, 0, socketNr),       // 4: socket -> deny
            new(JumpIfEqual, 1, 0, IoUringSetup),   // 5: io_uring_setup -> deny
            new(Return, 0, 0, Allow),               // 6
            new(Return, 0, 0, DenyEacces),          // 7
        ];

        var handle = GCHandle.Alloc(program, GCHandleType.Pinned);
        try
        {
            var prog = new SockFprog { Length = (ushort)program.Length, Filter = handle.AddrOfPinnedObject() };
            if (PrctlSeccomp(PrSetSeccomp, SeccompModeFilter, ref prog, 0, 0) != 0)
            {
                error = $"errno {Marshal.GetLastPInvokeError()}";
                return false;
            }
        }
        finally
        {
            handle.Free();
        }

        error = null;
        return true;
    }

    private const int PrSetSeccomp = 22;
    private const ulong SeccompModeFilter = 2;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SockFilter(ushort code, byte jumpIfTrue, byte jumpIfFalse, uint value)
    {
        public readonly ushort Code = code;
        public readonly byte JumpIfTrue = jumpIfTrue;
        public readonly byte JumpIfFalse = jumpIfFalse;
        public readonly uint Value = value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockFprog
    {
        public ushort Length;
        public IntPtr Filter;
    }

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int PrctlSeccomp(int option, ulong mode, ref SockFprog prog, ulong d, ulong e);

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
