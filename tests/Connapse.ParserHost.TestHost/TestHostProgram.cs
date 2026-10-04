using Connapse.Core.Interfaces;
using Connapse.Ingestion.Isolation;

namespace Connapse.ParserHost.TestHost;

/// <summary>
/// The parser host loop with parsers that misbehave on purpose, for ParserProcessPoolTests (#624).
/// A separate executable so the shipping host carries no test parsers.
/// </summary>
internal static class TestHostProgram
{
    /// <summary>Set by ParserProcessPoolTests on a host that should fail to start (#657).</summary>
    internal const string FailToStartVariable = "CONNAPSE_TEST_FAIL_TO_START";

    private static async Task Main()
    {
        // Ends before the loop says it is ready, as a host that cannot confine itself or load the
        // runtime would.
        if (Environment.GetEnvironmentVariable(FailToStartVariable) == "1")
            Environment.Exit(3);

        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        await ParserHostLoop.RunAsync(input, output,
        [
            new Behaving("Test.Spin", _ =>
            {
                // Ignores every token, like PdfPig in a malformed content stream.
                while (true)
                    Thread.SpinWait(1000);
            }),
            new Behaving("Test.Allocate", _ =>
            {
                var hoard = new List<byte[]>();
                while (true)
                    hoard.Add(new byte[16 * 1024 * 1024]);
            }),
            new Behaving("Test.AllocateAndSwallow", _ =>
            {
                // Parsers catch Exception and turn it into a warning; the host must still see it.
                try
                {
                    var hoard = new List<byte[]>();
                    while (true)
                        hoard.Add(new byte[16 * 1024 * 1024]);
                }
                catch (Exception ex)
                {
                    return new ParsedDocument(string.Empty, [], [$"Error parsing: {ex.Message}"]);
                }
            }),
            new Behaving("Test.Crash", _ =>
            {
                Environment.FailFast("Test.Crash takes the process down.");
                return null!;
            }),
            new Behaving("Test.Permanent", _ => throw new PermanentIngestionException("the file is locked [encrypted]")),
            new Behaving("Test.Throw", _ => throw new InvalidOperationException("the parser met content it cannot read")),
            new Behaving("Test.ProcessId", content => new ParsedDocument(
                Environment.ProcessId.ToString(), new() { ["Bytes"] = content.Length.ToString() }, [])),
            new Behaving("Test.NativeAllocate", _ =>
            {
                // Native memory, which the managed heap limit does not see: only the watchdog does.
                var blocks = new List<IntPtr>();
                while (true)
                {
                    const int size = 16 * 1024 * 1024;
                    IntPtr block = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                    unsafe { new Span<byte>((void*)block, size).Fill(1); }
                    blocks.Add(block);
                    Thread.Sleep(20);
                }
            }),
            new Behaving("Test.Environment", content => new ParsedDocument(
                Environment.GetEnvironmentVariable(System.Text.Encoding.UTF8.GetString(content)) ?? "(unset)", [], [])),
            new Behaving("Test.Sandbox", _ => new ParsedDocument(ParserSandbox.Current ?? "(not applied)", [], [])),
            new Behaving("Test.ReadFile", content =>
            {
                string path = System.Text.Encoding.UTF8.GetString(content);
                try
                {
                    return new ParsedDocument($"read {File.ReadAllBytes(path).Length} bytes", [], []);
                }
                catch (Exception ex)
                {
                    return new ParsedDocument($"denied: {ex.GetType().Name}", [], []);
                }
            }),
            new Behaving("Test.Connect", content =>
            {
                string[] target = System.Text.Encoding.UTF8.GetString(content).Split(':');
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    client.Connect(target[0], int.Parse(target[1]));
                    return new ParsedDocument("connected", [], []);
                }
                catch (Exception ex)
                {
                    return new ParsedDocument($"denied: {ex.GetType().Name}", [], []);
                }
            }),
            new Behaving("Test.Udp", content =>
            {
                string[] target = System.Text.Encoding.UTF8.GetString(content).Split(':');
                try
                {
                    using var client = new System.Net.Sockets.UdpClient();
                    client.Send("exfiltrated"u8.ToArray(), target[0], int.Parse(target[1]));
                    return new ParsedDocument("sent", [], []);
                }
                catch (Exception ex)
                {
                    return new ParsedDocument($"denied: {ex.GetType().Name}", [], []);
                }
            }),
            new Behaving("Test.Huge", _ => new ParsedDocument(new string('x', 100_000), [], [])),
            new Behaving("Test.SlowProcessId", _ =>
            {
                Thread.Sleep(500);
                return new ParsedDocument(Environment.ProcessId.ToString(), [], []);
            }),
        ]);
    }

    private sealed class Behaving(string name, Func<byte[], ParsedDocument> parse) : IDocumentParser
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public string Name => name;

        public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return Task.FromResult(parse(copy.ToArray()));
        }
    }
}
