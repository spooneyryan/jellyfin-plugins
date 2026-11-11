using System.Globalization;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// One check of the free-space guard (the request's, or the one when a job starts): the verdict, and the numbers it was
/// made from, for the answer's wording and the log.
/// </summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Space">What the probe found.</param>
/// <param name="EstimatedBytes">The clip's estimated size, or <c>null</c> when it can't be estimated (counted as 0).</param>
/// <param name="ReservedBytes">The estimated sizes of the other clips counted against the space: waiting and being made
/// (the request's check), or being made (the check when a job starts).</param>
/// <param name="MinFreeBytes">The space that must stay free (<c>MinFreeSpaceMegabytes</c>).</param>
internal sealed record ClipSpaceCheck(ClipSpaceVerdict Verdict, DriveSpace Space, long? EstimatedBytes, long ReservedBytes, long MinFreeBytes)
{
    /// <summary>
    /// Gets a value indicating whether the clip is refused.
    /// </summary>
    public bool Refused => Verdict is ClipSpaceVerdict.LowSpace or ClipSpaceVerdict.Unavailable;

    /// <summary>
    /// Gets what a user who isn't an admin is told (no drive, path or numbers), which is also the error a job
    /// refused when it was about to start keeps: its owner reads it, whoever that is.
    /// </summary>
    public string UserMessage => Verdict == ClipSpaceVerdict.Unavailable ? ClipStorageGuard.UnavailableMessage : ClipStorageGuard.LowSpaceMessage;

    /// <summary>
    /// Gets what an admin is told (the drive and the numbers; an unavailable folder by its path).
    /// </summary>
    public string AdminMessage => Verdict switch
    {
        ClipSpaceVerdict.Unavailable => string.Format(
            CultureInfo.InvariantCulture,
            "The clip folder isn't available right now: {0} can't be reached (no folder above it exists, or its drive or share is gone). Clips are refused until it is back.",
            Space.Folder),
        ClipSpaceVerdict.LowSpace => "Not enough free space for this clip: " + Describe() + " (\"Keep free on the clip drive\" in the Bookmarks settings).",
        _ => string.Empty
    };

    /// <summary>
    /// Describes the numbers without a path, for the log and the admin's message: "C:\ has 1.2 GB free; this clip needs about
    /// 300 MB, the clips waiting or being made about 500 MB, and 2 GB must stay free".
    /// </summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        if (Verdict == ClipSpaceVerdict.Unavailable)
        {
            return Space.Error is int error
                ? string.Format(CultureInfo.InvariantCulture, "the clip folder's drive or share isn't there (error {0})", error)
                : "no folder at or above the clip folder exists (its drive or share isn't there)";
        }

        if (Space.Status != DriveSpaceStatus.Ok)
        {
            return Space.TimedOut
                ? "free space couldn't be read in time, so the guard is off for this folder"
                : string.Format(CultureInfo.InvariantCulture, "free space can't be read (error {0}), so the guard is off for this folder", Space.Error);
        }

        var needs = EstimatedBytes is long estimate
            ? "this clip needs about " + ClipStorageGuard.FormatSize(estimate)
            : "this clip's size can't be estimated";
        var others = ReservedBytes > 0 ? ", the other clips waiting or being made about " + ClipStorageGuard.FormatSize(ReservedBytes) : string.Empty;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} has {1} free; {2}{3}, and {4} must stay free",
            Space.VolumeName,
            ClipStorageGuard.FormatSize(Space.AvailableBytes ?? 0),
            needs,
            others,
            ClipStorageGuard.FormatSize(MinFreeBytes));
    }
}
