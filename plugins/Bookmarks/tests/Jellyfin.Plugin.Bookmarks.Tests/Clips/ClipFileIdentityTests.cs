using System;
using Jellyfin.Plugin.Bookmarks.Clips;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// Found by mutation testing of clip rename: <see cref="ClipFileIdentity.IsSameFile"/> compares the last write
/// time, with and without a file id, so another file of the same size at a path (a clip made under a freed name) is never
/// taken for the renamed one.
/// </summary>
public sealed class ClipFileIdentityTests
{
    private static readonly DateTime Written = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsSameFile_ComparesTheLastWriteTime_WithAndWithoutAFileId()
    {
        var plain = new ClipFileIdentity(1000, Written);
        var withId = plain with { Volume = 7, FileId = "00000000000000000000000000000042" };

        Assert.True(ClipFileIdentity.IsSameFile(plain, plain with { }));
        Assert.True(ClipFileIdentity.IsSameFile(withId, withId with { }));
        Assert.False(ClipFileIdentity.IsSameFile(plain, plain with { WrittenUtc = Written.AddTicks(1) }));
        Assert.False(ClipFileIdentity.IsSameFile(withId, withId with { WrittenUtc = Written.AddSeconds(1) }));
        Assert.False(ClipFileIdentity.IsSameFile(withId, plain with { WrittenUtc = Written.AddSeconds(1) }));
        Assert.False(ClipFileIdentity.IsSameFile(plain, plain with { Length = 1001 }));
        Assert.False(ClipFileIdentity.IsSameFile(withId, withId with { FileId = "00000000000000000000000000000041" }));
        Assert.True(ClipFileIdentity.IsSameFile(withId, plain));
        Assert.False(ClipFileIdentity.IsSameFile(null, plain));
    }
}
