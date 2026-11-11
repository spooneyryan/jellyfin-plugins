using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Preflight;

/// <summary>
/// The minute-to-minute checks: playing from sessions (the item, a version through
/// <c>FullNowPlayingItem</c> or <c>MediaSourceId</c>), the busy set over the unit's items and version partners, the scan
/// warning, any file open anywhere in the unit (error 5 for a folder), and the write probe, which leaves nothing behind.
/// </summary>
public sealed class MovePreflightTests : IDisposable
{
    private readonly PlannerWorld _w = new();
    private readonly List<SessionInfo> _sessions = [];
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly LibraryOperationGate _gate;
    private readonly MovePreflight _preflight;

    public MovePreflightTests()
    {
        _sessionManager.SetupGet(s => s.Sessions).Returns(_sessions);
        _gate = new LibraryOperationGate(_w.Library.Object);
        _preflight = new MovePreflight(_sessionManager.Object, _gate, new FileLockProbe(static (_, _) => Task.CompletedTask));
    }

    public void Dispose()
    {
        _gate.Dispose();
        _w.Dispose();
    }

    [Fact]
    public async Task AFreeUnit_PassesClean_AndThePlanTimeCheckChangesNothing()
    {
        // A plan-time probe file once changed its non-dot parent folder, and the library watcher's
        // Changed event on it started a FileRefresher. The access check makes no entry, no write time and no event.
        var (movie, _, _) = OwnFolderMovie();
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        var before = Snapshot();
        var written = Directory.GetLastWriteTimeUtc(_w.Cult);
        var events = new List<string>();

        using (Watch(_w.Root, events))
        {
            await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan-1", null, TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Empty(unit.Blockers);
        Assert.Empty(unit.Warnings);
        Assert.Equal(before, Snapshot());
        Assert.Equal(written, Directory.GetLastWriteTimeUtc(_w.Cult));
        lock (events)
        {
            Assert.Empty(events);
        }
    }

    [Fact]
    public async Task ANewWrapFolder_IsCheckedInItsNearestExistingParent_ForAddingAFolder()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        _w.Movie(Path.Join(_w.Cult, "Planet Terror (2007)", "Planet Terror (2007).mkv"), "Planet Terror", 2007, false);
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, wrap: true);
        Assert.False(Directory.Exists(unit.TargetDirectory));
        var before = Snapshot();
        var access = new RecordingAccess();

        await Preflight(access).CheckAsync(unit, DestinationWriteCheck.Access, "plan-2", null, TestContext.Current.CancellationToken);

        Assert.Empty(unit.Blockers);
        Assert.Equal([(_w.Cult, false, true)], access.Asked);
        Assert.Equal(before, Snapshot());
        Assert.False(Directory.Exists(unit.TargetDirectory));
    }

    [Fact]
    public async Task TheAccessAsked_IsWhatTheCommitAdds()
    {
        // A folder unit adds a folder; a file set its files, and a folder too for its trickplay folder.
        var (alien, _, _) = OwnFolderMovie();
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        var ronin = _w.Movie(Path.Join(_w.Movies, "Ronin.1998.mkv"), "Ronin", 1998, true, "Ronin.1998.trickplay/320 - 10x10/0.jpg");
        var access = new RecordingAccess();
        var preflight = Preflight(access);

        foreach (var movie in new[] { alien, heat, ronin })
        {
            var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, wrap: false);
            await preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);
            Assert.Empty(unit.Blockers);
        }

        Assert.Equal([(_w.Cult, false, true), (_w.Cult, true, false), (_w.Cult, true, true)], access.Asked);
    }

    [Fact]
    public async Task AnUnwritableDestination_IsMoveNotWritable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("ACLs are tested on Windows.");
            return;
        }

        // A real ACL: Cult refuses new subfolders to this account, so a folder unit can't land there.
        var (movie, _, _) = OwnFolderMovie();
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        using (new DenyAccess(_w.Cult, FileSystemRights.CreateDirectories))
        {
            await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan-3", null, TestContext.Current.CancellationToken);
        }

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.NotWritable, blocker.Code);
        Assert.Equal([_w.Cult], blocker.Paths);
    }

#if !MOVE_PREVIEW_ONLY
    // The plan-only build has no probe file (PreviewOnlyBuildTests checks its IL).
    [Fact]
    public async Task TheJobStartProbeFile_CatchesWhatTheAccessCheckCant_AndChangesTheFolder()
    {
        // DestinationWriteCheck.ProbeFile (job start, inside step 3a's suppression scope): the access check, then the
        // dot-named probe file, which the watcher does see; deleted at once.
        var (movie, _, _) = OwnFolderMovie();
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        var events = new List<string>();
        using (Watch(_w.Cult, events))
        {
            await _preflight.CheckAsync(unit, DestinationWriteCheck.ProbeFile, "job-1", null, TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Empty(unit.Blockers);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_w.Cult));
        lock (events)
        {
            Assert.Contains(events, e => e.EndsWith(MovePreflight.ProbePrefix + "job-1", StringComparison.Ordinal));
        }

        // A folder where the probe file goes makes CreateNew fail, as a share that refuses writes would.
        Directory.CreateDirectory(Path.Join(_w.Cult, MovePreflight.ProbePrefix + "job-2"));
        var again = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        await _preflight.CheckAsync(again, DestinationWriteCheck.ProbeFile, "job-2", null, TestContext.Current.CancellationToken);
        var blocker = Assert.Single(again.Blockers);
        Assert.Equal((MoveErrorCodes.NotWritable, _w.Cult), (blocker.Code, Assert.Single(blocker.Paths!)));
    }
