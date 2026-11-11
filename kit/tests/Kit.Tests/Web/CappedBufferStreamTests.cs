using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Web;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web;

/// <summary>
/// The injection's 1 MB buffer: up to the cap it holds everything; past it, it writes what it holds and passes
/// the rest straight through, in order.
/// </summary>
public sealed class CappedBufferStreamTests
{
    [Fact]
    public async Task UpToTheCap_EverythingIsBuffered()
    {
        using var target = new MemoryStream();
        using var stream = new CappedBufferStream(target, 10);

        await stream.WriteAsync(Bytes(0, 4), TestContext.Current.CancellationToken);
        await stream.WriteAsync(Bytes(4, 6), TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);

        Assert.False(stream.Overflowed);
        Assert.Equal(Bytes(0, 10), stream.ToArray());
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public async Task PastTheCap_TheBufferedBytesAndTheRestGoThroughInOrder()
    {
        using var target = new MemoryStream();
        using var stream = new CappedBufferStream(target, 10);

        await stream.WriteAsync(Bytes(0, 8), TestContext.Current.CancellationToken);
        await stream.WriteAsync(Bytes(8, 5), TestContext.Current.CancellationToken);
        await stream.WriteAsync(Bytes(13, 3), TestContext.Current.CancellationToken);
        await stream.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(stream.Overflowed);
        Assert.Equal(Bytes(0, 16), target.ToArray());
        Assert.Throws<InvalidOperationException>(() => stream.ToArray());
    }

    [Fact]
    public void SynchronousWrites_BehaveTheSame()
    {
        using var target = new MemoryStream();
        using var stream = new CappedBufferStream(target, 10);

        stream.Write(Bytes(0, 10), 0, 10);
        Assert.False(stream.Overflowed);
        stream.Write(Bytes(10, 3), 0, 3);
        stream.Flush();

        Assert.True(stream.Overflowed);
        Assert.Equal(Bytes(0, 13), target.ToArray());
    }

    [Fact]
    public async Task ArrayWriteAsync_UsesTheOffset()
    {
        using var target = new MemoryStream();
        using var stream = new CappedBufferStream(target, 10);
        var data = Bytes(0, 8);

        await stream.WriteAsync(data, 2, 4, TestContext.Current.CancellationToken);

        Assert.Equal(Bytes(2, 4), stream.ToArray());
    }

    [Fact]
    public void ItIsWriteOnly()
    {
        using var stream = new CappedBufferStream(new MemoryStream(), 10);

        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.True(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.Length);
    }

    [Fact]
    public void Dispose_LeavesTheTargetOpen()
    {
        using var target = new MemoryStream();
        var stream = new CappedBufferStream(target, 1);
        stream.Write(Bytes(0, 2), 0, 2);

        stream.Dispose();

        Assert.True(target.CanWrite);
        Assert.Equal(Bytes(0, 2), target.ToArray());
    }

    private static byte[] Bytes(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();
}
