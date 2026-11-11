using System;
using System.IO;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A clip folder that misses one look-up of a new clip's name (a drive spinning up, a share
/// that lost one answer) is asked once more after a pause before the plan ends; the planner runs on a request's thread or
/// under the clip worker's name gate, never inside the store's lock.
/// </summary>
public sealed partial class ClipPlannerTests
{
    [Fact]
    public void Plan_AClipFolderThatMissesOneLookUp_IsAskedOnceMore_AndThePlanIsMade()
    {
        var data = _directory.Combine("data", "bookmarks");
        using var store = ClipFileSupport.StoreWithClipRoots(Path.Combine(data, "bookmarks.json"), Path.Combine(data, "clips"));
        var lookups = 0;
        var failures = 0;
        var fileSystem = new FakeClipFileSystem
        {
            AttributesFailure = path =>
            {
                if (!Path.GetFileName(path).StartsWith("Heat (1995)", StringComparison.Ordinal))
                {
                    return null;
                }

                lookups++;
                return lookups == 1 && ++failures == 1 ? new IOException("The device is not ready.", unchecked((int)0x80070015)) : null;
            }
        };

        var plan = Planner(ClipFileSupport.Ops(store, fileSystem: fileSystem)).Plan(Bookmark(), BookmarkClipMode.Accurate);

        Assert.Equal(1, failures);
        Assert.True(lookups >= 3, "the name asked again, then its .partial");
        Assert.StartsWith("Heat (1995)", Path.GetFileName(plan.OutputPath), StringComparison.Ordinal);
        Assert.DoesNotContain(" (2)", plan.OutputPath, StringComparison.Ordinal);
    }
}