#endif

    [Fact]
    public async Task ASymlinkedSidecar_IsNeverOpened_ThroughTheLink()
    {
        // The lock probe once opened the link's target, held elsewhere, and called the sidecar in use.
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        var target = _w.File(Path.Join(_w.Folder("Outside"), "elsewhere.srt"));
        Paths.SymbolicLink.ToFile(Path.Join(_w.Movies, "Heat.1995.srt"), target);
        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary);
        var asked = new List<string>();
        var preflight = new MovePreflight(_sessionManager.Object, _gate, new FileLockProbe(static (_, _) => Task.CompletedTask, path =>
        {
            asked.Add(path);
            return FileLockProbe.IsOpen(path);
        }));

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan-l", null, TestContext.Current.CancellationToken);
        }

        Assert.Equal([MoveErrorCodes.ReparsePoint], unit.Blockers.Select(b => b.Code));
        Assert.Equal([heat.Path], asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExistingWrapFolderThatIsALink_IsNeverWrittenBelow(bool symbolic)
    {
        // A linked folder: the probe file was once made in the link's target, outside every library.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Links are tested on Windows.");
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        _w.Movie(Path.Join(_w.Cult, "Planet Terror (2007)", "Planet Terror (2007).mkv"), "Planet Terror", 2007, false);
        var outside = _w.Folder("Outside");
        var link = Path.Join(_w.Cult, "Heat (1995)");
        if (symbolic)
        {
            Paths.SymbolicLink.ToFolder(link, outside);
        }
        else
        {
            Paths.Junction.Create(link, outside);
        }

        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary);
        var access = new RecordingAccess();
        var events = new List<string>();
        using (Watch(outside, events))
        {
            // Even the job-start probe file never goes below a link (the plan-only build has only the access check).
#if MOVE_PREVIEW_ONLY
            const DestinationWriteCheck check = DestinationWriteCheck.Access;
#else
            const DestinationWriteCheck check = DestinationWriteCheck.ProbeFile;
#endif
            await Preflight(access).CheckAsync(unit, check, "plan-w", null, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        Assert.Equal([MoveErrorCodes.ReparsePoint], unit.Blockers.Select(b => b.Code));
        Assert.Empty(access.Asked);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        lock (events)
        {
            Assert.Empty(events);
        }
    }

    [Fact]
    public async Task ThePrimaryPlaying_IsMovePlaying_NamingTheDevice()
    {
        var (movie, _, _) = OwnFolderMovie();
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        Play(s => s.NowPlayingItem = new BaseItemDto { Id = movie.Id });

        await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.Playing, blocker.Code);
        Assert.Equal("Playing on Living room (Jellyfin Web); stop playback first.", blocker.Message);
    }

    [Fact]
    public async Task AVersionPlaying_ByItsItemOrItsMediaSource_IsMovePlaying()
    {
        var (movie, version, _) = OwnFolderMovie();
        var versionId = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary).Items.Single(i => i.OldPath == version).OldId;

        foreach (var play in new Action<SessionInfo>[]
        {
            s => s.FullNowPlayingItem = new Video { Id = versionId },
            s => s.PlayState = new PlayerStateInfo { MediaSourceId = versionId.ToString("N") },
        })
        {
            _sessions.Clear();
            Play(play);
            var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
            await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);
            Assert.Equal(MoveErrorCodes.Playing, Assert.Single(unit.Blockers).Code);
        }

        // Something else playing doesn't count.
        _sessions.Clear();
        Play(s => s.NowPlayingItem = new BaseItemDto { Id = Guid.NewGuid() });
        var free = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        await _preflight.CheckAsync(free, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);
        Assert.Empty(free.Blockers);
    }

    [Fact]
    public async Task AnItemOrVersionPartnerInAnotherOperation_IsMoveItemBusy_ButNotForThatOperationItself()
    {
        var (movie, _, _) = OwnFolderMovie();
        var linked = _w.Movie(Path.Join(_w.Movies, "Alien (1979) Directors Cut", "Alien (1979) Directors Cut.mkv"), "Alien", 1979, false);
        movie.LinkedAlternateVersions = [new LinkedChild { Path = linked.Path, ItemId = linked.Id }];

        Assert.True(_gate.TryMarkBusy(OperationOwner.Duplicates, "merge-1", [linked.Id], out _));
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);
        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.ItemBusy, "This item is being changed on the Duplicates tab."), (blocker.Code, blocker.Message));

        _gate.Release("merge-1");
        Assert.True(_gate.TryMarkBusy(OperationOwner.Move, "job-1", [movie.Id], out _));
        unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);
        Assert.Equal("This item is being moved.", Assert.Single(unit.Blockers).Message);

        // A job re-checking its own unit isn't blocked by its own entries.
        unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "job-1", "job-1", TestContext.Current.CancellationToken);
        Assert.Empty(unit.Blockers);
    }

    [Fact]
    public async Task APredictedIdInTheBusySet_IsMoveItemBusy()
    {
        var (movie, _, _) = OwnFolderMovie();
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        Assert.True(_gate.TryMarkBusy(OperationOwner.Duplicates, "merge-1", [unit.Items[0].PredictedId], out _));

        await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);

        Assert.Equal(MoveErrorCodes.ItemBusy, Assert.Single(unit.Blockers).Code);
    }

    [Fact]
    public async Task ARunningScan_IsAWarning()
    {
        var (movie, _, _) = OwnFolderMovie();
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        _w.Library.IsScanRunning = true;

        await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);

        Assert.Empty(unit.Blockers);
        Assert.Equal(MoveErrorCodes.ScanRunning, Assert.Single(unit.Warnings).Code);
    }

    [Fact]
    public async Task AFolderWithAnythingOpenInside_IsMoveInUse()
    {
        // A file deep in the unit (an extra), not one of the items' own, open in another program.
        var (movie, _, featurette) = OwnFolderMovie();
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        Assert.Equal(MoveUnitKind.Folder, unit.Kind);

        using (new FileStream(featurette, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);
        }

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.InUse, blocker.Code);
        Assert.Equal([featurette], blocker.Paths);
        Assert.StartsWith("Making of.mkv is open in another program", blocker.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpenSidecarOfAFileSet_IsMoveInUse_CountingTheFiles()
    {
        var movie = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt", "Heat.1995.nfo");
        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);
        Assert.Equal(MoveUnitKind.FileSet, unit.Kind);

        using (File.OpenRead(Path.Join(_w.Movies, "Heat.1995.srt")))
        using (File.OpenRead(movie.Path))
        {
            await _preflight.CheckAsync(unit, DestinationWriteCheck.Access, "plan", null, TestContext.Current.CancellationToken);
        }

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal(MoveErrorCodes.InUse, blocker.Code);
        Assert.Equal(2, blocker.Paths!.Count);
        Assert.StartsWith("2 files are open", blocker.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionPartners_AreTheLinkedVersionsAndTheLinkedPrimary()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var video = new Movie { LinkedAlternateVersions = [new LinkedChild { ItemId = a }, new LinkedChild { ItemId = null }], PrimaryVersionId = b.ToString("N") };
        Assert.Equal([a, b], MovePreflight.VersionPartners(video));
        Assert.Empty(MovePreflight.VersionPartners(new Movie { PrimaryVersionId = "not a guid" }));
    }

    private (Movie Movie, string Version, string Featurette) OwnFolderMovie()
    {
        var folder = _w.Folder("Movies", "Alien (1979)");
        var movie = _w.Movie(Path.Join(folder, "Alien (1979) - 1080p.mkv"), "Alien", 1979, false, "Alien (1979) - 1080p.nfo", "poster.jpg");
        var version = _w.File(Path.Join(folder, "Alien (1979) - 720p.mkv"));
        movie.LocalAlternateVersions = [version];
        var featurette = Path.Join(folder, "extras", "Making of.mkv");
        _w.Extra(movie, featurette, ExtraType.Featurette);
        return (movie, version, featurette);
    }

    private void Play(Action<SessionInfo> setup)
    {
        var session = new SessionInfo(_sessionManager.Object, NullLogger.Instance) { DeviceName = "Living room", Client = "Jellyfin Web" };
        setup(session);
        _sessions.Add(session);
    }

    // LibraryMonitor's watcher (v10.11.5:Emby.Server.Implementations/IO/LibraryMonitor.cs:233-240), recording every event.
    private static FileSystemWatcher Watch(string folder, List<string> events)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
        };

        void Seen(object sender, FileSystemEventArgs e)
        {
            lock (events)
            {
                events.Add(e.ChangeType + " " + e.FullPath);
            }
        }

        watcher.Created += Seen;
        watcher.Changed += Seen;
        watcher.Deleted += Seen;
        watcher.Renamed += Seen;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private MovePreflight Preflight(IFolderWriteAccess access)
        => new(_sessionManager.Object, _gate, new FileLockProbe(static (_, _) => Task.CompletedTask), access);

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_w.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    private sealed class RecordingAccess : IFolderWriteAccess
    {
        public List<(string Directory, bool Files, bool Folders)> Asked { get; } = [];

        public bool CanAdd(string directory, bool files, bool folders)
        {
            Asked.Add((directory, files, folders));
            return true;
        }
    }
}
