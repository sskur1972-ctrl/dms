using Microsoft.Extensions.Caching.Distributed;

namespace DMS.Web.Services;

// Large immutable files are cached in bounded chunks, rather than one large Redis value.
// Authorization must happen before opening this stream, including on cache hits.
internal sealed class CachedFileReadStream(Stream source, IDistributedCache cache, string key,
    ILogger logger) : Stream
{
    private const int ChunkSize = 8 * 1024 * 1024;
    private long _position;
    private long _chunkIndex = -1;
    private byte[]? _chunk;
    private bool _cacheAvailable = true;
    private bool _disposed;
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed && source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (buffer.Length == 0 || _position >= Length) return 0;
        var index = _position / ChunkSize;
        if (_chunkIndex != index)
        {
            var count = (int)Math.Min(ChunkSize, Length - index * ChunkSize);
            var chunkKey = key + ":chunk:" + ChunkSize + ":" + index;
            byte[]? bytes = null;
            try { if (_cacheAvailable) bytes = await cache.GetAsync(chunkKey, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { CacheFailed(ex); }
            if (bytes?.Length != count)
            {
                bytes = new byte[count];
                source.Seek(index * ChunkSize, SeekOrigin.Begin);
                await source.ReadExactlyAsync(bytes, ct);
                try
                {
                    if (_cacheAvailable) await cache.SetAsync(chunkKey, bytes, new DistributedCacheEntryOptions
                    { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { CacheFailed(ex); }
            }
            _chunk = bytes;
            _chunkIndex = index;
        }
        var offset = (int)(_position % ChunkSize);
        var copied = Math.Min(buffer.Length, _chunk!.Length - offset);
        _chunk.AsMemory(offset, copied).CopyTo(buffer);
        _position += copied;
        return copied;
    }

    private void CacheFailed(Exception ex)
    {
        _cacheAvailable = false;
        logger.LogWarning("Document chunk cache unavailable ({ErrorType}); reading durable storage.", ex.GetType().Name);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override int Read(Span<byte> buffer)
    {
        var bytes = new byte[Math.Min(buffer.Length, ChunkSize)];
        var count = Read(bytes, 0, bytes.Length);
        bytes.AsSpan(0, count).CopyTo(buffer);
        return count;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0) throw new IOException("Cannot seek before the start of the file.");
        return _position = position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { source.Dispose(); _chunk = null; _disposed = true; }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed) { await source.DisposeAsync(); _chunk = null; _disposed = true; }
        GC.SuppressFinalize(this);
    }
}
