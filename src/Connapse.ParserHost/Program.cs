using Connapse.Core.Utilities;
using Connapse.Ingestion.Isolation;

namespace Connapse.ParserHost;

/// <summary>
/// Runs Connapse's document parsers in a process of their own (#624). Started and spoken to by
/// ParserProcessPool in the web process over stdin and stdout; not meant to be run by hand. A
/// parser that spins, exhausts its heap or crashes takes this process with it, not the web
/// process, and the pool kills and replaces it.
/// <para>
/// Internal and not a top-level program: Connapse.Web references this assembly so that it ships
/// beside it, and a public <c>Program</c> here would collide with the web app's own.
/// </para>
/// </summary>
internal static class ParserHostProgram
{
    private static async Task Main(string[] args)
    {
        // First, before anything touches Regex: the default match timeout is read once, at type init.
        RegexTimeout.ApplyProcessDefault();

        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();

        // stdout carries the protocol; anything a library prints goes to stderr, which the pool logs.
        Console.SetOut(Console.Error);

        // Started with this argument, the process is the shared inference host instead (#680).
        if (args.Contains(InferenceHostLoop.Argument))
            await InferenceHostLoop.RunAsync(input, output);
        else
            await ParserHostLoop.RunAsync(input, output);
    }
}
