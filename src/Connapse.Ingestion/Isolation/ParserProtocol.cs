using System.Buffers.Binary;
using System.Text.Json;
using Connapse.Core;

namespace Connapse.Ingestion.Isolation;

/// <summary>
/// The wire format between <see cref="ParserProcessPool"/> and a parser host process (#624):
/// length-prefixed frames over the child's stdin and stdout. A host first sends one JSON
/// <see cref="Ready"/> frame once it has started and confined itself (#657). Then a request is two
/// frames, a JSON <see cref="ParseRequest"/> and the file's bytes; the reply is one JSON
/// <see cref="ParseResponse"/>.
/// </summary>
public static class ParserProtocol
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Sent once, before the host reads anything: it has started, confined itself as far as it
    /// could, and is ready for files. A host that dies before sending it failed to start; one that
    /// dies after was killed by what it was given.
    /// </summary>
    public sealed record Ready(string? Sandbox);

    /// <summary>
    /// One file to parse with the parser of the given name, under the given limits. With
    /// <paramref name="SharedInference"/> the PDF models run in the shared inference host (#680): the
    /// host asks for each run with a <see cref="ParseResponse"/> carrying <see cref="ParseResponse.Infer"/>,
    /// then the image's pixels, and reads the inference host's reply before going on.
    /// </summary>
    public sealed record ParseRequest(string Parser, string FileName, UploadSettings Settings, bool SharedInference = false);

    /// <summary>One model run (#680): which model, the threads it may use, and the size of the BGRA pixels that follow.</summary>
    public sealed record InferRequest(string Model, int Width, int Height, int Threads);

    /// <summary>
    /// The parse result, or why there is none: <see cref="PermanentError"/> is a
    /// PermanentIngestionException's message, kept as is; <see cref="Error"/> is any other
    /// exception's; <see cref="OutOfMemory"/> means the heap limit was hit, in which case the
    /// host exits after replying.
    /// </summary>
    public sealed record ParseResponse(
        string? Content,
        Dictionary<string, string>? Metadata,
        List<string>? Warnings,
        string? PermanentError = null,
        string? Error = null,
        bool OutOfMemory = false,
        string? Sandbox = null,
        InferRequest? Infer = null);

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(payload, ct);
    }

    /// <summary>A frame written synchronously, for a parser calling a model mid-parse (#680).</summary>
    public static void WriteFrame(Stream stream, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
    }

    /// <summary>A frame read synchronously; the stream ending inside one, or before it, is an error.</summary>
    public static byte[] ReadFrame(Stream stream, int maxLength)
    {
        Span<byte> length = stackalloc byte[4];
        stream.ReadExactly(length);
        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 0 || size > maxLength)
            throw new InvalidDataException($"A frame of {size:N0} bytes is outside the 0 to {maxLength:N0} allowed.");
        byte[] payload = new byte[size];
        stream.ReadExactly(payload);
        return payload;
    }

    /// <summary>The next frame, or null when the stream ends cleanly before one starts.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxLength, CancellationToken ct)
    {
        byte[] length = new byte[4];
        int read = await stream.ReadAtLeastAsync(length, 4, throwOnEndOfStream: false, ct);
        if (read == 0)
            return null;
        if (read < 4)
            throw new EndOfStreamException("The stream ended inside a frame header.");

        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 0 || size > maxLength)
            throw new InvalidDataException($"A frame of {size:N0} bytes is outside the 0 to {maxLength:N0} allowed.");

        byte[] payload = new byte[size];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    public static Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken ct) =>
        WriteFrameAsync(stream, Serialize(value), ct);

    public static T Deserialize<T>(byte[] frame) =>
        JsonSerializer.Deserialize<T>(frame, Json) ?? throw new InvalidDataException($"An empty {typeof(T).Name} frame.");
}
