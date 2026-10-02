using System.Diagnostics;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Isolation;
using Connapse.Ingestion.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Connapse.Ingestion.Tests.Isolation;

/// <summary>
/// The pool against Connapse.ParserHost.TestHost, whose parsers spin, hoard memory and crash on
/// purpose (#624). Each test starts real processes, so they run as integration tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ParserProcessPoolTests : IDisposable
{
    private static readonly string TestHostPath = Path.Combine(AppContext.BaseDirectory, "Connapse.ParserHost.TestHost.dll");
    private static readonly UploadSettings Settings = new() { ParserMemoryLimitMb = 256, ParserFilesPerProcess = 50 };

    private readonly ParserProcessPool _pool;

    public ParserProcessPoolTests() => _pool = Pool(workers: 4);

    public void Dispose() => _pool.Dispose();

    private static ParserProcessPool Pool(int workers) => new(
        new ConfigurationBuilder().AddInMemoryCollection([new("Hangfire:IngestionWorkerCount", workers.ToString())]).Build(),
        hostPath: TestHostPath);

    private static Task<ParsedDocument> ParseAsync(
        ParserProcessPool pool, string parser, string text = "hello", UploadSettings? settings = null,
        TimeSpan? timeout = null, CancellationToken ct = default) =>
        pool.ParseAsync(new Named(parser), Encoding.UTF8.GetBytes(text), "file.txt", settings ?? Settings,
            timeout ?? TimeSpan.FromSeconds(30), ct);

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task ParseAsync_RealParser_RoundTripsContentMetadataAndWarnings()
    {
        var result = await ParseAsync(_pool, nameof(TextParser), "Line one\nLine two");

        result.Content.Should().Be("Line one\nLine two");
        result.Metadata["FileType"].Should().Be("PlainText");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_ParserThatNeverReturns_IsKilledAtTheDeadline()
    {
        var watch = Stopwatch.StartNew();
        var act = () => ParseAsync(_pool, "Test.Spin", timeout: TimeSpan.FromSeconds(3));

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*[parse_timeout]*");

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
        _pool.HostProcessIds.Should().BeEmpty("the spinning host is killed, not left holding a CPU");
    }

    [Fact]
    public async Task ParseAsync_KilledHost_LeavesNoProcessBehind()
    {
        int pid = int.Parse((await ParseAsync(_pool, "Test.ProcessId")).Content);
        IsRunning(pid).Should().BeTrue("an idle host waits for the next file");

        var act = () => ParseAsync(_pool, "Test.Spin", timeout: TimeSpan.FromSeconds(2));
        await act.Should().ThrowAsync<PermanentIngestionException>();

        IsRunning(pid).Should().BeFalse("the same host took the spinning file and was killed with it");
    }

    [Theory]
    [InlineData("Test.Allocate")]
    [InlineData("Test.AllocateAndSwallow")]
    public async Task ParseAsync_ParserThatExhaustsItsHeap_FailsAsOutOfMemory(string parser)
    {
        var act = () => ParseAsync(_pool, parser, settings: Settings with { ParserMemoryLimitMb = 128 });

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*[parse_out_of_memory]*");
        _pool.HostProcessIds.Should().BeEmpty("a host that ran out of memory is not reused");
    }

    [Fact]
    public async Task ParseAsync_HostThatGrowsNativeMemoryPastItsLimit_IsKilledAsOutOfMemory()
    {
        // Native allocations do not count against the managed heap limit; the watchdog sees the
        // whole process, as the container's memory limit would (#598).
        var act = () => ParseAsync(_pool, "Test.NativeAllocate", settings: Settings with { ParserMemoryLimitMb = 256 });

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*[parse_out_of_memory]*");
        _pool.HostProcessIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Host_DoesNotInheritTheWebProcessesConfiguration()
    {
        // A parse compromised through a native parser must not find connection strings or keys.
        const string name = "ConnectionStrings__ParserHostProbe";
        Environment.SetEnvironmentVariable(name, "Host=db;Password=secret");
        try
        {
            var result = await ParseAsync(_pool, "Test.Environment", name);

            result.Content.Should().Be("(unset)");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task ParseAsync_HostThatCrashes_FailsAsCrashedAndTheNextParseWorks()
    {
        var act = () => ParseAsync(_pool, "Test.Crash");

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*[parse_crashed]*");

        var next = await ParseAsync(_pool, nameof(TextParser), "still working");
        next.Content.Should().Be("still working");
    }

    [Fact]
    public async Task ParseAsync_PermanentFailureFromTheParser_KeepsItsMessage()
    {
        var act = () => ParseAsync(_pool, "Test.Permanent");

        (await act.Should().ThrowAsync<PermanentIngestionException>()).Which.Message.Should().Be("the file is locked [encrypted]");
    }

    [Fact]
    public async Task ParseAsync_ParserException_FailsWithItsMessageAndKeepsTheHost()
    {
        int pid = int.Parse((await ParseAsync(_pool, "Test.ProcessId")).Content);

        var act = () => ParseAsync(_pool, "Test.Throw");
        await act.Should().ThrowAsync<PermanentIngestionException>()
            .WithMessage("Could not parse file.txt: the parser met content it cannot read");

        int after = int.Parse((await ParseAsync(_pool, "Test.ProcessId")).Content);
        after.Should().Be(pid, "a parser's own exception says nothing against the process");
    }

    [Fact]
    public async Task ParseAsync_AfterItsFileQuota_HostIsReplaced()
    {
        var settings = Settings with { ParserFilesPerProcess = 2 };

        int first = int.Parse((await ParseAsync(_pool, "Test.ProcessId", settings: settings)).Content);
        int second = int.Parse((await ParseAsync(_pool, "Test.ProcessId", settings: settings)).Content);
        int third = int.Parse((await ParseAsync(_pool, "Test.ProcessId", settings: settings)).Content);

        second.Should().Be(first);
        third.Should().NotBe(first);
        IsRunning(first).Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_ManyAtOnce_RunNoMoreHostsThanWorkers()
    {
        using var pool = Pool(workers: 2);

        var pids = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ParseAsync(pool, "Test.SlowProcessId")));

        pids.Select(p => p.Content).Distinct().Should().HaveCountLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task ParseAsync_CallerCancels_SurfacesAsCancellationAndKillsTheHost()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var act = () => ParseAsync(_pool, "Test.Spin", ct: cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _pool.HostProcessIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_OutputOverTheLimit_IsRefusedInTheHost()
    {
        // The web process never receives the text: the host refuses it before replying.
        var act = () => ParseAsync(_pool, "Test.Huge", settings: Settings with { MaxExtractedCharacters = 1_000 });

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*100,000 characters*[output_too_large]*");
    }

    [Fact]
    public async Task ParseAsync_WaitForAFreeHost_CountsAgainstTheDeadline()
    {
        using var pool = Pool(workers: 1);
        var stuck = ParseAsync(pool, "Test.Spin", timeout: TimeSpan.FromSeconds(8));
        await Task.Delay(500);

        var watch = Stopwatch.StartNew();
        var act = () => ParseAsync(pool, "Test.ProcessId", timeout: TimeSpan.FromSeconds(1));

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*[parse_timeout]*");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "it fails at its own deadline, not after the stuck parse's");
        await stuck.Invoking(t => t).Should().ThrowAsync<PermanentIngestionException>();
    }

    [Fact]
    public async Task Host_WhoseParentDiesMidParse_ExitsInsteadOfSpinningOn()
    {
        // A stand-in parent: another host, idle on its stdin until it is killed.
        using var parent = Process.Start(ParserProcessPool.StartInfo(TestHostPath, 256))!;
        using var host = Process.Start(ParserProcessPool.StartInfo(TestHostPath, 256, parentProcessId: parent.Id))!;
        host.BeginErrorReadLine();

        Stream input = host.StandardInput.BaseStream;
        await ParserProtocol.WriteJsonAsync(input, new ParserProtocol.ParseRequest("Test.Spin", "file.txt", Settings), CancellationToken.None);
        await ParserProtocol.WriteFrameAsync(input, "spin"u8.ToArray(), CancellationToken.None);
        await input.FlushAsync();
        await Task.Delay(1000);
        host.HasExited.Should().BeFalse("it is spinning on the file");

        parent.Kill(entireProcessTree: true);

        host.WaitForExit(TimeSpan.FromSeconds(10)).Should().BeTrue("it watches its parent and leaves with it");
    }

    [Fact]
    public async Task Dispose_KillsIdleHosts()
    {
        var pool = Pool(workers: 2);
        int pid = int.Parse((await ParseAsync(pool, "Test.ProcessId")).Content);

        pool.Dispose();

        IsRunning(pid).Should().BeFalse();
    }

    [Theory]
    // available, workers -> hosts at once, per-host limit for the default 2,048 MB setting
    [InlineData(64L * 1024, 4, 4, 2048)]
    [InlineData(8L * 1024, 4, 4, 1024)]
    [InlineData(2L * 1024, 4, 2, 512)]
    [InlineData(512L, 4, 1, 512)]
    public void Constructor_SizesHostsToHalfTheAvailableMemory(long availableMb, int workers, int hosts, int perHostMb)
    {
        // Four hosts at 2 GB each would outgrow a small container before any one hit its own cap.
        using var pool = new ParserProcessPool(
            new ConfigurationBuilder().AddInMemoryCollection([new("Hangfire:IngestionWorkerCount", workers.ToString())]).Build(),
            hostPath: TestHostPath,
            availableMemoryBytes: availableMb * 1024 * 1024);

        pool.Slots.Should().Be(hosts);
        pool.MemoryLimitMb(new UploadSettings()).Should().Be(perHostMb);
        pool.MemoryLimitMb(new UploadSettings { ParserMemoryLimitMb = 256 }).Should().Be(256, "a lower setting is kept");
    }

    /// <summary>
    /// The sandbox exists only on Linux (#641); elsewhere these return early. CI runs on Linux, where
    /// a host that is not confined fails the first test rather than letting the rest pass vacuously.
    /// </summary>
    private async Task<bool> ConfinedAsync()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        string sandbox = (await ParseAsync(_pool, "Test.Sandbox")).Content;
        sandbox.Should().StartWith("landlock", "parser hosts confine themselves on Linux");
        return true;
    }

    [Fact]
    public async Task Sandbox_HostOnLinux_IsConfinedByLandlock()
    {
        if (!await ConfinedAsync())
            return;
    }

    [Fact]
    public async Task Sandbox_HostCannotReadSettingsFilesBesideItsAssemblies()
    {
        if (!await ConfinedAsync())
            return;

        // The app folder holds appsettings*.json, and beside them the generated signing key.
        string settings = Path.Combine(AppContext.BaseDirectory, "appsettings.SandboxProbe.json");
        await File.WriteAllTextAsync(settings, """{ "ConnectionStrings": { "Db": "secret" } }""");
        try
        {
            (await ParseAsync(_pool, "Test.ReadFile", settings)).Content.Should().StartWith("denied");
        }
        finally
        {
            File.Delete(settings);
        }
    }

    [Fact]
    public async Task Sandbox_HostCannotReadTheSharedTempFolder()
    {
        if (!await ConfinedAsync())
            return;

        // Where ASP.NET buffers uploads in flight.
        string upload = Path.Combine(Path.GetTempPath(), $"sandbox-probe-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(upload, "another user's upload");
        try
        {
            (await ParseAsync(_pool, "Test.ReadFile", upload)).Content.Should().StartWith("denied");
        }
        finally
        {
            File.Delete(upload);
        }
    }

    [Fact]
    public async Task Sandbox_HostCannotOpenNetworkConnections()
    {
        if (!await ConfinedAsync())
            return;

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            (await ParseAsync(_pool, "Test.Connect", $"127.0.0.1:{port}")).Content.Should().StartWith("denied");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Sandbox_HostStillReadsItsOwnCodeAndModels()
    {
        if (!await ConfinedAsync())
            return;

        string model = Path.Combine(AppContext.BaseDirectory, "models", "v5", "ppocrv5_latin_dict.txt");
        (await ParseAsync(_pool, "Test.ReadFile", model)).Content.Should().StartWith("read");
    }

    [Fact]
    public async Task Sandbox_ConfinedHost_StillOcrsAScan()
    {
        // OCR loads ONNX Runtime, PDFium and Skia after the sandbox is in place: the real test that
        // the allow-list holds everything they need, and that their telemetry is off.
        byte[] scan = TestScanPdf.Build(pages: new TestPdf.Page([new(72, 700, "Quarterly harbour report")]));

        var result = await _pool.ParseAsync(new PdfParser(), scan, "scan.pdf", Settings, TimeSpan.FromSeconds(60), CancellationToken.None);

        result.Content.Should().Contain("harbour report");
    }

    [Fact]
    public async Task Sandbox_RequiredWhereUnavailable_RefusesToParse()
    {
        if (OperatingSystem.IsLinux())
            return;

        var act = () => ParseAsync(_pool, nameof(TextParser), settings: Settings with { ParserSandbox = "Required" });

        await act.Should().ThrowAsync<PermanentIngestionException>().WithMessage("*[sandbox_unavailable]*");
    }

    [Fact]
    public void CanRun_OnlyConnapsesOwnParsersWhenTheHostIsDeployed()
    {
        _pool.CanRun(new TextParser()).Should().BeTrue();
        _pool.CanRun(new Named("Elsewhere")).Should().BeFalse("a parser the host does not carry runs in-process");
        new ParserProcessPool(hostPath: Path.Combine(AppContext.BaseDirectory, "missing.dll"))
            .CanRun(new TextParser()).Should().BeFalse("without a host, parsing falls back to the web process");
    }

    private sealed class Named(string name) : IDocumentParser
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public string Name => name;

        public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Only its name is used: the host does the parsing.");
    }
}
