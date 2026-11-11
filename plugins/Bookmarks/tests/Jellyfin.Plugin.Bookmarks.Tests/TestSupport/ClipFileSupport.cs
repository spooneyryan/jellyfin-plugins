using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// The clip file operations for tests: <see cref="ClipFileOps"/> and <see cref="PartialFileJanitor"/>
/// over a store, and stores whose <c>ClipRoots</c> already hold the tests' clip folders, as the worker records them before
/// it writes a clip (or the 1→2 upgrade seeds them).
/// </summary>
internal static class ClipFileSupport
{
    /// <summary>
    /// The pause between attempts in unit tests; the held-open integration tests use the real 200 ms.
    /// </summary>
    public static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// The clip file operations over a store, by default on the real file system with the unit tests' short pause, telling
    /// the My Clips list's cache about the files they change when given one.
    /// </summary>
    public static ClipFileOps Ops(
        IBookmarkStore store,
        ILogger? logger = null,
        IClipFileSystem? fileSystem = null,
        TimeSpan? retryDelay = null,
        ClipStateCache? states = null,
        BookmarkPaths? paths = null,
        TimeProvider? clock = null)
        => new(
            store,
            new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance),
            logger ?? NullLogger.Instance,
            fileSystem ?? PhysicalClipFileSystem.Instance,
            retryDelay ?? FastRetry,
            states,
            paths,
            clock);

    /// <summary>
    /// The free-space guard over a store, by default with a probe that finds 1 TiB free everywhere, the
    /// default settings, the system clock, the default 5 s watchdog interval and the real file system.
    /// </summary>
    public static ClipStorageGuard Guard(
        IBookmarkStore store,
        IDriveSpaceProbe? probe = null,
        PluginConfiguration? configuration = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        BookmarkPaths? paths = null,
        TimeSpan? probeTimeout = null,
        IClipFileSystem? fileSystem = null)
        => new(
            probe ?? new FakeDriveSpaceProbe(),
            store,
            TestData.ConfigurationProvider(configuration ?? new PluginConfiguration()),
            paths ?? BookmarkPaths.ForRoot(Path.Combine(Path.GetTempPath(), "jfbm-tests-no-data")),
            logger ?? NullLogger.Instance,
            fileSystem ?? PhysicalClipFileSystem.Instance,
            timeProvider ?? TimeProvider.System,
            ClipStorageGuard.DefaultWatchdogInterval,
            probeTimeout ?? ClipStorageGuard.DefaultProbeTimeout);

    /// <summary>
    /// A plan as the real planner makes it, for a mocked planner: the bookmark's range in the mode, written to
    /// <c>&lt;root&gt;/&lt;bookmark id&gt;.mkv</c> or <c>.mp4</c>, with an estimate.
    /// </summary>
    public static ClipPlan Plan(Bookmark bookmark, BookmarkClipMode mode, string root, long? estimatedBytes = 1_000_000)
    {
        var output = Path.Combine(root, bookmark.Id.ToString("N") + ClipFileNaming.GetExtension(mode));
        var duration = TimeSpan.FromTicks((bookmark.EndTicks ?? bookmark.StartTicks) - bookmark.StartTicks);
        return new ClipPlan
        {
            EncoderPath = "ffmpeg",
            OutputPath = output,
            TempPath = output + ClipFileGuard.PartialExtension,
            Duration = duration,
            Root = root,
            EstimatedBytes = estimatedBytes,
            Command = new ClipCommand
            {
                InputPath = "source.mkv",
                OutputPath = output + ClipFileGuard.PartialExtension,
                Mode = mode,
                Start = TimeSpan.FromTicks(bookmark.StartTicks),
                Duration = duration
            }
        };
    }

    /// <summary>
    /// When this test process started: before every file a test writes.
    /// </summary>
    public static readonly DateTime TestProcessStartUtc = GetTestProcessStartUtc();

    /// <summary>
    /// The janitor over a store and its file operations. By default its run started with this test process, so every
    /// <c>.partial</c> file a test writes is newer and kept; a test of the stale-file clean-up passes the run's start.
    /// </summary>
    public static PartialFileJanitor Janitor(
        IBookmarkStore store,
        ClipFileOps files,
        PluginConfiguration? configuration = null,
        DateTime? startedUtc = null,
        ILogger? logger = null)
        => new(
            files,
            store,
            TestData.ConfigurationProvider(configuration ?? new PluginConfiguration()),
            new BackgroundWriteGate(store, NullLogger<BackgroundWriteGate>.Instance),
            logger ?? NullLogger<PartialFileJanitor>.Instance,
            startedUtc ?? TestProcessStartUtc);

    private static DateTime GetTestProcessStartUtc()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    /// <summary>
    /// Unplugs a clip folder's drive: the folder is gone, as a drive letter is when its USB drive is unplugged
    /// (.NET reads both as <see cref="DirectoryNotFoundException"/>), while its files still exist, moved aside as they
    /// would be on the drive.
    /// </summary>
    /// <param name="folder">The clip folder.</param>
    public static void Unplug(string folder) => Directory.Move(folder, folder + "-unplugged");

    /// <summary>
    /// Plugs the drive of a clip folder <see cref="Unplug"/> took away in again: the folder and its files are back.
    /// </summary>
    /// <param name="folder">The clip folder.</param>
    public static void PlugIn(string folder) => Directory.Move(folder + "-unplugged", folder);

    /// <summary>
    /// Gives a clip folder its marker file (<see cref="ClipRootMarker"/>) with a new token (what proves the folder is the
    /// recorded one), as the clip worker does when it makes the folder's first clip. The token is recorded by
    /// <see cref="StoreWithClipRoots"/> for a store made afterwards, or by <see cref="MarkClipFolderAsync"/>; a store that
    /// doesn't record it adopts it at its first look (<see cref="ClipRootState.Adoptable"/>).
    /// </summary>
    /// <param name="folder">The clip folder; it must be there.</param>
    /// <returns>The folder, normalised.</returns>
    public static string MarkClipFolder(string folder)
    {
        var root = ClipRootPaths.TryNormalize(folder, out var normalized) ? normalized : throw new ArgumentException("Not a folder: " + folder, nameof(folder));
        PhysicalClipFileSystem.Instance.WriteRootMarker(root, ClipRootMarker.Text(root, ClipRootMarker.NewToken()));
        return root;
    }

    /// <summary>
    /// Gives a recorded clip folder its marker file with a new token and records the token in the store, as the clip worker
    /// leaves a folder.
    /// </summary>
    /// <param name="store">The store, which records the folder.</param>
    /// <param name="folder">The clip folder; it must be there.</param>
    /// <returns>The folder, normalised.</returns>
    public static async Task<string> MarkClipFolderAsync(IBookmarkStore store, string folder)
    {
        var root = MarkClipFolder(folder);
        var token = TokenOf(root) ?? throw new InvalidOperationException("No token in the marker just written.");
        await store.UpdateDocumentAsync(d => d.WithClipRootToken(root, token), CancellationToken.None);
        return root;
    }

    /// <summary>
    /// Gives a clip folder a marker file as an early 1.2.0 development build wrote it, without a token: adopted once while
    /// no token is recorded for the folder.
    /// </summary>
    /// <param name="folder">The clip folder; it must be there.</param>
    /// <returns>The folder, normalised.</returns>
    public static string MarkClipFolderWithoutToken(string folder)
    {
        var root = ClipRootPaths.TryNormalize(folder, out var normalized) ? normalized : throw new ArgumentException("Not a folder: " + folder, nameof(folder));
        PhysicalClipFileSystem.Instance.WriteRootMarker(root, ClipRootMarker.Text(root));
        return root;
    }

    /// <summary>
    /// Gets the token a clip folder's marker file names, or <c>null</c>.
    /// </summary>
    /// <param name="folder">The clip folder.</param>
    /// <returns>The token.</returns>
    public static string? TokenOf(string folder)
        => ClipRootPaths.TryNormalize(folder, out var root) ? ClipRootMarker.Read(PhysicalClipFileSystem.ReadMarkerFile(root), root).Token : null;

    /// <summary>
    /// Takes a clip folder's marker file away (a folder of Bookmarks 1.1 or of an early 1.2.0 development build, which wrote none).
    /// </summary>
    /// <param name="folder">The clip folder.</param>
    public static void UnmarkClipFolder(string folder) => File.Delete(ClipRootMarker.PathIn(folder));

    /// <summary>
    /// The files in a clip folder (or in it and below it) besides its marker file: what clip jobs left there.
    /// </summary>
    /// <param name="folder">The clip folder.</param>
    /// <param name="option">Whether the folders below it count too.</param>
    /// <returns>The files' full paths.</returns>
    public static string[] FilesBesidesMarker(string folder, SearchOption option = SearchOption.TopDirectoryOnly)
        => Directory.GetFiles(folder, "*", option)
            .Where(file => !string.Equals(Path.GetFileName(file), ClipRootMarker.FileName, StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// Whether a clip folder has its marker file, naming it.
    /// </summary>
    /// <param name="folder">The clip folder.</param>
    /// <returns>Whether it is marked.</returns>
    public static bool IsMarked(string folder)
        => ClipRootPaths.TryNormalize(folder, out var root) && ClipRootMarker.Names(PhysicalClipFileSystem.ReadMarkerFile(root), root);

    /// <summary>
    /// Writes an empty schema 2 <c>bookmarks.json</c> with the given clip folders, and opens the store over it. A folder whose
    /// marker file names a token (<see cref="MarkClipFolder"/>) has it recorded, as the clip worker leaves it.
    /// </summary>
    /// <param name="file">The store file; it must not exist yet.</param>
    /// <param name="clipRoots">The clip folders to record.</param>
    /// <returns>The store.</returns>
    public static BookmarkStore StoreWithClipRoots(string file, params string[] clipRoots)
    {
        if (File.Exists(file))
        {
            throw new InvalidOperationException("The store file exists already.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var roots = clipRoots.Select(r => ClipRootPaths.TryNormalize(r, out var root) ? root : throw new ArgumentException("Not a folder: " + r, nameof(clipRoots))).ToArray();
        var tokens = roots.Select(r => (Root: r, Token: Directory.Exists(r) ? TokenOf(r) : null)).Where(t => t.Token is not null).ToDictionary(t => t.Root, t => t.Token!);
        object document = tokens.Count == 0
            ? new { Version = BookmarkStoreDocument.CurrentVersion, ClipRoots = roots, Bookmarks = Array.Empty<object>() }
            : new { Version = BookmarkStoreDocument.CurrentVersion, ClipRoots = roots, ClipRootTokens = tokens, Bookmarks = Array.Empty<object>() };
        File.WriteAllText(file, JsonSerializer.Serialize(document));
        return BookmarkStore.Create(file, NullLogger.Instance);
    }
}
