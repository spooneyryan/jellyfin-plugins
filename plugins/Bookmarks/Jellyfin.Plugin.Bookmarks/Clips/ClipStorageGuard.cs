using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The free-space guard for clip output (since 1.1.0): the checks when a clip is asked for and when its job starts, the
/// watchdog while ffmpeg writes, the disk-full wording of a failed job, and Diagnostics <c>ClipStorage</c> for the current
/// clip folder.
/// </summary>
/// <remarks>
/// <para><b>Probe.</b> <see cref="IDriveSpaceProbe"/> on the nearest existing folder, off the calling thread and given
/// up after <see cref="DefaultProbeTimeout"/> (a share that doesn't answer): a probe that takes longer is
/// <see cref="DriveSpaceStatus.Unknown"/>. Diagnostics' check whether the folder is there, which blocks on such a share
/// as long, runs beside it and is given up at the same moment.</para>
/// <para><b>The request's check</b> (<see cref="CheckBeforeQueueAsync"/>): refused when the available space minus the
/// clip's estimate minus the persisted estimates (<c>ClipInfo.EstimatedBytes</c>) of every other Queued or Running job is
/// below <c>MinFreeSpaceMegabytes</c>, or when the folder's drive isn't there. All queued jobs write under the current clip
/// folder, so they share its volume; a running job's whole estimate is counted although part of it is written already, which
/// is deliberately conservative. A folder whose free space can't be read switches the guard off for it.</para>
/// <para><b>When the job starts</b> (<see cref="CheckAtClaimAsync"/>): the same test before anything is written, with
/// the jobs being made at that moment counted but not the ones still waiting, which start after it and are checked then; a
/// refusal fails the job with the user's wording.</para>
/// <para><b>Watchdog</b> (<see cref="WatchAsync"/>): every <see cref="DefaultWatchdogInterval"/> while ffmpeg runs,
/// with <c>MinFreeSpaceMegabytes</c> read again each time; below half of it the job is stopped
/// (<see cref="ClipStopReason.LowDiskSpace"/>) and fails with <see cref="AlmostFullMessage"/>.</para>
/// <para><b>Disk full</b> (<see cref="DescribeFailureAsync"/>): "No space left on device" in ffmpeg's error lines,
/// ffmpeg's exit code -28, or an <see cref="IOException"/> with <c>0x80070070</c> or <c>0x80070027</c> (reserving the
/// output, moving the finished clip into place) make the job's error <see cref="DriveFullMessage"/>; so does any other
/// failure after the job wrote its output when a probe then finds less than half the reserve left (a full disk reported
/// as <c>ERROR_HANDLE_DISK_FULL</c> reads "Invalid argument" in ffmpeg, as measured on Windows).</para>
/// <para>Users' messages name no drive, path or number; admins' name the drive and the numbers. Log lines name
/// volumes and ids only.</para>
/// </remarks>
internal sealed class ClipStorageGuard
{
    /// <summary>One megabyte, as <c>MinFreeSpaceMegabytes</c> counts it.</summary>
    public const long Megabyte = 1024L * 1024;

    /// <summary>One gigabyte, as the messages count it.</summary>
    public const long Gigabyte = 1024L * Megabyte;

    /// <summary>
    /// The margin above the reserve under which the clip folder's line warns (<c>LowFreeSpace</c>: free &lt; the
    /// reserve + 1 GB).
    /// </summary>
    public const long LowFreeSpaceMargin = Gigabyte;

    /// <summary>ffmpeg's exit code for <c>AVERROR(ENOSPC)</c>, a full disk.</summary>
    public const int FfmpegNoSpaceExitCode = -28;

    /// <summary>ffmpeg's text for a full disk on Windows.</summary>
    public const string NoSpaceText = "No space left on device";

    /// <summary>The 503 message a user gets when the space would run short.</summary>
    public const string LowSpaceMessage = "Not enough free space on the server for this clip. Ask your server admin.";

    /// <summary>The 503 message a user gets when the clip folder's drive isn't there.</summary>
    public const string UnavailableMessage = "The clip folder isn't available right now. Ask your server admin.";

    /// <summary>The error of a job the watchdog stopped.</summary>
    public const string AlmostFullMessage = "Stopped: the clip drive is almost full.";

