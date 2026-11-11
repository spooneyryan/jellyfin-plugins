using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;

/// <summary>
/// The checks that can change from minute to minute (at plan time, again at job start and at the commit
/// point): a session playing one of the unit's items (<c>move.playing</c>: <c>NowPlayingItem</c>,
/// <c>FullNowPlayingItem</c>, and <c>PlayState.MediaSourceId</c> for a version streamed through another item, as
/// Duplicates checks it), a file something has open (<c>move.in-use</c>, <see cref="FileLockProbe"/>), an item
/// or version partner another operation holds (<c>move.item-busy</c>, <see cref="LibraryOperationGate"/>), a running scan
/// (the warning <c>move.scan-running</c>), and whether the destination can be written (<c>move.not-writable</c>). At plan
/// time that is an access check that writes nothing (<see cref="IFolderWriteAccess"/>): the earlier dot-named probe file
/// changed its non-dot parent folder, whose watcher event started a <c>FileRefresher</c>. The probe
/// file stays for job start, inside step 3a's suppression scope (<c>DestinationWriteCheck.ProbeFile</c>); the plan-only
/// build (<c>MOVE_PREVIEW_ONLY</c>) leaves it and its writer out.
/// </summary>
internal sealed class MovePreflight
{
    /// <summary>The probe file's name prefix.</summary>
    public const string ProbePrefix = ".jflm-probe-";

    private readonly ISessionManager _sessions;
    private readonly LibraryOperationGate _gate;
    private readonly FileLockProbe _locks;
    private readonly IFolderWriteAccess _access;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovePreflight"/> class.
    /// </summary>
    /// <param name="sessions">The session manager.</param>
    /// <param name="gate">The operation gate.</param>
    /// <param name="locks">The lock probe.</param>
    /// <param name="access">The write check that writes nothing; by default <see cref="FolderWriteAccess"/>.</param>
    public MovePreflight(ISessionManager sessions, LibraryOperationGate gate, FileLockProbe locks, IFolderWriteAccess? access = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(locks);
        _sessions = sessions;
        _gate = gate;
        _locks = locks;
        _access = access ?? new FolderWriteAccess();
    }

    /// <summary>
    /// Gets an item's version partners (<c>LibraryOperationGate</c>): its linked alternate versions and the
    /// primary it is linked to.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Their ids.</returns>
    public static IEnumerable<Guid> VersionPartners(Video item)
    {
        ArgumentNullException.ThrowIfNull(item);
        foreach (var link in item.LinkedAlternateVersions ?? [])
        {
            if (link?.ItemId is { } id && id != Guid.Empty)
            {
                yield return id;
            }
        }

        if (Guid.TryParse(item.PrimaryVersionId, CultureInfo.InvariantCulture, out var primary) && primary != Guid.Empty)
        {
            yield return primary;
        }
    }

