using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A clip folder that misses a look-up, the worker's part: the clip worker has no state for a job that waits (a queued job is made,
/// fails or is cancelled), so a queued clip whose folder still can't answer the look-up of its name after the planner's
/// second try ends Failed with the plain reason, never "Unexpected error:" and never a path.
/// </summary>
public sealed partial class ClipFileWorkerTests
{
    [Fact]
    public async Task AQueuedClip_WhoseFolderCantAnswerTheLookUpOfItsName_EndsFailed_WithThePlainReason()
    {
        var service = await StartAsync();
        var bookmark = await AddAsync();
        _planner.Failure = new ClipExtractionException(
            ClipFileOps.FolderUnreachableClipMessage,
            new ClipFolderUnreachableException(ClipFileOps.FolderUnreachableMessage, new IOException("The device is not ready.", unchecked((int)0x80070015))))
        {
            FolderUnreachable = true
        };

        await service.EnqueueAsync(bookmark.Id, bookmark.UserId, BookmarkClipMode.Fast, null, Ct);
        var failed = await WaitForStatusAsync(bookmark.Id, BookmarkClipStatus.Failed);

        Assert.Equal(ClipFileOps.FolderUnreachableClipMessage, failed.Clip!.Error);
        Assert.Equal(0, _runner.Runs);
    }
}
