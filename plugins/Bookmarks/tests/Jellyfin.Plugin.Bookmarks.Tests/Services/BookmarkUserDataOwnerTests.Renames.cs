using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// <c>DeletedUserPolicy=Delete</c> forgets the clip renames' history of the deleted user's
/// records (its paths can start with a note's words); the records other users have keep theirs.
/// </summary>
public sealed partial class BookmarkUserDataOwnerTests
{
    [Fact]
    public async Task TheDeletePolicy_ForgetsTheRenameHistoryOfTheDeletedUsersRecords()
    {
        _policy = DeletedUserPolicy.Delete;
        Create(_alice, _bob);
        var clipped = await AddAsync(Bookmark(_bob) with { Clip = Clip("clipped.mkv") });
        var plain = await AddAsync(Bookmark(_bob));
        var alices = await AddAsync(Bookmark(_alice) with { Clip = Clip("alice.mkv") });
        var history = new ClipRenameHistory(Path.GetDirectoryName(StoreFile)!, PhysicalClipFileSystem.Instance, NullLogger.Instance);
        foreach (var id in new[] { clipped.Id, plain.Id, alices.Id })
        {
            // Paths no file of theirs has now (an earlier name of each), so only the policy's forgetting removes them.
            history.Add(new ClipRenameEntry { BookmarkId = id, From = Path.Combine(_root, id.ToString("N") + " - a note's words [00.01.00-00.01.30].mkv"), To = Path.Combine(_root, id.ToString("N") + " - renamed [00.01.00-00.01.30].mkv") }, DateTime.UtcNow);
        }

        _server.Remove(_bob);
        await DeleteUserAsync(_bob);

        Assert.Equal([alices.Id], _store.GetAll().Select(b => b.Id));
        Assert.Equal([alices.Id], new ClipRenameHistory(Path.GetDirectoryName(StoreFile)!, PhysicalClipFileSystem.Instance, NullLogger.Instance).Entries.Select(e => e.BookmarkId));
    }
}
