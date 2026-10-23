using System.IO.Compression;
using Microsoft.AspNetCore.Http.Metadata;

namespace Jiten.Api.Middleware;

/// <summary>
/// Opts an endpoint into gzip request bodies. Inflation is per-endpoint because a small compressed body inflates to
/// the full size limit, so anonymous or lightly limited endpoints must keep rejecting it. The endpoint must also
/// declare [RequestSizeLimit], which caps the inflated size; without one the body is not inflated.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AcceptsCompressedBodyAttribute : Attribute;

public static class CompressedBodyApplicationBuilderExtensions
{
    /// <summary>Must run after UseRouting, so the endpoint and its request size limit are known before inflating.</summary>
    public static IApplicationBuilder UseRequestDecompressionForOptedInEndpoints(this IApplicationBuilder app)
        => app.UseWhen(IsGzipBodyForOptedInEndpoint, branch => branch.Use(InflateGzipBody));

    private static bool IsGzipBodyForOptedInEndpoint(HttpContext context)
        => string.Equals(context.Request.Headers.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase)
           && InflatedSizeLimit(context) != null;

    private static long? InflatedSizeLimit(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        return metadata?.GetMetadata<AcceptsCompressedBodyAttribute>() == null
            ? null
            : metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
    }

    private static async Task InflateGzipBody(HttpContext context, RequestDelegate next)
    {
        var compressed = context.Request.Body;
        await using var inflated = new SizeCappedReadStream(
            new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true), InflatedSizeLimit(context)!.Value);

        context.Request.Body = inflated;
        context.Request.Headers.ContentEncoding = default;
        context.Request.Headers.ContentLength = default;

        try
        {
            await next(context);
        }
        catch (Exception) when (inflated.LimitExceeded && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        catch (InvalidDataException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        finally
        {
            context.Request.Body = compressed;
        }
    }

    private sealed class SizeCappedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public bool LimitExceeded { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int bytesRead)
        {
            _read += bytesRead;
            if (_read <= maxBytes)
                return bytesRead;

            LimitExceeded = true;
            throw new BadHttpRequestException("Inflated request body exceeds the size limit.", StatusCodes.Status413PayloadTooLarge);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
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

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
