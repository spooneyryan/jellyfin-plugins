using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// The stable codes of Move's errors, blockers and warnings: the ProblemDetails'
/// <c>code</c> extension of an error, and the <c>Code</c> of a plan's blocker or warning. A client tells them apart by
/// these, never by their words. "Feature disabled" is the kit's <c>FeatureDisabled</c> problem type (403), not a code.
/// </summary>
internal static class MoveErrorCodes
{
    /// <summary>The item can't be moved (its kind, an extra, a virtual or placeholder item, not a file, a library type).</summary>
    public const string NotEligible = "move.not-eligible";

    /// <summary>The destination isn't one the caller may choose (400): a bad path, a library they can't open, another type.</summary>
    public const string DestinationInvalid = "move.destination-invalid";

    /// <summary>Blocker: a target file or folder exists already (compared ignoring case); a move never overwrites.</summary>
    public const string Collision = "move.collision";

    /// <summary>Blocker: the destination, or a folder above it, is another movie's own folder.</summary>
    public const string IntoMovieFolder = "move.into-movie-folder";

    /// <summary>Blocker: an episode's destination is outside every series (and not a new series folder at a TV root).</summary>
    public const string EpisodeNeedsSeries = "move.episode-needs-series";

    /// <summary>Blocker: the folder that would move holds another item's media or a nested library folder.</summary>
    public const string ForeignMedia = "move.foreign-media";

    /// <summary>Blocker: a junction or symbolic link in the source or the destination.</summary>
    public const string ReparsePoint = "move.reparse-point";

    /// <summary>Blocker: a destination folder (existing, new, a wrap folder, a folder unit's own) named like an extras folder
    /// where Jellyfin ignores one (<c>trailers</c>, <c>extras</c>… below another folder).</summary>
    public const string ExtrasFolderName = "move.extras-folder-name";

    /// <summary>Blocker: a session plays the item (or one of its versions).</summary>
    public const string Playing = "move.playing";

    /// <summary>Blocker: another process has one of the unit's files or folders open.</summary>
    public const string InUse = "move.in-use";

    /// <summary>Blocker: not enough free space on the destination drive.</summary>
    public const string InsufficientSpace = "move.insufficient-space";

    /// <summary>Blocker in 1.1: the destination is on another drive; such moves come in a later version.</summary>
    public const string CrossVolumeLater = "move.cross-volume-later";

    /// <summary>Blocker: the destination folder can't be written, or its drive couldn't be identified.</summary>
    public const string NotWritable = "move.not-writable";

    /// <summary>The plan changed since it was shown (409).</summary>
    public const string PlanStale = "move.plan-stale";

    /// <summary>The plan is no longer cached (410).</summary>
    public const string PlanExpired = "move.plan-expired";

    /// <summary>Blocker and 409: Duplicates or another move job holds the item or a version partner.</summary>
    public const string ItemBusy = "move.item-busy";

    /// <summary>The queue is full (409).</summary>
    public const string QueueFull = "move.queue-full";

    /// <summary>A job past its commit point can't be cancelled (409).</summary>
    public const string PastCommit = "move.past-commit";

    /// <summary>The job isn't in a state that allows this (409).</summary>
    public const string JobState = "move.job-state";

    /// <summary>The job can't be rolled back any more (409).</summary>
    public const string RollbackUnavailable = "move.rollback-unavailable";

    /// <summary>A dismiss that loses restored state needs <c>confirm=true</c> (400).</summary>
    public const string ConfirmRequired = "move.confirm-required";

    /// <summary>The original place can't be restored (409).</summary>
    public const string MoveBackUnavailable = "move.moveback-unavailable";

    /// <summary>Moving media works on Windows only in this version (503).</summary>
    public const string UnsupportedOs = "move.unsupported-os";

    /// <summary>Warning: a library scan is running; a job waits for it.</summary>
    public const string ScanRunning = "move.scan-running";

    /// <summary>Warning: the episode goes into a new series folder.</summary>
    public const string NewSeries = "move.new-series";

    /// <summary>Warning: the episode moves to another series; its metadata follows the new one.</summary>
    public const string EpisodeOtherSeries = "move.episode-other-series";

    /// <summary>Warning: the episode's file name has no season number and the destination's season differs.</summary>
    public const string EpisodeOtherSeason = "move.episode-other-season";

    /// <summary>
    /// Warning: the episode's file name has a season number that differs from the destination season's, and the file
    /// name wins (the season comes from the file name; no other code fits it).
    /// </summary>
    public const string EpisodeSeasonFromName = "move.episode-season-from-name";

    /// <summary>Warning: a file that looks like a sidecar but is another video, or an extra in a shared folder, stays.</summary>
    public const string SidecarLeftBehind = "move.sidecar-left-behind";

    /// <summary>Warning (a job's result): images had to be fetched again.</summary>
    public const string ImagesRefetched = "move.images-refetched";

    /// <summary>Gets every code above, for the tests' pinned list.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NotEligible, DestinationInvalid, Collision, IntoMovieFolder, EpisodeNeedsSeries, ForeignMedia, ReparsePoint, ExtrasFolderName,
        Playing, InUse, InsufficientSpace, CrossVolumeLater, NotWritable, PlanStale, PlanExpired, ItemBusy, QueueFull, PastCommit,
        JobState, RollbackUnavailable, ConfirmRequired, MoveBackUnavailable, UnsupportedOs, ScanRunning, NewSeries,
        EpisodeOtherSeries, EpisodeOtherSeason, EpisodeSeasonFromName, SidecarLeftBehind, ImagesRefetched,
    ];
}
