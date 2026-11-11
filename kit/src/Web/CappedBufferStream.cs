using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// Buffers a response body up to a cap. Past the cap it writes what it holds to the real body and
/// passes everything else through, so an unexpectedly large response is served untransformed instead of being held in
/// memory.
/// </summary>
internal sealed class CappedBufferStream : Stream
{
    // Not owned: it is the response body, which the server disposes.
#pragma warning disable CA2213
    private readonly Stream _passThrough;
#pragma warning restore CA2213
    private readonly int _capacity;
    private MemoryStream? _buffer = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CappedBufferStream"/> class.
    /// </summary>
    /// <param name="passThrough">The real response body, used once the cap is exceeded.</param>
    /// <param name="capacity">The most bytes to buffer.</param>
    public CappedBufferStream(Stream passThrough, int capacity)
    {
        ArgumentNullException.ThrowIfNull(passThrough);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _passThrough = passThrough;
        _capacity = capacity;
    }

    /// <summary>
    /// Gets a value indicating whether the cap was exceeded and the body went straight to the client.
    /// </summary>
    public bool Overflowed => _buffer is null;

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Gets the buffered bytes.
    /// </summary>
    /// <returns>The bytes written so far.</returns>
    /// <exception cref="InvalidOperationException">The cap was exceeded.</exception>
    public byte[] ToArray() => _buffer?.ToArray() ?? throw new InvalidOperationException("The buffer overflowed.");

    /// <inheritdoc />
    public override void Flush()
    {
        if (_buffer is null)
        {
            _passThrough.Flush();
        }
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
        => _buffer is null ? _passThrough.FlushAsync(cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (_buffer is not null && _buffer.Length + count <= _capacity)
        {
            _buffer.Write(buffer, offset, count);
            return;
        }

        SpillSync();
        _passThrough.Write(buffer, offset, count);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_buffer is not null && _buffer.Length + buffer.Length <= _capacity)
        {
            _buffer.Write(buffer.Span);
            return;
        }

        if (_buffer is not null)
        {
            var pending = _buffer;
            _buffer = null;
            await _passThrough.WriteAsync(pending.GetBuffer().AsMemory(0, (int)pending.Length), cancellationToken).ConfigureAwait(false);
            await pending.DisposeAsync().ConfigureAwait(false);
        }

        await _passThrough.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer?.Dispose();
            _buffer = null;
        }

        base.Dispose(disposing);
    }

    private void SpillSync()
    {
        if (_buffer is null)
        {
            return;
        }

        var pending = _buffer;
        _buffer = null;
        _passThrough.Write(pending.GetBuffer(), 0, (int)pending.Length);
        pending.Dispose();
    }
}
