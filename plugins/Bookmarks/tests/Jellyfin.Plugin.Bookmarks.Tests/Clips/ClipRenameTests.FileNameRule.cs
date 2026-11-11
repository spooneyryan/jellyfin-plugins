using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The clip file-name rule of Bookmarks 1.2.0: a clip's file takes its bookmark's
/// name, never its note. A file a bookmark without a name got from its note before 1.2.0 keeps its name (nothing renames it
/// by itself), reads "Name changed", and Rename file gives it the video's name and its range alone.
/// </summary>
public sealed partial class ClipRenameTests
{
    [Fact]
    public async Task AnOlderFileNamedFromItsNote_IsNameChanged_KeptAsItIs_AndRenameFileGivesItTheVideosNameAlone()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Note = "Watch the window\nsecond line", FileName = "Heat (1995) - Watch the window [00.01.00-00.01.30].mp4" }, Ct);
        var old = record.Clip!.FilePath!;

        var listed = Service().GetClips(_alice, new BookmarkClipQueryFilter(), null).Items.Single();
        Assert.True(listed.Clip.IsNameOutdated);
        Assert.True(File.Exists(old));
        Assert.Empty(_fileSystem.Moves);

        var renamed = await Service().RenameFileAsync(_alice, record.Id, null, Ct);

        var expected = Path.Combine(Path.GetDirectoryName(old)!, "Heat (1995) [00.01.00-00.01.30].mp4");
        Assert.Equal(Path.GetFileName(expected), renamed.Clip.FileName);
        Assert.False(renamed.Clip.IsNameOutdated);
        Assert.Equal(expected, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.Equal("Watch the window\nsecond line", _fixture.Store.Get(record.Id)!.Note);
        Assert.True(File.Exists(expected));
        Assert.Equal([(old, expected)], _fileSystem.Moves);
    }

    [Fact]
    public async Task ARenameWithItsFile_OfABookmarkGivenNoName_NamesTheFileAfterTheVideo_NeverTheNote()
    {
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job", Note = "private words" }, Ct);
        var old = record.Clip!.FilePath!;

        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = null, RenameFile = true, IfUpdatedAt = record.UpdatedAt }, Ct);

        Assert.Equal("Heat (1995) [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
        Assert.DoesNotContain("private", _fixture.Store.Get(record.Id)!.Clip!.FilePath!, System.StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(old));
    }
}
