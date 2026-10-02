using System.Diagnostics;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Connapse.Ingestion.Isolation.ParserProtocol;

namespace Connapse.Ingestion.Isolation;

/// <summary>
/// The parser host's side of <see cref="ParserProtocol"/> (#624): reads requests from the parent
/// until its stdin closes, parses each with the built-in parsers, and replies. Lives here rather
/// than in Connapse.ParserHost so a test host can serve the same loop with parsers of its own.
/// </summary>
public static class ParserHostLoop
{
    /// <summary>Requests carry the whole file; anything over this is not one the parent would send.</summary>
    private const int MaxRequestFrame = int.MaxValue;

    /// <summary>Warnings kept in a reply, and the length each is cut to: the pipeline stores 4,000 characters of them.</summary>
    private const int MaxWarnings = 200;
    private const int MaxWarningLength = 2000;

    public static async Task RunAsync(Stream input, Stream output, IReadOnlyList<IDocumentParser>? extraParsers = null)
    {
        WatchParent();

        // An OutOfMemoryException is usually caught inside a parser and turned into a warning, so
        // the reply would look like an empty document. Seeing it first-chance keeps it a memory
        // failure, which the parent reports as one.
        bool outOfMemory = false;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is OutOfMemoryException)
                outOfMemory = true;
        };

        while (true)
        {
            byte[]? header = await ReadFrameAsync(input, MaxRequestFrame, CancellationToken.None);
            if (header is null)
                return;
            byte[] content = await ReadFrameAsync(input, MaxRequestFrame, CancellationToken.None)
                ?? throw new EndOfStreamException("A request header arrived without its content.");

            var request = Deserialize<ParseRequest>(header);
            outOfMemory = false;
            ParseResponse response;
            try
            {
                response = await ParseAsync(request, content, extraParsers);
            }
            catch (OutOfMemoryException)
            {
                response = new ParseResponse(null, null, null, OutOfMemory: true);
            }

            if (outOfMemory)
                response = new ParseResponse(null, null, null, OutOfMemory: true);

            content = [];
            await WriteJsonAsync(output, response, CancellationToken.None);
            await output.FlushAsync();

            // After running out, the heap is not one to trust with the next file.
            if (response.OutOfMemory)
                return;
        }
    }

    private static async Task<ParseResponse> ParseAsync(ParseRequest request, byte[] content, IReadOnlyList<IDocumentParser>? extraParsers)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<UploadSettings>>(new FixedOptionsMonitor<UploadSettings>(request.Settings));
        services.AddDocumentParsers();
        foreach (var parser in extraParsers ?? [])
            services.AddSingleton<IDocumentParser>(parser);
        using var provider = services.BuildServiceProvider();

        var chosen = provider.GetServices<IDocumentParser>().FirstOrDefault(p => p.Name == request.Parser);
        if (chosen is null)
            return new ParseResponse(null, null, null, Error: $"The parser host has no parser named {request.Parser}.");

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var parsed = await chosen.ParseAsync(stream, request.FileName, CancellationToken.None);

            // Refused here, as the pipeline would refuse it, so the text never crosses to the web
            // process: a reply that size would cost it the memory this process exists to spend.
            int limit = request.Settings.MaxExtractedCharacters;
            if (parsed.Content.Length > limit)
            {
                return new ParseResponse(null, null, null, PermanentError:
                    $"Could not parse {Path.GetFileName(request.FileName)}: it yielded {parsed.Content.Length:N0} characters, " +
                    $"over the {limit:N0} limit [output_too_large]");
            }

            var warnings = parsed.Warnings.Take(MaxWarnings)
                .Select(w => w.Length > MaxWarningLength ? w[..MaxWarningLength] : w)
                .ToList();
            if (parsed.Warnings.Count > MaxWarnings)
                warnings.Add($"… ({parsed.Warnings.Count} warnings in total)");
            return new ParseResponse(parsed.Content, parsed.Metadata, warnings);
        }
        catch (PermanentIngestionException ex)
        {
            return new ParseResponse(null, null, null, PermanentError: ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ParseResponse(null, null, null, Error: ex.Message);
        }
    }

    /// <summary>
    /// Ends this process when its parent does. Between requests a closed stdin ends the loop, but a
    /// parser spinning on a file never reads stdin again, so without this a host whose web process
    /// crashed would spin on alone.
    /// </summary>
    private static void WatchParent()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(ParserProcessPool.ParentProcessIdVariable), out int parentId))
            return;

        Process parent;
        try
        {
            parent = Process.GetProcessById(parentId);
        }
        catch (ArgumentException)
        {
            Environment.Exit(2); // already gone
            return;
        }

        var watcher = new Thread(() =>
        {
            parent.WaitForExit();
            Environment.Exit(2);
        })
        {
            IsBackground = true,
            Name = "ParentWatch",
        };
        watcher.Start();
    }

    /// <summary>Settings fixed for one request: the host does not watch the parent's configuration.</summary>
    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