    /// <summary>
    /// Finds the session playing one of some items.
    /// </summary>
    /// <param name="itemIds">The items.</param>
    /// <returns>The session, or null.</returns>
    public SessionInfo? FindPlaying(IReadOnlySet<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        foreach (var session in _sessions.Sessions ?? [])
        {
            if (session is null)
            {
                continue;
            }

            var playing = new[]
            {
                session.NowPlayingItem?.Id,
                session.FullNowPlayingItem?.Id,
                Guid.TryParse(session.PlayState?.MediaSourceId, CultureInfo.InvariantCulture, out var source) ? source : null,
            };
            if (playing.Any(id => id is { } value && itemIds.Contains(value)))
            {
                return session;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the checks, adding their blockers and warnings to the unit.
    /// </summary>
    /// <param name="unit">The planned unit.</param>
    /// <param name="writeCheck">How the destination is checked: <see cref="DestinationWriteCheck.Access"/> at plan time;
    /// <c>DestinationWriteCheck.ProbeFile</c> only inside step 3a's suppression scope (never in the plan-only build).</param>
    /// <param name="probeId">Names the probe file (the job id); unused by the access check.</param>
    /// <param name="exceptOperationId">A job whose own busy entries don't count (a job re-checking itself), or null.</param>
    /// <param name="cancellationToken">Cancels the lock probe's waits.</param>
    /// <returns>A task.</returns>
    public async Task CheckAsync(MoveUnit unit, DestinationWriteCheck writeCheck, string probeId, string? exceptOperationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentException.ThrowIfNullOrEmpty(probeId);

        var ids = unit.Items.Select(i => i.OldId).ToHashSet();
        if (FindPlaying(ids) is { } session)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.Playing, "Playing on " + Device(session) + "; stop playback first."));
        }

        var busyIds = unit.AllIds.Concat(VersionPartners(unit.Primary));
        if (_gate.FindBusy(busyIds, exceptOperationId) is { } busy)
        {
            unit.Blockers.Add(new PlanIssue(MoveErrorCodes.ItemBusy, LibraryOperationGate.BusyMessage(busy.Owner)));
        }

        if (_gate.IsScanRunning)
        {
            unit.Warnings.Add(new PlanIssue(MoveErrorCodes.ScanRunning, "A library scan is running; the move waits until it ends."));
        }

        // A link the plan found (a move.reparse-point blocker) is never opened: nothing at or below one is probed.
        var files = unit.Entries.Where(e => !e.IsDirectory).Select(e => Path.Join(unit.SourceRoot, e.RelativePath)).Where(f => !unit.IsLinked(f)).ToList();
        var open = await _locks.FindOpenAsync(files, cancellationToken).ConfigureAwait(false);
        if (open.Count > 0)
        {
            unit.Blockers.Add(new PlanIssue(
                MoveErrorCodes.InUse,
                (open.Count == 1 ? Path.GetFileName(open[0]) + " is" : open.Count + " files are") + " open in another program (a player, a transcode, a scan); close it and try again.",
                open));
        }

        // A linked target (a move.reparse-point blocker) is never checked: the check would reach through the link.
        if (unit.IsLinked(unit.TargetDirectory))
        {
            return;
        }

        // The target's nearest existing folder, and an existing folder below it that gets files of its own (an episode's
        // metadata folder): each has its own ACL. A linked one is never checked.
        var folders = new List<string> { NearestExisting(unit.TargetDirectory) };
        folders.AddRange(unit.Renames
            .Select(r => Path.GetDirectoryName(r.To)!)
            .Where(d => PathRules.IsUnder(d, unit.TargetDirectory) && !unit.CreatedDirectories.Any(c => PathRules.SamePath(c, d)) && !unit.IsLinked(d))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var folder in folders)
        {
#if MOVE_PREVIEW_ONLY
            // The plan-only build's only check writes nothing: it has no probe file.
            var canWrite = writeCheck == DestinationWriteCheck.Access && MayAdd(unit, folder);
#else
            var canWrite = MayAdd(unit, folder) && (writeCheck != DestinationWriteCheck.ProbeFile || CanWriteProbeFile(folder, probeId));
#endif
            if (!canWrite)
            {
                unit.Blockers.Add(new PlanIssue(MoveErrorCodes.NotWritable, "Jellyfin can't write to " + folder + ".", [folder]));
            }
        }
    }

    // The deepest existing folder of the target (the target itself when it exists): a new or wrap folder doesn't yet.
    private static string NearestExisting(string target)
    {
        var folder = target;
        while (!Directory.Exists(folder) && Path.GetDirectoryName(folder) is { Length: > 0 } parent)
        {
            folder = parent;
        }

        return folder;
    }

#if !MOVE_PREVIEW_ONLY
    // Makes and deletes the probe file in a folder: job start only, inside step 3a's suppression scope. The move job's seam; not in
    // the plan-only build.
    private static bool CanWriteProbeFile(string folder, string probeId)
    {
        try
        {
            using (new FileStream(Path.Join(folder, ProbePrefix + probeId), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
#endif

    private static string Device(SessionInfo session)
    {
        var device = string.IsNullOrWhiteSpace(session.DeviceName) ? "a device" : session.DeviceName;
        return string.IsNullOrWhiteSpace(session.Client) ? device : device + " (" + session.Client + ")";
    }

    // What the commit adds to the folder: the renames into it (a folder unit's folder, a file set's files and trickplay
    // folder) and the folders made in it (an episode's metadata folder); a missing target is a new folder made in its
    // nearest existing parent.
    private bool MayAdd(MoveUnit unit, string folder)
    {
        var into = unit.Renames.Where(r => PathRules.SamePath(Path.GetDirectoryName(r.To), folder)).ToList();
        var makes = unit.CreatedDirectories.Any(c => PathRules.SamePath(Path.GetDirectoryName(c), folder));
        if (into.Count == 0)
        {
            return _access.CanAdd(folder, files: false, folders: true);
        }

        return _access.CanAdd(folder, into.Any(r => !r.IsDirectory), into.Any(r => r.IsDirectory) || makes);
    }
}
