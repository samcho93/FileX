namespace FileX.Services;

/// <summary>
/// A stream wrapper that reports read progress via a callback.
/// Used to track file upload/download progress.
/// </summary>
public class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onBytesRead;
    private long _totalRead;

    public ProgressStream(Stream inner, Action<long> onBytesRead)
    {
        _inner = inner;
        _onBytesRead = onBytesRead;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _inner.Read(buffer, offset, count);
        if (bytesRead > 0)
        {
            _totalRead += bytesRead;
            _onBytesRead(_totalRead);
        }
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (bytesRead > 0)
        {
            _totalRead += bytesRead;
            _onBytesRead(_totalRead);
        }
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
        if (bytesRead > 0)
        {
            _totalRead += bytesRead;
            _onBytesRead(_totalRead);
        }
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Helper to copy a stream with progress reporting in chunks.
/// </summary>
public static class StreamCopyHelper
{
    public static async Task CopyWithProgressAsync(
        Stream source, Stream destination,
        Action<long> onProgress,
        int bufferSize = 81920,
        CancellationToken ct = default)
    {
        var buffer = new byte[bufferSize];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead, ct);
            totalRead += bytesRead;
            onProgress(totalRead);
        }
    }
}
