using Connapse.Core.Interfaces;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>
/// A read-only stream that refuses to hand out more than <paramref name="cap"/> bytes. Each read
/// asks the inner stream for at most one byte past the cap, so a caller copying it into memory
/// never receives more than the cap before the failure. The failure is permanent: the same file
/// will be just as large on a retry.
/// </summary>
internal sealed class SizeCappedStream(Stream inner, long cap, string tooLargeMessage) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        Count(inner.Read(buffer, offset, Allowance(count)));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        Count(await inner.ReadAsync(buffer[..Allowance(buffer.Length)], ct));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    /// <summary>At most what is left under the cap, plus one byte so going over is noticed.</summary>
    private int Allowance(int requested) => (int)Math.Min(requested, cap - _read + 1);

    private int Count(int read)
    {
        _read += read;
        if (_read > cap)
            throw new PermanentIngestionException(tooLargeMessage);
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
