using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A clip's rename, guarded since 1.2.0: a clip file another record names too (two records
/// left on one file by a version before 1.1.3) is never moved away from it, and a clip folder that can't answer a look-up of
/// a new name ends the rename at the first one.
/// </summary>
public sealed partial class ClipRenameTests
{
    [Fact]
    public async Task ARenameOfAFileAnotherRecordNamesToo_Is409_AndNothingMoves_NeitherRecordChanges()
    {
        // As 1.1.2 could leave them: Alice's clip and a record of Bob's (here a kept clip) naming one file. Moving it would leave
        // Bob's record "File missing", and its Forget would then forget a clip that is there under another name.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var file = record.Clip!.FilePath!;
        var other = _fixture.Make(_bob.Id, new ClipSpec { Name = "Bank job", Tombstone = true, OnDisk = false }) with { Clip = record.Clip };
        await _fixture.Store.AddAsync(other, Ct);
        var storeFile = _fixture.Directory.Combine("data", "bookmarks.json");
        var before = await File.ReadAllBytesAsync(storeFile, Ct);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct));

        Assert.Equal((PluginErrorKind.Conflict, ClipFileOps.NamedByAnotherMessage), (error.Kind, error.Message));
        Assert.Empty(_fileSystem.Moves);
        Assert.True(File.Exists(file));
        Assert.Equal(before, await File.ReadAllBytesAsync(storeFile, Ct));
        Assert.Empty(JournalFiles());
        Assert.Equal("Bank job", _fixture.Store.Get(record.Id)!.Name);

        // The name alone touches no file, so it is renamed; Rename file then is refused the same way.
        var named = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = false }, Ct);
        Assert.Equal("Vault", named.Name);
        var fileOnly = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameFileAsync(_alice, record.Id, null, Ct));
        Assert.Equal((PluginErrorKind.Conflict, ClipFileOps.NamedByAnotherMessage), (fileOnly.Kind, fileOnly.Message));
        Assert.Empty(_fileSystem.Moves);
        Assert.Equal(file, _fixture.Store.Get(record.Id)!.Clip!.FilePath);
        Assert.Equal(file, _fixture.Store.Get(other.Id)!.Clip!.FilePath);

        // Once the other record lets go of the file (here its record removed), the rename moves it.
        await _fixture.Store.RemoveAsync(other.Id, Ct);
        var renamed = await Service().RenameFileAsync(_alice, record.Id, null, Ct);
        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task AClipFolderThatCantAnswerALookUpOfTheNewName_Is503_AtTheFirstName_AndNothingIsRenamed()
    {
        // Found after Bookmarks 1.1.3: a drive that isn't ready (or a share that doesn't answer) used to
        // count every name as taken, so the rename asked about up to a thousand names, each as slow as the folder, under the
        // store's lock and the name gate.
        var record = await _fixture.AddAsync(_alice.Id, new ClipSpec { Name = "Bank job" }, Ct);
        var file = record.Clip!.FilePath!;
        var lookups = 0;
        _fileSystem.AttributesFailure = path =>
        {
            if (!Path.GetFileName(path).StartsWith("Heat (1995) - Vault", StringComparison.Ordinal))
            {
                return null;
            }

            lookups++;
            return new IOException("The device is not ready.", unchecked((int)0x80070015));
        };
        var storeFile = _fixture.Directory.Combine("data", "bookmarks.json");
        var before = await File.ReadAllBytesAsync(storeFile, Ct);

        var error = await Assert.ThrowsAnyAsync<PluginException>(() => Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct));

        Assert.Equal((PluginErrorKind.Unavailable, ClipFileOps.FolderUnreachableMessage), (error.Kind, error.Message));

        // Since 1.2.0 the attempt is made once more after a pause, outside the store's lock: two look-ups.
        Assert.Equal(2, lookups);
        Assert.Empty(_fileSystem.Moves);
        Assert.True(File.Exists(file));
        Assert.Equal(before, await File.ReadAllBytesAsync(storeFile, Ct));
        Assert.Empty(JournalFiles());
        Assert.Equal("Bank job", _fixture.Store.Get(record.Id)!.Name);

        // The name gate was given back: once the folder answers, the rename is made.
        _fileSystem.AttributesFailure = null;
        var renamed = await Service().RenameAsync(_alice, record.Id, new BookmarkClipRenameRequest { Name = "Vault", RenameFile = true }, Ct);
        Assert.Equal("Heat (1995) - Vault [00.01.00-00.01.30].mp4", renamed.Clip.FileName);
        Assert.Equal(1, Files.NameGate.CurrentCount);
        Assert.Equal(BookmarkClipState.Ready, renamed.Clip.State);
        Assert.Single(_fileSystem.Moves, m => m.Source == file);
    }
}