    /// <summary>The error of a job that failed on a full disk.</summary>
    public const string DriveFullMessage = "The clip drive is full.";

    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    private readonly IDriveSpaceProbe _probe;
    private readonly IBookmarkStore _store;
    private readonly IPluginConfigurationProvider _configurationProvider;
    private readonly BookmarkPaths _paths;
    private readonly IClipFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _probeTimeout;
    private readonly ConcurrentDictionary<string, bool> _guardOffLogged = new(ClipRootPaths.Comparer);

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipStorageGuard"/> class.
    /// </summary>
    /// <param name="probe">The free-space probe.</param>
    /// <param name="store">The bookmark store: the estimates of the jobs waiting or being made, and the files waiting to be
    /// deleted.</param>
    /// <param name="configurationProvider">The settings: <c>MinFreeSpaceMegabytes</c> and the clip folder.</param>
    /// <param name="paths">The plugin's paths, for the default clip folder.</param>
    /// <param name="logger">The logger.</param>
    public ClipStorageGuard(
        IDriveSpaceProbe probe,
        IBookmarkStore store,
        IPluginConfigurationProvider configurationProvider,
        BookmarkPaths paths,
        ILogger<ClipStorageGuard> logger)
        : this(probe, store, configurationProvider, paths, logger, PhysicalClipFileSystem.Instance, TimeProvider.System, DefaultWatchdogInterval, DefaultProbeTimeout)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipStorageGuard"/> class with a given clock and file system (tests).
    /// </summary>
    /// <param name="probe">The free-space probe.</param>
    /// <param name="store">The bookmark store.</param>
    /// <param name="configurationProvider">The settings.</param>
    /// <param name="paths">The plugin's paths.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="fileSystem">Whether the clip folder is there, for Diagnostics.</param>
    /// <param name="timeProvider">The watchdog's clock.</param>
    /// <param name="watchdogInterval">The time between two of the watchdog's probes.</param>
    /// <param name="probeTimeout">How long a probe may take.</param>
    internal ClipStorageGuard(
        IDriveSpaceProbe probe,
        IBookmarkStore store,
        IPluginConfigurationProvider configurationProvider,
        BookmarkPaths paths,
        ILogger logger,
        IClipFileSystem fileSystem,
        TimeProvider timeProvider,
        TimeSpan watchdogInterval,
        TimeSpan probeTimeout)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configurationProvider);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _probe = probe;
        _store = store;
        _configurationProvider = configurationProvider;
        _paths = paths;
        _logger = logger;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
        WatchdogInterval = watchdogInterval;
        _probeTimeout = probeTimeout;
    }

    /// <summary>
    /// Gets the watchdog's interval: 5 seconds.
    /// </summary>
    public static TimeSpan DefaultWatchdogInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets how long a probe may take before it counts as <see cref="DriveSpaceStatus.Unknown"/>: 10 seconds
    /// (measured on Windows: an unreachable share can block much longer).
    /// </summary>
    public static TimeSpan DefaultProbeTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the time between two of the watchdog's probes.
    /// </summary>
    public TimeSpan WatchdogInterval { get; }

    /// <summary>
    /// Gets the space that must stay free, in bytes: <c>MinFreeSpaceMegabytes</c>, 0 for a negative value.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The reserve.</returns>
    public static long MinFreeBytes(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Math.Max(0L, configuration.MinFreeSpaceMegabytes) * Megabyte;
    }

    /// <summary>
    /// Formats a size for messages: "0.8 GB", "2 GB", "264.5 GB" from a tenth of a gigabyte, "37 MB" or "1.5 MB" from a
    /// megabyte, "465 KB" below (a short clip's estimate).
    /// </summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The text.</returns>
    public static string FormatSize(long bytes)
    {
        bytes = Math.Max(0, bytes);
        return bytes switch
        {
            >= Gigabyte / 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)Gigabyte:0.#} GB"),
            >= Megabyte => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)Megabyte:0.#} MB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB")
        };
    }

    /// <summary>
    /// Says why a failure is a full disk, from the failure alone: ffmpeg's exit code -28 or "No space left on device" in its
    /// error lines, or an <see cref="IOException"/> with <c>ERROR_DISK_FULL</c> or <c>ERROR_HANDLE_DISK_FULL</c>, on
    /// the exception or one inside it.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns>What gave it away, for the log; <c>null</c> when nothing does.</returns>
    public static string? DiskFullSignal(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is ClipExtractionException { ExitCode: FfmpegNoSpaceExitCode })
            {
                return "ffmpeg's exit code -28";
            }

            if (e is ClipExtractionException && e.Message.Contains(NoSpaceText, StringComparison.OrdinalIgnoreCase))
            {
                return "ffmpeg's \"" + NoSpaceText + "\"";
            }

            if (e is IOException && IsDiskFull(e.HResult))
            {
                return string.Create(CultureInfo.InvariantCulture, $"0x{e.HResult:X8}");
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an <c>HResult</c> is a full disk: <c>0x80070070</c> (<c>ERROR_DISK_FULL</c>) or <c>0x80070027</c>
    /// (<c>ERROR_HANDLE_DISK_FULL</c>).
    /// </summary>
    /// <param name="hresult">The <c>HResult</c>.</param>
    /// <returns>Whether it is one.</returns>
    public static bool IsDiskFull(int? hresult) => hresult is DiskFull or HandleDiskFull;

    /// <summary>
    /// Probes a folder off the calling thread, giving up after the probe timeout (<see cref="DriveSpaceStatus.Unknown"/>,
    /// <see cref="DriveSpace.TimedOut"/>).
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>What was found.</returns>
    public async Task<DriveSpace> ProbeAsync(string folder, CancellationToken cancellationToken)
    {
        var probing = Task.Run(() => SafeGet(folder), CancellationToken.None);
        try
        {
            return await probing.WaitAsync(_probeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return TimedOut(folder);
        }
    }

    /// <summary>
    /// The request's check, after the clip was planned and before it is queued. A refusal is logged at Information with
    /// the numbers, a folder whose free space can't be read once as a Warning.
    /// </summary>
    /// <param name="outputFolder">The folder the clip will be written to (the user's folder in the clip folder, which need not
    /// exist yet).</param>
    /// <param name="estimatedBytes">The clip's estimated size.</param>
    /// <param name="bookmarkId">The bookmark; its own job, if one is waiting, isn't counted twice.</param>
    /// <param name="cancellationToken">Stops waiting for the probe.</param>
    /// <returns>The check.</returns>
    public async Task<ClipSpaceCheck> CheckBeforeQueueAsync(string outputFolder, long? estimatedBytes, Guid bookmarkId, CancellationToken cancellationToken)
    {
        var check = await CheckAsync(outputFolder, estimatedBytes, EstimatedBytesOfOtherJobs(bookmarkId, includeQueued: true), cancellationToken).ConfigureAwait(false);
        if (check.Refused)
        {
            _logger.LogInformation("Refused a clip of bookmark {BookmarkId}: {Reason}", bookmarkId, check.Describe());
        }

        return check;
    }

    /// <summary>
    /// The check when a job starts, before its clip folder is recorded or anything is written. A refusal is
    /// logged as a Warning with the numbers.
    /// </summary>
    /// <param name="outputFolder">The folder the clip will be written to.</param>
    /// <param name="estimatedBytes">The clip's estimated size.</param>
    /// <param name="bookmarkId">The bookmark.</param>
    /// <param name="cancellationToken">Stops waiting for the probe.</param>
    /// <returns>The check.</returns>
    public async Task<ClipSpaceCheck> CheckAtClaimAsync(string outputFolder, long? estimatedBytes, Guid bookmarkId, CancellationToken cancellationToken)
    {
        var check = await CheckAsync(outputFolder, estimatedBytes, EstimatedBytesOfOtherJobs(bookmarkId, includeQueued: false), cancellationToken).ConfigureAwait(false);
        if (check.Refused)
        {
            _logger.LogWarning("The clip of bookmark {BookmarkId} was refused when it was about to start: {Reason}", bookmarkId, check.Describe());
        }

        return check;
    }

    /// <summary>
    /// The watchdog: every <see cref="WatchdogInterval"/> until <paramref name="cancellationToken"/> is cancelled, it
    /// reads <c>MinFreeSpaceMegabytes</c> again and probes the folder; with less than half of it available it stops the
    /// job (<see cref="ClipStopReason.LowDiskSpace"/>) and returns. A folder whose free space can't be read is skipped
    /// (the guard is off for it). Never throws.
    /// </summary>
    /// <param name="outputFolder">The folder the clip is written to.</param>
    /// <param name="job">The job to stop.</param>
    /// <param name="bookmarkId">The bookmark, for the log.</param>
    /// <param name="cancellationToken">Ends the watch: the job finished or was stopped.</param>
    /// <returns>A task that completes when the watch ends.</returns>
    public async Task WatchAsync(string outputFolder, RunningClip job, Guid bookmarkId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            using var timer = new PeriodicTimer(WatchdogInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var min = MinFreeBytes(_configurationProvider.GetConfiguration());
                if (min <= 0)
                {
                    continue;
                }

                var space = await ProbeAsync(outputFolder, cancellationToken).ConfigureAwait(false);
                if (space is { Status: DriveSpaceStatus.Ok, AvailableBytes: long available } && available < min / 2)
                {
                    _logger.LogWarning("Stopped the clip of bookmark {BookmarkId}: {Reason}", bookmarkId, BelowHalf(space.VolumeName, available, min));
                    job.Stop(ClipStopReason.LowDiskSpace);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished, was cancelled, or the server is stopping.
        }
    }

    /// <summary>
    /// The disk-full wording of a failed job: <see cref="DriveFullMessage"/> when the failure says so
    /// (<see cref="DiskFullSignal"/>), or, for a job that wrote its output, when a probe of its folder then finds less
    /// than half of <c>MinFreeSpaceMegabytes</c> available. Call it before the job's <c>.partial</c> is deleted, which
    /// gives the space back. The reason is logged as a Warning.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="outputFolder">The job's folder, when it was planned.</param>
    /// <param name="wroteOutput">Whether the job reserved its output (so ffmpeg or the final move failed).</param>
    /// <param name="bookmarkId">The bookmark, for the log.</param>
    /// <returns>The job's error, or <c>null</c> when the failure isn't a full disk.</returns>
    public async Task<string?> DescribeFailureAsync(Exception exception, string? outputFolder, bool wroteOutput, Guid bookmarkId)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (DiskFullSignal(exception) is { } signal)
        {
            _logger.LogWarning("The clip of bookmark {BookmarkId} failed: the clip drive is full ({Signal})", bookmarkId, signal);
            return DriveFullMessage;
        }

        var min = MinFreeBytes(_configurationProvider.GetConfiguration());
        if (!wroteOutput || outputFolder is null || min <= 0)
        {
            return null;
        }

        var space = await ProbeAsync(outputFolder, CancellationToken.None).ConfigureAwait(false);
        if (space is { Status: DriveSpaceStatus.Ok, AvailableBytes: long available } && available < min / 2)
        {
            _logger.LogWarning("The clip of bookmark {BookmarkId} failed: the clip drive is full ({Reason})", bookmarkId, BelowHalf(space.VolumeName, available, min));
            return DriveFullMessage;
        }

        return null;
    }

    /// <summary>
    /// Describes the current clip folder for Diagnostics <c>ClipStorage</c>: <c>Root</c>, <c>Exists</c>,
    /// <c>FreeSpace</c> (the probe's outcome), <c>ProbedPath</c>, <c>Volume</c>, <c>FreeBytes</c>, <c>TotalBytes</c>,
    /// <c>MinFreeBytes</c>, <c>LowFreeSpace</c> (free &lt; the reserve + 1 GB), <c>Waiting</c> (the jobs waiting or
    /// being made and their estimates), <c>PendingFileDeletes</c>, <c>Message</c> (the line the settings page shows under the
    /// Clip folder field) and <c>Warnings</c> (<c>LowFreeSpace</c>, <c>FolderUnavailable</c>, <c>FreeSpaceUnknown</c>, each
    /// with its message). It answers within <see cref="DefaultProbeTimeout"/> even for a share that stopped answering: the
    /// probe and the folder check run off the request thread and are given up together, the probe as <c>Unknown</c> (the
    /// check took too long), the folder check as <c>Exists: null</c> (not known).
    /// </summary>
    /// <returns>The entry.</returns>
    public IDictionary<string, object?> DescribeCurrentRoot()
    {
        var configuration = _configurationProvider.GetConfiguration();
        var min = MinFreeBytes(configuration);
        var warnings = new List<IDictionary<string, object?>>();
        var (waitingJobs, waitingBytes) = Waiting();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["MinFreeBytes"] = min,
            ["Waiting"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Jobs"] = waitingJobs, ["EstimatedBytes"] = waitingBytes },
            ["PendingFileDeletes"] = _store.PendingFileDeletes.Count,
            ["Warnings"] = warnings
        };

        if (!ClipRootPaths.TryGetCurrent(configuration, _paths, out var root))
        {
            result["Root"] = configuration.ClipOutputPath;
            result["Exists"] = false;
            result["FreeSpace"] = nameof(DriveSpaceStatus.Unavailable);
            result["LowFreeSpace"] = false;
            result["Message"] = ClipRootPaths.InvalidFolderMessage;
            warnings.Add(Warning("FolderUnavailable", ClipRootPaths.InvalidFolderMessage + " Clips are refused until it is one."));
            return result;
        }

        var (space, exists) = ProbeAndCheckWithin(root);
        var low = space is { Status: DriveSpaceStatus.Ok, AvailableBytes: long free } && free < min + LowFreeSpaceMargin;
        result["Root"] = root;
        result["Exists"] = exists;
        result["FreeSpace"] = space.Status.ToString();
        result["ProbedPath"] = space.ProbedPath;
        result["Volume"] = space.Volume;
        result["FreeBytes"] = space.AvailableBytes;
        result["TotalBytes"] = space.TotalBytes;
        result["LowFreeSpace"] = low;

        // A folder that isn't there although clips were made in it (an unplugged drive) isn't made again by a
        // clip (the clip worker refuses), so the line doesn't say the first clip creates it. The default folder in the plugin's
        // data folder, which is there, is deleted rather than away: the next clip, or the daily maintenance, makes it
        // again and marks its clips missing.
        var holds = exists == false && HoldsRecordedClips(root);
        var deleted = holds && IsDeletedDefaultFolder(root);
        var away = holds && !deleted;
        var notYet = exists == false && space.Status == DriveSpaceStatus.Ok
            ? away ? "The folder isn't there, although clips were made in it (an unplugged drive?): no clip is made until it is back. "
                : deleted ? "The folder isn't there (deleted?), although clips were made in it: the next clip, or the daily maintenance, makes it again and marks its clips missing. "
                : "The folder doesn't exist yet; the first clip creates it. "
            : string.Empty;
        if (away)
        {
            warnings.Add(Warning(
                "FolderUnavailable",
                "This folder isn't there, although clips were made in it; clips are refused until it is back, and none of its clips is marked missing meanwhile. If it is gone for good, confirm it (its clips are then marked missing) or choose another clip folder.",
                root));
        }
        else if (deleted)
        {
            warnings.Add(Warning(
                "FolderDeleted",
                "This folder, the default one in the server's data folder, isn't there although clips were made in it, so it was deleted: the next clip, or the daily maintenance, makes it again and marks its clips missing."));
        }
        else if (exists == true && UnrecognisedWithin(root) is { } unrecognised)
        {
            // A folder there that the clip worker refuses, as it isn't proven to be its clips' own.
            warnings.Add(Warning("FolderUnrecognised", unrecognised, root));
        }

        switch (space.Status)
        {
            case DriveSpaceStatus.Ok:
                result["Message"] = notYet + string.Format(
                    CultureInfo.InvariantCulture,
                    "Free space for clips: {0} has {1} free of {2}. {3}",
                    space.VolumeName,
                    FormatSize(space.AvailableBytes ?? 0),
                    FormatSize(space.TotalBytes ?? 0),
                    min > 0
                        ? "Clips are refused when fewer than " + FormatSize(min) + " would remain."
                        : "No space is kept free (the setting is 0), so clips are refused only when they wouldn't fit.");
                if (low)
                {
                    warnings.Add(Warning(
                        "LowFreeSpace",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} has {1} free. Clips are refused when fewer than {2} would remain.",
                            space.VolumeName,
                            FormatSize(space.AvailableBytes ?? 0),
                            FormatSize(min))));
                }

                break;
            case DriveSpaceStatus.Unavailable:
                result["Message"] = "The clip folder can't be reached.";
                warnings.Add(Warning("FolderUnavailable", "This folder's drive isn't available; clips are refused until it is."));
                break;
            default:
                result["Message"] = "Free space for clips can't be checked.";
                warnings.Add(Warning(
                    "FreeSpaceUnknown",
                    "Free space can't be checked here; the guard is off for this folder."
                        + (space.TimedOut ? " (The check took too long.)" : space.Error is int error ? string.Create(CultureInfo.InvariantCulture, $" (Error {error}.)") : string.Empty)));
                break;
        }

        return result;
    }

    // "C:\ has 0.9 GB free, less than half of the 2 GB that must stay free": the watchdog's and the re-probe's reason, one
    // string for the log (Jellyfin's log quotes each string value).
    private static string BelowHalf(string volume, long available, long min)
        => string.Format(CultureInfo.InvariantCulture, "{0} has {1} free, less than half of the {2} that must stay free", volume, FormatSize(available), FormatSize(min));

    // A warning of the clip folder's line; one an admin can answer by confirming the folder names its RootId, for the
    // settings page's button.
    private static IDictionary<string, object?> Warning(string code, string message, string? confirmableRoot = null)
    {
        var warning = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Code"] = code, ["Message"] = message };
        if (confirmableRoot is not null)
        {
            warning["RootId"] = ClipRootPaths.RootId(confirmableRoot);
        }

        return warning;
    }

    private static DriveSpace TimedOut(string folder)
        => new() { Status = DriveSpaceStatus.Unknown, Folder = folder, TimedOut = true };

    private static long AddSaturating(long total, long value)
        => value > 0 && total > long.MaxValue - value ? long.MaxValue : total + Math.Max(0, value);

    private async Task<ClipSpaceCheck> CheckAsync(string outputFolder, long? estimatedBytes, long reservedBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputFolder);
        var min = MinFreeBytes(_configurationProvider.GetConfiguration());
        var space = await ProbeAsync(outputFolder, cancellationToken).ConfigureAwait(false);
        var needed = Math.Max(0, estimatedBytes ?? 0);
        var verdict = space.Status switch
        {
            DriveSpaceStatus.Unavailable => ClipSpaceVerdict.Unavailable,
            DriveSpaceStatus.Ok when AddSaturating(AddSaturating(min, needed), reservedBytes) > space.AvailableBytes => ClipSpaceVerdict.LowSpace,
            DriveSpaceStatus.Ok => ClipSpaceVerdict.Ok,
            _ => ClipSpaceVerdict.GuardOff
        };

        var check = new ClipSpaceCheck(verdict, space, estimatedBytes, reservedBytes, min);
        if (verdict == ClipSpaceVerdict.GuardOff && _guardOffLogged.TryAdd(space.ProbedPath ?? space.Folder, true))
        {
            _logger.LogWarning("The clip folder's free space can't be checked on {Volume}: {Reason}", space.VolumeName, check.Describe());
        }

        return check;
    }

    // The persisted estimates (ClipInfo.EstimatedBytes) of the other live records' jobs being made and, for the request's
    // check, waiting. A tombstone never has a job.
    private long EstimatedBytesOfOtherJobs(Guid bookmarkId, bool includeQueued)
    {
        var total = 0L;
        foreach (var record in _store.GetAll())
        {
            if (!record.Id.Equals(bookmarkId)
                && BookmarkRecordRules.IsLive(record)
                && record.Clip is { } clip
                && (clip.Status == BookmarkClipStatus.Running || (includeQueued && clip.Status == BookmarkClipStatus.Queued)))
            {
                total = AddSaturating(total, clip.EstimatedBytes ?? 0);
            }
        }

        return total;
    }

    // Whether a clip folder that isn't there is the default one in the plugin's data folder, which is there (deleted,
    // not away), as ClipFileOps.DeletedDefaultFolder decides.
    private bool IsDeletedDefaultFolder(string root)
    {
        if (!ClipRootPaths.TryNormalize(_paths.DefaultClipsDirectory, out var defaultRoot) || !ClipRootPaths.Comparer.Equals(defaultRoot, root))
        {
            return false;
        }

        try
        {
            return _fileSystem.FolderExists(_paths.Root) && _fileSystem.GetAttributes(root) is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not look at the default clip folder");
            return false;
        }
    }

    // The sentence for a recorded clip folder that is there and holds recorded clips, but that the clip worker doesn't take for
    // theirs: no marker file with the token recorded for it, and not all of its clips there (none of them: not
    // recognised; some gone: unproven). Null for one it takes, and when the look didn't answer within the probe's time
    // (a share that stopped answering), like the probe.
    private string? UnrecognisedWithin(string root)
    {
        if (!HoldsRecordedClips(root))
        {
            return null;
        }

        var looking = Task.Run<string?>(
            () =>
            {
                if (ClipFileOps.CheckRoot(_store, _fileSystem, root, _logger) != ClipRootState.Unmarked)
                {
                    return null;
                }

                var clips = ClipFileOps.CountClips(_store, _fileSystem, root);
                if (clips.Present == 0)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "This folder is there, but Bookmarks doesn't recognise it as the one its clips were made in: it has neither its marker file (.jfbm-clip-root) nor any of its {0} clip(s) (a disk or share that isn't mounted?). New clips are refused until it is; if its clips are gone for good, confirm the folder (they are then marked missing) or choose another clip folder.",
                        clips.Clips);
                }

                return clips.Gone == 0
                    ? null
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "This folder is there without its marker file (.jfbm-clip-root), and {0} of its {1} clip(s) are gone, so Bookmarks doesn't take it for the folder they were made in. New clips are refused until it is confirmed (the gone clips are then marked missing, and their owners can forget them on the My Clips page) or another clip folder is chosen.",
                        clips.Gone,
                        clips.Clips);
            },
            CancellationToken.None);
        try
        {
            return looking.Wait(_probeTimeout) ? looking.Result : null;
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "Could not look at whether the clip folder is recognised");
            return null;
        }
    }

    // Whether a clip folder is recorded (ClipRoots) and holds a clip file of a record, live or a tombstone's.
    private bool HoldsRecordedClips(string root)
        => _store.ClipRoots.Any(r => ClipRootPaths.TryNormalize(r, out var recorded) && ClipRootPaths.Comparer.Equals(recorded, root))
            && _store.GetAll().Any(b => ClipFileGuard.TryGetFullPath(b.Clip?.FilePath, out var full) && full.Length > root.Length && ClipRootPaths.IsUnder(full, root));

    private (int Jobs, long EstimatedBytes) Waiting()
    {
        int jobs = 0;
        var bytes = 0L;
        foreach (var record in _store.GetAll())
        {
            if (BookmarkRecordRules.IsLive(record) && record.Clip is { Status: BookmarkClipStatus.Queued or BookmarkClipStatus.Running } clip)
            {
                jobs++;
                bytes = AddSaturating(bytes, clip.EstimatedBytes ?? 0);
            }
        }

        return (jobs, bytes);
    }

    // For Diagnostics, whose sources are synchronous: the probe and the check whether the folder is there, each on another
    // thread (on a share that stopped answering both block for as long as SMB waits), waited for together up to the
    // probe timeout. A probe that hasn't answered by then is Unknown (TimedOut); a folder check, null: not known.
    private (DriveSpace Space, bool? Exists) ProbeAndCheckWithin(string folder)
    {
        var probing = Task.Run(() => SafeGet(folder), CancellationToken.None);
        var checking = Task.Run(() => SafeFolderExists(folder), CancellationToken.None);
        Task.WaitAll([probing, checking], _probeTimeout);
        return (
            probing.IsCompletedSuccessfully ? probing.Result : TimedOut(folder),
            checking.IsCompletedSuccessfully ? checking.Result : null);
    }

    private bool? SafeFolderExists(string folder)
    {
        try
        {
            return _fileSystem.FolderExists(folder);
        }
        catch (Exception ex)
        {
            // The real check never throws (any error is "not there"); if one does anyway, whether the folder is there isn't
            // known, and the rest of Diagnostics still answers. The message can name the folder: at Debug only.
            _logger.LogDebug(ex, "Could not check whether the clip folder is there");
            return null;
        }
    }

    private DriveSpace SafeGet(string folder)
    {
        try
        {
            return _probe.Get(folder);
        }
        catch (Exception ex)
        {
            // The probe doesn't throw; if a call it makes does anyway, whatever it is, free space is unknown here: an
            // exception escaping the pool thread would fail a clip request, a job or the whole Diagnostics body instead. The
            // message can name the folder, so it is at Debug only.
            _logger.LogWarning("The free-space probe failed ({Type}); free space is unknown for this check", ex.GetType().Name);
            _logger.LogDebug(ex, "The free-space probe failed");
            return new DriveSpace { Status = DriveSpaceStatus.Unknown, Folder = folder, Error = ex.HResult };
        }
    }
}
