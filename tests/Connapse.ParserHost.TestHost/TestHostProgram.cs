using Connapse.Core.Interfaces;
using Connapse.Ingestion.Isolation;

namespace Connapse.ParserHost.TestHost;

/// <summary>
/// The parser host loop with parsers that misbehave on purpose, for ParserProcessPoolTests (#624).
/// A separate executable so the shipping host carries no test parsers.
/// </summary>
internal static class TestHostProgram
{
    private static async Task Main()
    {
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
