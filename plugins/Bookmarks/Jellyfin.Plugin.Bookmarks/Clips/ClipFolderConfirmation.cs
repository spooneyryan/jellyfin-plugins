using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What a confirmation of a clip folder did, or would do in a dry run (<see cref="ClipFileOps.ConfirmFolderAsync"/>): the
/// folder taken for the one its clips were made in, its gone clips marked missing.
/// </summary>
/// <param name="Root">The folder, normalised.</param>
/// <param name="Existed">Whether it was there.</param>
/// <param name="Created">Whether it was made.</param>
/// <param name="Marker">What became of its marker file (<see cref="ClipRootMarking.Failed"/> in a dry run).</param>
/// <param name="Counts">Its clips: after the confirmation, or now in a dry run.</param>
/// <param name="Newly">How many clips were (or would be) marked missing now.</param>
/// <param name="Failure">Why nothing was marked (the folder couldn't be made, its marker couldn't be written), or
/// <c>null</c>.</param>
internal sealed record ClipFolderConfirmation(string Root, bool Existed, bool Created, ClipRootMarking Marker, ClipFolderClips Counts, int Newly, string? Failure)
{
    /// <summary>The failure of a folder that isn't there and couldn't be made.</summary>
    public const string NotMadeMessage = "The clip folder isn't there and couldn't be made (is its drive there? may the server write there?), so nothing was marked.";

    /// <summary>The failure of a folder whose marker file couldn't be written.</summary>
    public const string MarkerNotWrittenMessage = "The clip folder's marker file (.jfbm-clip-root) couldn't be written (let the server write in the folder), so nothing was marked.";

    /// <summary>
    /// Describes it with plain values, for the admin route's answer.
    /// </summary>
    /// <param name="dryRun">Whether it was a dry run.</param>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary(bool dryRun) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["DryRun"] = dryRun,
        ["Folder"] = Root,
        ["RootId"] = ClipRootPaths.RootId(Root),
        ["Existed"] = Existed,
        ["Created"] = Created,
        ["Marker"] = dryRun ? null : Marker.ToString(),
        ["Clips"] = Counts.Clips,
        ["Present"] = Counts.Present,
        ["Gone"] = Counts.Gone,
        ["MarkedBefore"] = Counts.Marked - (dryRun ? 0 : Newly),
        ["Marked"] = Newly
    };
}
