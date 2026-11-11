using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// A clip folder's marker file (<see cref="ClipRootMarker"/>): the <c>RootId</c> it names, its text,
/// and how the real file system reads and writes it (never through a folder or link at its name, never an oversized file,
/// replaced whole, nothing left behind by a write that fails), <see cref="ClipFileOps.CheckRoot(string)"/>'s answers, and the token each
/// writing gives the marker, recorded in the store: an older copy of the folder is another
/// folder, a marker without a token is adopted once.
/// </summary>
public sealed class ClipRootMarkerTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheRootId_IsTwelveHexCharacters_OfTheNormalisedPath_TheSameForEverySpellingOfOneFolder()
    {
        var id = ClipRootPaths.RootId(@"E:\Jellyfin Clips");

        Assert.Matches("^[0-9a-f]{12}$", id);
        Assert.Equal(id, ClipRootPaths.RootId(@"E:\Jellyfin Clips"));
        Assert.NotEqual(id, ClipRootPaths.RootId(@"E:\Jellyfin Clips2"));
        if (OperatingSystem.IsWindows())
        {
            // Windows compares paths without regard to case, and so does the id.
            Assert.Equal(id, ClipRootPaths.RootId(@"e:\jellyfin clips"));
        }

        Assert.Throws<ArgumentException>(() => ClipRootPaths.RootId(string.Empty));
    }

    [Fact]
    public void TheText_NamesItsFolderOnly_AndExplainsItself()
    {
        var root = _directory.Combine("clips");
        var text = ClipRootMarker.Text(root);

        Assert.StartsWith("Jellyfin Bookmarks plugin: this folder holds clip files. Keep this file here", text, StringComparison.Ordinal);
        Assert.True(ClipRootMarker.Names(text, root));
        Assert.True(ClipRootMarker.Names(text.Replace("\n", "\r\n", StringComparison.Ordinal), root));
        Assert.False(ClipRootMarker.Names(text, _directory.Combine("other")));
        Assert.False(ClipRootMarker.Names(null, root));
        Assert.False(ClipRootMarker.Names(string.Empty, root));
        Assert.False(ClipRootMarker.Names("RootId=", root));
        Assert.True(ClipRootMarker.Names("anything\nRootId=" + ClipRootPaths.RootId(root).ToUpperInvariant(), root));
    }

    [Fact]
    public void TheRealFileSystem_WritesTheMarkerWhole_AndReadsItBack_ReplacingOneOfAnotherFolder()
    {
        var root = _directory.Combine("clips");
        Directory.CreateDirectory(root);
        var fileSystem = PhysicalClipFileSystem.Instance;
        Assert.Null(fileSystem.ReadRootMarker(root));

        fileSystem.WriteRootMarker(root, ClipRootMarker.Text(_directory.Combine("elsewhere")));
        Assert.False(ClipRootMarker.Names(fileSystem.ReadRootMarker(root), root));
        fileSystem.WriteRootMarker(root, ClipRootMarker.Text(root));

        Assert.True(ClipRootMarker.Names(fileSystem.ReadRootMarker(root), root));
        Assert.Equal([ClipRootMarker.PathIn(root)], Directory.GetFiles(root));
        Assert.Null(fileSystem.ReadRootMarker(_directory.Combine("not there")));
    }

    [Fact]
    public void WhatIsntAMarkerFile_IsReadAsNone_AndAWriteThatFails_LeavesNothingBehind()
    {
        var fileSystem = PhysicalClipFileSystem.Instance;

        // A folder at the marker's name (a link to one too: never followed), and a file too large to be one.
        var withFolder = _directory.Combine("with folder");
        Directory.CreateDirectory(ClipRootMarker.PathIn(withFolder));
        Assert.Equal(string.Empty, fileSystem.ReadRootMarker(withFolder));
        var error = Record.Exception(() => fileSystem.WriteRootMarker(withFolder, ClipRootMarker.Text(withFolder)));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal([ClipRootMarker.PathIn(withFolder)], Directory.GetFileSystemEntries(withFolder));

        var withLarge = _directory.Combine("with large");
        Directory.CreateDirectory(withLarge);
        File.WriteAllText(ClipRootMarker.PathIn(withLarge), new string('x', ClipRootMarker.MaxBytes) + "\nRootId=" + ClipRootPaths.RootId(withLarge) + "\n");
        Assert.Equal(string.Empty, fileSystem.ReadRootMarker(withLarge));

        // A folder that isn't there gets no marker, and no folder.
        var missing = _directory.Combine("missing");
        Assert.ThrowsAny<IOException>(() => fileSystem.WriteRootMarker(missing, ClipRootMarker.Text(missing)));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task CheckRoot_IsAbsent_Unmarked_OrMarked_AndAMarkerThatCantBeRead_CountsAsNone()
    {
        var root = _directory.Combine("clips");
        using var store = ClipFileSupport.StoreWithClipRoots(_directory.Combine("data", "bookmarks.json"), root);
        var fileSystem = new FakeClipFileSystem();
        var logger = new ListLogger();
        var files = ClipFileSupport.Ops(store, logger, fileSystem);

        Assert.Equal(ClipRootState.Absent, files.CheckRoot(root));
        Directory.CreateDirectory(root);
        Assert.Equal(ClipRootState.Unmarked, files.CheckRoot(root));
        Assert.Equal(ClipRootMarking.Written, await files.WriteRootMarkerAsync(root, Ct));
        Assert.Equal(ClipRootState.Marked, files.CheckRoot(root));
        Assert.Equal(ClipRootMarking.AlreadyMarked, await files.WriteRootMarkerAsync(root, Ct));
        Assert.Single(fileSystem.MarkerWrites);

        fileSystem.MarkerReadFailure = _ => new UnauthorizedAccessException("Access to the path is denied.");
        Assert.Equal(ClipRootState.Unmarked, files.CheckRoot(root));
        fileSystem.FolderFailure = _ => new IOException("The device is not ready.");
        Assert.Equal(ClipRootState.Absent, files.CheckRoot(root));

        // A write that fails says so, without the folder at Information or above, and records nothing.
        fileSystem.MarkerReadFailure = null;
        fileSystem.FolderFailure = null;
        var token = store.GetClipRootToken(root);
        File.Delete(ClipRootMarker.PathIn(root));
        fileSystem.MarkerWriteFailure = _ => new IOException("The media is write protected.");
        Assert.Equal(ClipRootMarking.Failed, await files.WriteRootMarkerAsync(root, Ct));
        Assert.Equal(token, store.GetClipRootToken(root));
        Assert.DoesNotContain(logger.Entries.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Information), e => e.Message.Contains(root, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Debug && e.Message.Contains(root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheMarkersToken_IsRandomPerWriting_AndJudgedAgainstTheTokenTheStoreRecords()
    {
        // Found in testing: the RootId alone is the path's hash, so an older clip drive or a backup copy of the folder
        // back under the same path counted as the folder. A marker carries a token of its own, recorded in the store.
        var root = _directory.Combine("clips");
        var first = ClipRootMarker.NewToken();
        var second = ClipRootMarker.NewToken();
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.NotEqual(first, second);

        var text = ClipRootMarker.Text(root, first);
        Assert.EndsWith("\nRootId=" + ClipRootPaths.RootId(root) + "\nToken=" + first + "\n", text, StringComparison.Ordinal);
        Assert.Equal((true, first), ClipRootMarker.Read(text, root));
        Assert.Equal((true, first), ClipRootMarker.Read(text.Replace("\n", "\r\n", StringComparison.Ordinal).Replace(first, first.ToUpperInvariant(), StringComparison.Ordinal), root));
        Assert.Equal((true, (string?)null), ClipRootMarker.Read(ClipRootMarker.Text(root), root));
        Assert.Equal((false, (string?)null), ClipRootMarker.Read(ClipRootMarker.Text(_directory.Combine("other"), first), root));

        // Marked only with the recorded token; another token, or none, is another folder once a token is recorded; while none
        // is, a marker that names the folder is adopted once (a marker of an early 1.2.0 build, or one whose token was lost).
        Assert.Equal(ClipRootState.Marked, ClipRootMarker.Judge(text, root, first));
        Assert.Equal(ClipRootState.Marked, ClipRootMarker.Judge(text, root, first.ToUpperInvariant()));
        Assert.Equal(ClipRootState.Unmarked, ClipRootMarker.Judge(text, root, second));
        Assert.Equal(ClipRootState.Unmarked, ClipRootMarker.Judge(ClipRootMarker.Text(root), root, first));
        Assert.Equal(ClipRootState.Adoptable, ClipRootMarker.Judge(ClipRootMarker.Text(root), root, null));
        Assert.Equal(ClipRootState.Adoptable, ClipRootMarker.Judge(text, root, null));
        Assert.Equal(ClipRootState.Unmarked, ClipRootMarker.Judge(ClipRootMarker.Text(_directory.Combine("other"), first), root, first));
        Assert.Equal(ClipRootState.Unmarked, ClipRootMarker.Judge(null, root, null));
    }

    [Fact]
    public async Task AnOlderCopyOfTheFolder_BackUnderItsPath_IsUnmarked_AndAMarkerWithoutAToken_IsAdoptedOnce()
    {
        // On the real disk and store: the folder's marker as written today, a copy of the folder from
        // before (its marker an older token's) put back in its place, and a marker without a token.
        var root = _directory.Combine("clips");
        Directory.CreateDirectory(root);
        var file = _directory.Combine("data", "bookmarks.json");
        var store = ClipFileSupport.StoreWithClipRoots(file, root);
        var fileSystem = new FakeClipFileSystem();
        var files = ClipFileSupport.Ops(store, fileSystem: fileSystem);

        // A marker without a token (an early 1.2.0 build's): adopted, given a token of its own, recorded; Marked from then on.
        ClipFileSupport.MarkClipFolderWithoutToken(root);
        Assert.Null(store.GetClipRootToken(root));
        Assert.Equal(ClipRootState.Adoptable, files.CheckRoot(root));
        Assert.Equal(ClipRootMarking.Written, await files.WriteRootMarkerAsync(root, Ct));
        var current = store.GetClipRootToken(root);
        Assert.Equal(current, ClipFileSupport.TokenOf(root));
        Assert.Equal(ClipRootState.Marked, files.CheckRoot(root));

        // The token is in bookmarks.json (it survives a restart), under ClipRootTokens.
        store.Dispose();
        store = BookmarkStore.Create(file, NullLogger.Instance);
        files = ClipFileSupport.Ops(store, fileSystem: fileSystem);
        Assert.Equal(current, store.GetClipRootToken(root.ToUpperInvariant()));
        Assert.Contains("\"ClipRootTokens\"", await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
        Assert.Equal(ClipRootState.Marked, files.CheckRoot(root));

        // An older copy back in its place: its marker names the folder, but an older token (or, older still, none).
        PhysicalClipFileSystem.Instance.WriteRootMarker(root, ClipRootMarker.Text(root, ClipRootMarker.NewToken()));
        Assert.Equal(ClipRootState.Unmarked, files.CheckRoot(root));
        ClipFileSupport.MarkClipFolderWithoutToken(root);
        Assert.Equal(ClipRootState.Unmarked, files.CheckRoot(root));

        // A marker with a token while none is recorded (the store's write failed after the marker's): its token is adopted,
        // the file kept.
        var other = _directory.Combine("other");
        Directory.CreateDirectory(other);
        await store.AddClipRootAsync(other, Ct);
        ClipFileSupport.MarkClipFolder(other);
        var onDisk = ClipFileSupport.TokenOf(other);
        var writes = fileSystem.MarkerWrites.Count;
        Assert.Equal(ClipRootState.Adoptable, files.CheckRoot(other));
        Assert.Equal(ClipRootMarking.Adopted, await files.WriteRootMarkerAsync(other, Ct));
        Assert.Equal(writes, fileSystem.MarkerWrites.Count);
        Assert.Equal(onDisk, store.GetClipRootToken(other));
        Assert.Equal(ClipRootState.Marked, files.CheckRoot(other));

        // A folder that isn't recorded gets nothing.
        var stray = _directory.Combine("stray");
        Directory.CreateDirectory(stray);
        Assert.Equal(ClipRootMarking.Failed, await files.WriteRootMarkerAsync(Path.TrimEndingDirectorySeparator(Path.GetFullPath(stray)), Ct));
        Assert.False(File.Exists(ClipRootMarker.PathIn(stray)));
        store.Dispose();
    }

    [Fact]
    public async Task TheTokens_AreLeftOutOfAFileWithoutOne_AndARestoredSnapshotKeepsTheTokensTheDataKnew()
    {
        // ClipRootTokens is written only once it has an entry, so a schema 2 file without tokens is the same bytes as before
        // (Bookmarks 1.1 keeps an unknown field as it is); a snapshot restored keeps the tokens of today's markers.
        var root = _directory.Combine("clips");
        Directory.CreateDirectory(root);
        var file = _directory.Combine("data", "bookmarks.json");
        using var store = ClipFileSupport.StoreWithClipRoots(file, root);
        await store.AddClipRootAsync(_directory.Combine("second"), Ct);
        Assert.DoesNotContain("ClipRootTokens", await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);

        var older = store.Documents.Current.WithClipRootToken(root, "0123456789abcdef0123456789abcdef");
        var today = store.Documents.Current.WithClipRootToken(root, "fedcba9876543210fedcba9876543210");
        var restored = Jellyfin.Plugin.Bookmarks.Services.BookmarkAdminTools.AfterRestore(older, today, DateTime.UtcNow, out _);
        Assert.Equal("fedcba9876543210fedcba9876543210", restored.ClipRootTokens[root]);
        Assert.Same(today, Jellyfin.Plugin.Bookmarks.Services.BookmarkAdminTools.AfterRestore(today, today, DateTime.UtcNow, out _));
    }

    [Fact]
    public void TheMarker_IsNoClipFile_TheGuardRefusesIt_AndTheJanitorsPatternNeverTakesIt()
    {
        var root = _directory.Combine("clips");
        Directory.CreateDirectory(root);
        using var store = BookmarkStore.Create(_directory.Combine("data", "bookmarks.json"), NullLogger.Instance);

        Assert.Equal(ClipPathVerdict.NotAClipFile, ClipFileGuard.Check(ClipRootMarker.PathIn(root), [root], PhysicalClipFileSystem.Instance).Verdict);
        Assert.False(ClipNamePattern.IsPartialFileName(ClipRootMarker.FileName));
        Assert.False(ClipNamePattern.IsPartialFileName(ClipRootMarker.FileName + ".0123456789abcdef0123456789abcdef.tmp"));
    }
}
