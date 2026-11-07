using System.Buffers;
using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Gives an upload endpoint a body limit of its own, read from <see cref="UploadSettings.MaxFileBytes"/> on each
/// request so an admin's change needs no restart. It runs after authorization, so only a caller allowed to upload
/// gets more than the server's default, and before the endpoint, because binding the form reads the body.
/// </summary>
public static class UploadBodyLimit
{
    /// <summary>The most of a refused body read away; past this the client meets a closed connection.</summary>
    public const long MaxDrainBytes = 1024 * 1024 * 1024;

    /// <summary>
    /// How many refused bodies are read away at once. Past this a refusal is still sent, and Kestrel gives the rest of
    /// the body only its own few seconds, so a caller repeating refused uploads can't hold connections open.
    /// </summary>
    public const int MaxConcurrentDrains = 8;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(1);

    private sealed record Marker;

    /// <summary>Marks an endpoint as taking files, up to the upload limit.</summary>
    public static RouteHandlerBuilder WithUploadBodyLimit(this RouteHandlerBuilder builder) =>
        // The body limit alone decides: multipart's own limit, per part, would otherwise refuse a file past 128 MiB.
        builder.WithFormOptions(multipartBodyLengthLimit: long.MaxValue)
            .WithMetadata(new Marker());

    public static IApplicationBuilder UseUploadBodyLimit(this IApplicationBuilder app) => app.UseMiddleware<Middleware>();

    private sealed class Middleware(RequestDelegate next, IOptionsMonitor<UploadSettings> settings)
    {
        private readonly SemaphoreSlim _drains = new(MaxConcurrentDrains);

        public async Task InvokeAsync(HttpContext context)
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<Marker>() is null)
            {
                await next(context);
                return;
            }

            long maxFileBytes = settings.CurrentValue.MaxFileBytes;
            long limit = UploadLimits.RequestBytes(maxFileBytes);
            IHttpMaxRequestBodySizeFeature? body = context.Features.Get<IHttpMaxRequestBodySizeFeature>();

            if (context.Request.ContentLength is { } length)
            {
                if (length > limit)
                {
                    SetBodyLimit(body, Math.Min(length, MaxDrainBytes));
                    await RefuseAsync(context, UploadLimits.RequestTooLarge(length, maxFileBytes));
                    return;
                }

                SetBodyLimit(body, limit);
                await next(context);
                return;
            }

            // A body of no stated length can only be refused partway through. Past its own limit Kestrel can't be read
            // from, so the drain below would fail, and the limit is held here instead, leaving Kestrel room for the drain.
            SetBodyLimit(body, limit > long.MaxValue - MaxDrainBytes ? long.MaxValue : limit + MaxDrainBytes);
            Stream original = context.Request.Body;
            CappedBody capped = new(original, limit);
            context.Request.Body = capped;
            try
            {
                await next(context);
            }
            catch (IOException) when (capped.Exceeded && !context.Response.HasStarted)
            {
                // Refused below. Form binding usually turns this into a bare 413 instead of letting it through.
            }

            context.Request.Body = original;
            if (capped.Exceeded && !context.Response.HasStarted)
                await RefuseAsync(context, UploadLimits.RequestOverLimit(maxFileBytes));
        }

        /// <summary>
        /// Answers first, then reads the rest of the body away. Closing a connection with a body still arriving resets
        /// it, and the client then reports a network failure and never sees the answer.
        /// </summary>
        private async Task RefuseAsync(HttpContext context, string detail)
        {
            await FileProblems.TooLarge(detail).ExecuteAsync(context);
            await context.Response.CompleteAsync();

            if (!_drains.Wait(0))
                return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(DrainTimeout);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (await context.Request.Body.ReadAsync(buffer, timeout.Token) > 0)
                {
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // The client stopped sending, or used up the drain's time or bytes.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                _drains.Release();
            }
        }
    }

    private static void SetBodyLimit(IHttpMaxRequestBodySizeFeature? body, long bytes)
    {
        if (body is { IsReadOnly: false })
            body.MaxRequestBodySize = bytes;
    }

    /// <summary>Reads through to a body of no stated length and stops at the limit.</summary>
    private sealed class CappedBody(Stream inner, long limit) : Stream
    {
        private long _read;

        public bool Exceeded { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        private int Count(int read)
        {
            _read += read;
            if (_read <= limit)
                return read;

            Exceeded = true;
            throw new BadHttpRequestException("The request body is over the upload limit.", StatusCodes.Status413PayloadTooLarge);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
