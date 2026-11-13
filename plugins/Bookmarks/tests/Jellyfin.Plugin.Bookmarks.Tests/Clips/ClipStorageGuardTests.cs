using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The free-space guard with a fake probe and a fake clock: the request's
/// check subtracts the persisted estimates of the other Queued and Running jobs, the check when a job starts only the
/// running ones; an unavailable folder is refused and an unreadable one switches the guard off; the watchdog stops a job
/// below half the reserve, reading the setting on every tick; a full disk is recognised from ffmpeg's text and exit code
/// (the six lines measured on Windows), from the HResults, and by a probe after a failed job; users' and admins' wording; and
/// Diagnostics' <c>ClipStorage</c> entry.
/// </summary>
public sealed class ClipStorageGuardTests : IDisposable
{
    private const long Mb = ClipStorageGuard.Megabyte;
    private const long Gb = ClipStorageGuard.Gigabyte;

    // Measured on Windows, verbatim: ffmpeg 7.1.3-Jellyfin's error lines when a write fails with ENOSPC, as FfmpegRunner
    // keeps them (the last 20 non-empty lines, trimmed, joined with spaces), and its exit code.
    private static readonly string[] _fullDiskLines =
    [
        "[aost#0:1/copy @ 000001cebf328640] Error submitting a packet to the muxer: No space left on device",
        "Last message repeated 1 times",
        "[out#0/matroska @ 000001cebd615340] Error muxing a packet",
        "[out#0/matroska @ 000001cebd615340] Task finished with error code: -28 (No space left on device)",
        "[out#0/matroska @ 000001cebd615340] Terminating thread with return code -28 (No space left on device)",
        "[out#0/matroska @ 000001cebd615340] Error writing trailer: No space left on device",
        "[out#0/matroska @ 000001cebd615340] Error closing file: No space left on device",
    ];

    private readonly TempDirectory _directory = new();
    private readonly BookmarkStore _store;
    private readonly FakeDriveSpaceProbe _probe = new();
    private readonly PluginConfiguration _configuration = new();
    private readonly ListLogger _logger = new();
    private readonly ManualTimerProvider _clock = new();
    private readonly ClipStorageGuard _guard;

    public ClipStorageGuardTests()
    {
        _store = BookmarkStore.Create(_directory.Combine("bookmarks.json"), NullLogger.Instance);
        _guard = ClipFileSupport.Guard(_store, _probe, _configuration, _clock, _logger, BookmarkPaths.ForRoot(_directory.Combine("data")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder => _directory.Combine("clips", "alice");

    public void Dispose()
    {
        _store.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public void MinFreeSpaceMegabytes_Is2048ByDefault_AndNeverNegative()
    {
        Assert.Equal(2048, new PluginConfiguration().MinFreeSpaceMegabytes);
        Assert.Equal(2 * Gb, ClipStorageGuard.MinFreeBytes(new PluginConfiguration()));
        Assert.Equal(0, ClipStorageGuard.MinFreeBytes(new PluginConfiguration { MinFreeSpaceMegabytes = -5 }));
    }

    [Theory]
    [InlineData(-1, "0 KB")]
    [InlineData(476_516, "465 KB")]
    [InlineData(Mb + (Mb / 2), "1.5 MB")]
    [InlineData(37 * Mb, "37 MB")]
    [InlineData(800 * Mb, "0.8 GB")]
    [InlineData(2 * Gb, "2 GB")]
    [InlineData(264 * Gb + (512 * Mb), "264.5 GB")]
    public void Sizes_AreWrittenForPeople(long bytes, string text) => Assert.Equal(text, ClipStorageGuard.FormatSize(bytes));

    [Fact]
    public async Task TheRequestsCheck_ProbesTheClipsFolder_AndLetsAClipThroughWhenItFits()
    {
        _probe.Available(10 * Gb);

        var check = await _guard.CheckBeforeQueueAsync(Folder, 1 * Gb, Guid.NewGuid(), Ct);

        Assert.Equal(ClipSpaceVerdict.Ok, check.Verdict);
        Assert.False(check.Refused);
        Assert.Equal([Folder], _probe.Probed);
    }

    [Fact]
    public async Task TheRequestsCheck_SubtractsTheEstimatesOfTheOtherWaitingAndRunningJobs()
    {
        // Two queued jobs would together exceed free space: Available − estimate − Σ persisted
        // estimates of Queued/Running jobs < reserve → refused. The bookmark's own job, finished jobs and tombstones don't
        // count; a job without an estimate counts as nothing.
        var own = await AddAsync(BookmarkClipStatus.Queued, 5 * Gb);
        await AddAsync(BookmarkClipStatus.Queued, 1 * Gb);
        await AddAsync(BookmarkClipStatus.Running, 2 * Gb);
        await AddAsync(BookmarkClipStatus.Queued, null);
        await AddAsync(BookmarkClipStatus.Completed, 7 * Gb);
        await AddAsync(BookmarkClipStatus.Failed, 7 * Gb);
        await AddAsync(BookmarkClipStatus.Queued, 7 * Gb, tombstone: true);

        // 2 GB reserve + 1 GB clip + 3 GB of other jobs = 6 GB.
        _probe.Available(6 * Gb);
        var fits = await _guard.CheckBeforeQueueAsync(Folder, 1 * Gb, own.Id, Ct);
        _probe.Available((6 * Gb) - 1);
        var short1 = await _guard.CheckBeforeQueueAsync(Folder, 1 * Gb, own.Id, Ct);

        Assert.Equal(ClipSpaceVerdict.Ok, fits.Verdict);
        Assert.Equal(3 * Gb, fits.ReservedBytes);
        Assert.Equal(ClipSpaceVerdict.LowSpace, short1.Verdict);
        Assert.True(short1.Refused);
    }

    [Fact]
    public async Task TheCheckWhenAJobStarts_CountsTheRunningJobs_ButNotTheWaitingOnes()
    {
        // The jobs still waiting start after this one and are checked then.
        var own = await AddAsync(BookmarkClipStatus.Running, 1 * Gb);
        await AddAsync(BookmarkClipStatus.Queued, 3 * Gb);
        await AddAsync(BookmarkClipStatus.Running, 2 * Gb);

        _probe.Available(5 * Gb);
        var check = await _guard.CheckAtClaimAsync(Folder, 1 * Gb, own.Id, Ct);
        _probe.Available((5 * Gb) - 1);
        var refused = await _guard.CheckAtClaimAsync(Folder, 1 * Gb, own.Id, Ct);

        Assert.Equal(ClipSpaceVerdict.Ok, check.Verdict);
        Assert.Equal(2 * Gb, check.ReservedBytes);
        Assert.Equal(ClipSpaceVerdict.LowSpace, refused.Verdict);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.StartsWith($"The clip of bookmark {own.Id} was refused when it was about to start: C:\\ has ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClipWithoutAnEstimate_CountsAsNothing_ButTheReserveStillApplies()
    {
        _probe.Available(2 * Gb);
        Assert.Equal(ClipSpaceVerdict.Ok, (await _guard.CheckBeforeQueueAsync(Folder, null, Guid.NewGuid(), Ct)).Verdict);

        _probe.Available((2 * Gb) - 1);
        Assert.Equal(ClipSpaceVerdict.LowSpace, (await _guard.CheckBeforeQueueAsync(Folder, null, Guid.NewGuid(), Ct)).Verdict);
    }

    [Fact]
    public async Task WithNoReserve_AClipIsRefusedOnlyWhenItWouldntFit()
    {
        _configuration.MinFreeSpaceMegabytes = 0;
        _probe.Available(1 * Gb);

        Assert.Equal(ClipSpaceVerdict.Ok, (await _guard.CheckBeforeQueueAsync(Folder, 1 * Gb, Guid.NewGuid(), Ct)).Verdict);
        Assert.Equal(ClipSpaceVerdict.LowSpace, (await _guard.CheckBeforeQueueAsync(Folder, (1 * Gb) + 1, Guid.NewGuid(), Ct)).Verdict);
    }

    [Fact]
    public async Task AFolderWhoseDriveIsntThere_IsRefused_NotWavedThrough()
    {
        _probe.Unavailable();

        var check = await _guard.CheckBeforeQueueAsync(@"R:\clips\alice", 1 * Mb, Guid.NewGuid(), Ct);

        Assert.Equal(ClipSpaceVerdict.Unavailable, check.Verdict);
        Assert.True(check.Refused);
        Assert.Equal("The clip folder isn't available right now. Ask your server admin.", check.UserMessage);
        Assert.StartsWith(@"The clip folder isn't available right now: R:\clips\alice can't be reached", check.AdminMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderWhoseFreeSpaceCantBeRead_SwitchesTheGuardOffForIt_WithOneWarning()
    {
        _probe.Unknown(error: 5);

        var first = await _guard.CheckBeforeQueueAsync(Folder, 100 * Gb, Guid.NewGuid(), Ct);
        var second = await _guard.CheckBeforeQueueAsync(Folder, 100 * Gb, Guid.NewGuid(), Ct);

        Assert.Equal(ClipSpaceVerdict.GuardOff, first.Verdict);
        Assert.False(first.Refused);
        Assert.Equal(ClipSpaceVerdict.GuardOff, second.Verdict);
        var warning = Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Contains("free space can't be read (error 5), so the guard is off for this folder", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeThatDoesntAnswer_IsUnknown_AfterTheTimeout()
    {
        // Measured on Windows: a share that doesn't answer can block much longer than a local call.
        using var never = new ManualResetEventSlim();
        _probe.Answer(folder =>
        {
            never.Wait(TimeSpan.FromSeconds(5));
            return FakeDriveSpaceProbe.Ok(folder, 10 * Gb);
        });
        var guard = ClipFileSupport.Guard(_store, _probe, _configuration, _clock, _logger, probeTimeout: TimeSpan.FromMilliseconds(100));

        var space = await guard.ProbeAsync(Folder, Ct);
        never.Set();

        Assert.Equal(DriveSpaceStatus.Unknown, space.Status);
        Assert.True(space.TimedOut);
    }

    [Fact]
    public async Task AProbeThatThrows_IsUnknown_AndDiagnosticsStillAnswers()
    {
        // An exception the probe lets through (it shouldn't) must not fail a request or the Diagnostics body,
        // whose sources are waited for synchronously.
        _probe.Answer(_ => throw new FormatException("C:\\clips\\alice"));

        var check = await _guard.CheckBeforeQueueAsync(Folder, Gb, Guid.NewGuid(), Ct);
        var entry = _guard.DescribeCurrentRoot();

        Assert.Equal(ClipSpaceVerdict.GuardOff, check.Verdict);
        Assert.Equal("Unknown", entry["FreeSpace"]);
        Assert.Equal("FreeSpaceUnknown", Assert.Single(Warnings(entry)).Code);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message == "The free-space probe failed (FormatException); free space is unknown for this check");
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information && e.Message.Contains("alice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AUserIsToldNoDriveOrNumbers_AnAdminTheDriveAndTheNumbers()
    {
        // The admin message names the drive and numbers; the user message does not.
        await AddAsync(BookmarkClipStatus.Queued, 500 * Mb);
        _probe.Available(1200 * Mb, volume: @"Q:\");

        var check = await _guard.CheckBeforeQueueAsync(Folder, 300 * Mb, Guid.NewGuid(), Ct);

        Assert.Equal(ClipSpaceVerdict.LowSpace, check.Verdict);
        Assert.Equal("Not enough free space on the server for this clip. Ask your server admin.", check.UserMessage);
        Assert.Equal(
            "Not enough free space for this clip: Q:\\ has 1.2 GB free; this clip needs about 0.3 GB, the other clips waiting or being made about 0.5 GB, and 2 GB must stay free (\"Keep free on the clip drive\" in the Bookmarks settings).",
            check.AdminMessage);
        Assert.DoesNotContain(Folder, check.AdminMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("Q:\\ has 1.2 GB free; this clip needs about 0.3 GB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheWatchdog_StopsTheJob_OnceLessThanHalfTheReserveIsLeft()
    {
        // With the fake clock: nothing happens between ticks; at 1 GB (the reserve is 2 GB) it goes on, below
        // it the job is stopped with LowDiskSpace.
        using var job = new RunningClip(CancellationToken.None);
        _probe.Available(Gb);
        var watch = _guard.WatchAsync(Folder, job, Guid.NewGuid(), Ct);
        await _clock.WaitForArmedTimersAsync();

        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, _probe.Calls);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => _probe.Calls == 1);
        Assert.False(job.Token.IsCancellationRequested);

        _probe.Available(Gb - 1);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await watch.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Assert.True(job.Token.IsCancellationRequested);
        Assert.Equal(ClipStopReason.LowDiskSpace, job.StopReason);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("C:\\ has 1 GB free, less than half of the 2 GB that must stay free", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheWatchdog_ReadsTheSettingOnEveryTick()
    {
        using var job = new RunningClip(CancellationToken.None);
        _probe.Available(3 * Gb);
        var watch = _guard.WatchAsync(Folder, job, Guid.NewGuid(), Ct);
        await _clock.WaitForArmedTimersAsync();

        _clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => _probe.Calls == 1);
        Assert.False(job.Token.IsCancellationRequested);

        // Raised mid-encode above twice the free space.
        _configuration.MinFreeSpaceMegabytes = 7 * 1024;
        _clock.Advance(TimeSpan.FromSeconds(5));
        await watch.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Assert.Equal(ClipStopReason.LowDiskSpace, job.StopReason);
    }

    [Fact]
    public async Task TheWatchdog_WithoutAReserve_OrWhenFreeSpaceCantBeRead_NeverStopsAJob()
    {
        using var job = new RunningClip(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        _configuration.MinFreeSpaceMegabytes = 0;
        _probe.Available(0);
        var watch = _guard.WatchAsync(Folder, job, Guid.NewGuid(), stop.Token);
        await _clock.WaitForArmedTimersAsync();

        // Ticks without a reserve don't even probe.
        for (var tick = 0; tick < 3; tick++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(50, Ct);
        }

        Assert.Equal(0, _probe.Calls);
        _configuration.MinFreeSpaceMegabytes = 2048;
        _probe.Unknown();
        for (var tick = 1; tick <= 3; tick++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => _probe.Calls >= tick);
        }

        await stop.CancelAsync();
        await watch.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Assert.False(job.Token.IsCancellationRequested);
        Assert.Equal(ClipStopReason.None, job.StopReason);
    }

    [Fact]
    public async Task TheWatchdog_EndsWhenItsTokenIsCancelled_WithoutThrowing()
    {
        using var job = new RunningClip(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var watch = _guard.WatchAsync(Folder, job, Guid.NewGuid(), stop.Token);
        await _clock.WaitForArmedTimersAsync();

        await stop.CancelAsync();
        await watch.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Assert.Equal(0, _probe.Calls);
        Assert.Equal(0, _clock.ArmedTimers);
    }

    [Theory]
    [InlineData(-28, true)] // Fast (MKV) into a full disk: exit code and text
    [InlineData(-22, true)] // the MP4 muxer's lines are UNVERIFIED; the text alone is enough
    [InlineData(null, true)]
    public void FfmpegsLinesOfAFullDisk_AreTheClipDriveIsFull(int? exitCode, bool full)
    {
        var tail = string.Join(' ', _fullDiskLines);
        var failure = new ClipExtractionException($"ffmpeg failed with exit code {exitCode}: {tail}") { ExitCode = exitCode };

        Assert.Equal(full, ClipStorageGuard.DiskFullSignal(failure) is not null);
    }

    [Fact]
    public void ExitCodeMinus28_AloneIsAFullDisk()
        => Assert.Equal("ffmpeg's exit code -28", ClipStorageGuard.DiskFullSignal(new ClipExtractionException("ffmpeg failed with exit code -28.") { ExitCode = -28 }));

    [Theory]
    [InlineData(unchecked((int)0x80070070), true)] // ERROR_DISK_FULL
    [InlineData(unchecked((int)0x80070027), true)] // ERROR_HANDLE_DISK_FULL
    [InlineData(unchecked((int)0x80070020), false)] // a sharing violation
    [InlineData(unchecked((int)0x80070005), false)] // access denied
    public void AnIOExceptionOfAFullDisk_IsRecognised_AlsoInsideAnother(int hresult, bool full)
    {
        var io = new IOException("There is not enough space on the disk.", hresult);

        Assert.Equal(full, ClipStorageGuard.DiskFullSignal(io) is not null);
        Assert.Equal(full, ClipStorageGuard.DiskFullSignal(new ClipExtractionException("The finished clip couldn't be put in place.", io)) is not null);
        Assert.Equal(full, ClipStorageGuard.IsDiskFull(hresult));
    }

    [Fact]
    public void OtherFailures_AreNoFullDisk()
    {
        Assert.Null(ClipStorageGuard.DiskFullSignal(new ClipExtractionException("ffmpeg failed with exit code 1: Invalid data found when processing input") { ExitCode = 1 }));
        Assert.Null(ClipStorageGuard.DiskFullSignal(new InvalidOperationException("No space left on device")));
    }

    [Fact]
    public async Task AFailureThatSaysSo_IsWordedTheClipDriveIsFull_WithoutAProbe()
    {
        var failure = new ClipExtractionException("ffmpeg failed with exit code -28: " + string.Join(' ', _fullDiskLines)) { ExitCode = -28 };

        Assert.Equal("The clip drive is full.", await _guard.DescribeFailureAsync(failure, Folder, wroteOutput: true, Guid.NewGuid()));
        Assert.Equal(0, _probe.Calls);
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.EndsWith("failed: the clip drive is full (ffmpeg's exit code -28)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnyOtherFailureOfAJobThatWrote_IsAFullDisk_WhenLessThanHalfTheReserveIsLeft()
    {
        // Measured on Windows: a full disk reported as ERROR_HANDLE_DISK_FULL reads "Invalid argument" (-22) in ffmpeg.
        var failure = new ClipExtractionException("ffmpeg failed with exit code -22: Error muxing a packet: Invalid argument") { ExitCode = -22 };

        _probe.Available(Gb - 1);
        Assert.Equal("The clip drive is full.", await _guard.DescribeFailureAsync(failure, Folder, wroteOutput: true, Guid.NewGuid()));
        _probe.Available(Gb);
        Assert.Null(await _guard.DescribeFailureAsync(failure, Folder, wroteOutput: true, Guid.NewGuid()));

        // A job that failed before it wrote anything (the planner, the check when it started) is never re-probed.
        _probe.Available(0);
        var calls = _probe.Calls;
        Assert.Null(await _guard.DescribeFailureAsync(failure, Folder, wroteOutput: false, Guid.NewGuid()));
        Assert.Null(await _guard.DescribeFailureAsync(failure, null, wroteOutput: true, Guid.NewGuid()));
        Assert.Equal(calls, _probe.Calls);
    }

    [Fact]
    public void ClipStorage_ForAClipFolderNotCreatedYet_SaysSo_AndHasItsDrivesFreeSpace()
    {
        // A new install: the default clip folder appears with the first clip.
        _probe.Available(264 * Gb, total: 476 * Gb);

        var entry = _guard.DescribeCurrentRoot();

        Assert.Equal(Path.Combine(_directory.Path, "data", "clips"), entry["Root"]);
        Assert.Equal(false, entry["Exists"]);
        Assert.Equal("Ok", entry["FreeSpace"]);
        Assert.Equal(264 * Gb, entry["FreeBytes"]);
        Assert.Equal(476 * Gb, entry["TotalBytes"]);
        Assert.Equal(@"C:\", entry["Volume"]);
        Assert.Equal(2 * Gb, entry["MinFreeBytes"]);
        Assert.Equal(false, entry["LowFreeSpace"]);
        Assert.Equal(0, entry["PendingFileDeletes"]);
        Assert.Empty((IEnumerable<object>)entry["Warnings"]!);
        Assert.Equal(
            "The folder doesn't exist yet; the first clip creates it. Free space for clips: C:\\ has 264 GB free of 476 GB. Clips are refused when fewer than 2 GB would remain.",
            entry["Message"]);
    }

    [Fact]
    public async Task ClipStorage_ForARecordedClipFolderThatHoldsClipsButIsntThere_SaysNoClipIsMadeUntilItIsBack()
    {
        // The clip worker never makes such a folder again (an unplugged drive's, which it would make anew while
        // the free-space probe measures the folder above), so the line doesn't promise that the first clip creates it.
        var root = Path.Combine(_directory.Path, "data", "clips");
        await _store.AddClipRootAsync(root, Ct);
        await _store.AddAsync(TestData.CreateBookmark(Guid.NewGuid()) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = Path.Combine(root, "alice", "Heat (1995) [00.01.00-00.01.30].mkv") } }, Ct);
        _probe.Available(264 * Gb, total: 476 * Gb);

        var entry = _guard.DescribeCurrentRoot();

        Assert.Equal(false, entry["Exists"]);
        Assert.Equal(
            "The folder isn't there, although clips were made in it (an unplugged drive?): no clip is made until it is back. Free space for clips: C:\\ has 264 GB free of 476 GB. Clips are refused when fewer than 2 GB would remain.",
            entry["Message"]);
        var warning = Assert.Single((IEnumerable<IDictionary<string, object?>>)entry["Warnings"]!);
        Assert.Equal(
            ("FolderUnavailable", "This folder isn't there, although clips were made in it; clips are refused until it is back, and none of its clips is marked missing meanwhile. If it is gone for good, confirm it (its clips are then marked missing) or choose another clip folder."),
            (warning["Code"], warning["Message"]));

        // The warning names the folder's RootId, for the settings page's confirmation.
        Assert.Equal(ClipRootPaths.RootId(Path.GetFullPath(root)), warning["RootId"]);
    }

    [Fact]
    public async Task ClipStorage_ForTheDefaultClipFolderDeleted_SaysTheNextClipMakesItAgain_AndItsClipsAreMarkedMissing()
    {
        // Found in testing: the default folder in the plugin's data folder, which is there, isn't an unplugged drive.
        var data = _directory.Combine("data");
        Directory.CreateDirectory(data);
        var root = Path.Combine(data, "clips");
        await _store.AddClipRootAsync(root, Ct);
        await _store.AddAsync(TestData.CreateBookmark(Guid.NewGuid()) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = Path.Combine(root, "alice", "Heat (1995) [00.01.00-00.01.30].mkv") } }, Ct);
        _probe.Available(264 * Gb, total: 476 * Gb);

        var entry = _guard.DescribeCurrentRoot();

        Assert.StartsWith("The folder isn't there (deleted?), although clips were made in it: the next clip, or the daily maintenance, makes it again and marks its clips missing. Free space for clips:", (string)entry["Message"]!, StringComparison.Ordinal);
        var warning = Assert.Single((IEnumerable<IDictionary<string, object?>>)entry["Warnings"]!);
        Assert.Equal("FolderDeleted", warning["Code"]);
        Assert.False(warning.ContainsKey("RootId"));
    }

    [Fact]
    public async Task ClipStorage_ForAFolderThereThatTheClipWorkerDoesntTakeForItsClips_Warns_WithTheRemedy()
    {
        // Found in testing: an existing but unrecognised clip folder refuses every new clip, and the Clip folder field's
        // line said nothing. Without its marker: none of its clips there (not recognised), some there and some gone
        // (unproven); every one there is the backfill's, no warning.
        var root = _directory.Combine("data", "clips");
        Directory.CreateDirectory(Path.Combine(root, "alice"));
        await _store.AddClipRootAsync(root, Ct);
        var paths = new[] { "a", "b", "c" }.Select(n => Path.Combine(root, "alice", "Heat (1995) - " + n + " [00.01.00-00.01.30].mkv")).ToArray();
        foreach (var path in paths)
        {
            await _store.AddAsync(TestData.CreateBookmark(Guid.NewGuid()) with { Clip = new ClipInfo { Status = BookmarkClipStatus.Completed, FilePath = path } }, Ct);
        }

        _probe.Available(264 * Gb, total: 476 * Gb);
        var none = _guard.DescribeCurrentRoot();
        Assert.Equal(
            [("FolderUnrecognised", "This folder is there, but Bookmarks doesn't recognise it as the one its clips were made in: it has neither its marker file (.jfbm-clip-root) nor any of its 3 clip(s) (a disk or share that isn't mounted?). New clips are refused until it is; if its clips are gone for good, confirm the folder (they are then marked missing) or choose another clip folder.")],
            Warnings(none));
        Assert.Equal(ClipRootPaths.RootId(Path.GetFullPath(root)), ((IEnumerable<IDictionary<string, object?>>)none["Warnings"]!).Single()["RootId"]);

        await File.WriteAllBytesAsync(paths[0], [1], Ct);
        Assert.Equal(
            [("FolderUnrecognised", "This folder is there without its marker file (.jfbm-clip-root), and 2 of its 3 clip(s) are gone, so Bookmarks doesn't take it for the folder they were made in. New clips are refused until it is confirmed (the gone clips are then marked missing, and their owners can forget them on the My Clips page) or another clip folder is chosen.")],
            Warnings(_guard.DescribeCurrentRoot()));

        foreach (var path in paths)
        {
            await File.WriteAllBytesAsync(path, [1], Ct);
        }

        Assert.Empty(Warnings(_guard.DescribeCurrentRoot()));

        // With its marker (its token recorded), a folder whose clips are gone is the sweep's (AllMissing), not the field's.
        foreach (var path in paths)
        {
            File.Delete(path);
        }

        await ClipFileSupport.MarkClipFolderAsync(_store, root);
        Assert.Empty(Warnings(_guard.DescribeCurrentRoot()));
    }

    [Theory]
    [InlineData(3 * Gb, false)] // LowFreeSpace when free < the reserve + 1 GB
    [InlineData((3 * Gb) - 1, true)]
    public void ClipStorage_WarnsLowFreeSpace_BelowTheReservePlus1GB(long free, bool low)
    {
        Directory.CreateDirectory(_directory.Combine("data", "clips"));
        _probe.Available(free, volume: @"Q:\");

        var entry = _guard.DescribeCurrentRoot();

        Assert.Equal(true, entry["Exists"]);
        Assert.Equal(low, entry["LowFreeSpace"]);
        var warnings = Warnings(entry);
        if (low)
        {
            var (code, message) = Assert.Single(warnings);
            Assert.Equal("LowFreeSpace", code);
            Assert.Equal("Q:\\ has 3 GB free. Clips are refused when fewer than 2 GB would remain.", message);
        }
        else
        {
            Assert.Empty(warnings);
        }

        Assert.StartsWith("Free space for clips: Q:\\ has 3 GB free", (string)entry["Message"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipStorage_ForAFolderWhoseDriveIsntThere_WarnsFolderUnavailable()
    {
        _configuration.ClipOutputPath = @"R:\Clips";
        _probe.Unavailable();

        var entry = _guard.DescribeCurrentRoot();

        Assert.Equal(@"R:\Clips", entry["Root"]);
        Assert.Equal("Unavailable", entry["FreeSpace"]);
        Assert.Equal(false, entry["LowFreeSpace"]);
        var (code, message) = Assert.Single(Warnings(entry));
        Assert.Equal("FolderUnavailable", code);
        Assert.Equal("This folder's drive isn't available; clips are refused until it is.", message);
    }

    [Fact]
    public void ClipStorage_ForAFolderWhoseFreeSpaceCantBeRead_WarnsFreeSpaceUnknown()
    {
        _probe.Unknown(error: 5);

        var entry = _guard.DescribeCurrentRoot();

        Assert.Equal("Unknown", entry["FreeSpace"]);
        var (code, message) = Assert.Single(Warnings(entry));
        Assert.Equal("FreeSpaceUnknown", code);
        Assert.Equal("Free space can't be checked here; the guard is off for this folder. (Error 5.)", message);
    }

    [Fact]
    public void ClipStorage_OnAShareThatStoppedAnswering_AnswersOnceTheProbeTimeoutIsUp()
    {
        // Found in testing: on such a share the check whether the folder is there blocks as long as the probe, and it
        // ran on the request thread after the probe had been given up, so Diagnostics and the settings page's line waited
        // the timeout plus SMB's own. Both are given up together now.
        using var share = new ManualResetEventSlim();
        _probe.Answer(folder =>
        {
            share.Wait(HangsFor);
            return FakeDriveSpaceProbe.Ok(folder, 10 * Gb);
        });
        var files = new FakeClipFileSystem
        {
            FolderFailure = _ =>
            {
                share.Wait(HangsFor);
                return null;
            }
        };
        var guard = GuardOn(files);

        var watch = Stopwatch.StartNew();
        var entry = guard.DescribeCurrentRoot();
        var elapsed = watch.Elapsed;
        share.Set();

        Assert.True(elapsed < GivenUpBy, $"ClipStorage took {elapsed}.");
        Assert.Null(entry["Exists"]);
        Assert.Equal("Unknown", entry["FreeSpace"]);
        Assert.Equal("Free space for clips can't be checked.", entry["Message"]);
        var (code, message) = Assert.Single(Warnings(entry));
        Assert.Equal("FreeSpaceUnknown", code);
        Assert.Equal("Free space can't be checked here; the guard is off for this folder. (The check took too long.)", message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClipStorage_WhenOnlyTheFolderCheckDoesntAnswer_HasTheFreeSpace_AndExistsIsUnknown(bool throws)
    {
        // A folder check that doesn't answer in time, or throws (the real one never does): whether the folder is there isn't
        // known, so the line doesn't say "doesn't exist yet", and the rest of the entry is there.
        using var share = new ManualResetEventSlim();
        _probe.Available(264 * Gb, total: 476 * Gb);
        var files = new FakeClipFileSystem
        {
            FolderFailure = folder =>
            {
                if (throws)
                {
                    return new IOException("The network path was not found: " + folder);
                }

                share.Wait(HangsFor);
                return null;
            }
        };
        var guard = GuardOn(files);

        var watch = Stopwatch.StartNew();
        var entry = guard.DescribeCurrentRoot();
        var elapsed = watch.Elapsed;
        share.Set();

        Assert.True(elapsed < GivenUpBy, $"ClipStorage took {elapsed}.");
        Assert.Null(entry["Exists"]);
        Assert.Equal("Ok", entry["FreeSpace"]);
        Assert.Equal(264 * Gb, entry["FreeBytes"]);
        Assert.Empty(Warnings(entry));
        Assert.Equal(
            "Free space for clips: C:\\ has 264 GB free of 476 GB. Clips are refused when fewer than 2 GB would remain.",
            entry["Message"]);
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
    }

    [Fact]
    public void ClipStorage_ForAClipFolderSettingThatIsntAPath_WarnsFolderUnavailable()
    {
        _configuration.ClipOutputPath = "C:\\bad\0path";

        var entry = _guard.DescribeCurrentRoot();

        Assert.Equal("Unavailable", entry["FreeSpace"]);
        Assert.Equal("FolderUnavailable", Assert.Single(Warnings(entry)).Code);
        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public async Task ClipStorage_CountsTheJobsWaiting_AndTheFilesWaitingToBeDeleted()
    {
        await AddAsync(BookmarkClipStatus.Queued, 100 * Mb);
        await AddAsync(BookmarkClipStatus.Running, 200 * Mb);
        await AddAsync(BookmarkClipStatus.Queued, null);
        await AddAsync(BookmarkClipStatus.Completed, 900 * Mb);
        await _store.AddClipRootAsync(_directory.Path, Ct);
        await _store.AddPendingFileDeleteAsync(_directory.Combine("old.mkv"), Ct);

        var entry = _guard.DescribeCurrentRoot();

        var waiting = (IDictionary<string, object?>)entry["Waiting"]!;
        Assert.Equal(3, waiting["Jobs"]);
        Assert.Equal(300 * Mb, waiting["EstimatedBytes"]);
        Assert.Equal(1, entry["PendingFileDeletes"]);
    }

    // The fixture's guard with a given file system, and a probe timeout short enough for a test.
    // The two tests above: a probe or folder check that doesn't answer hangs for HangsFor, the guard gives up after
    // ProbeTimeout, and DescribeCurrentRoot must answer well before the hang ends (GivenUpBy). The fake probe that does
    // answer must get a thread-pool thread within ProbeTimeout: 200 ms was too short while the ffmpeg tests kept every core
    // busy during a whole-solution `dotnet test` (2 failures in 4 runs), so it is 4 s, for a 4-core CI runner running every
    // test assembly at once. The hangs end when each test does (share.Set()), so a long HangsFor costs nothing.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan HangsFor = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan GivenUpBy = TimeSpan.FromSeconds(30);

    private ClipStorageGuard GuardOn(IClipFileSystem files)
        => ClipFileSupport.Guard(_store, _probe, _configuration, _clock, _logger, BookmarkPaths.ForRoot(_directory.Combine("data")), ProbeTimeout, files);

    private static List<(string Code, string Message)> Warnings(IDictionary<string, object?> entry)
        => ((IEnumerable<IDictionary<string, object?>>)entry["Warnings"]!).Select(w => ((string)w["Code"]!, (string)w["Message"]!)).ToList();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(10, Ct);
        }
    }

    private async Task<Bookmark> AddAsync(BookmarkClipStatus status, long? estimate, bool tombstone = false)
    {
        var bookmark = TestData.CreateBookmark(Guid.NewGuid()) with
        {
            DeletedUtc = tombstone ? DateTime.UtcNow : null,
            Clip = new ClipInfo { Status = status, StartTicks = 0, EndTicks = 10 * TestData.OneSecond, EstimatedBytes = estimate, FilePath = tombstone ? _directory.Combine("kept.mkv") : null }
        };
        await _store.AddAsync(bookmark, Ct);
        return bookmark;
    }
}
