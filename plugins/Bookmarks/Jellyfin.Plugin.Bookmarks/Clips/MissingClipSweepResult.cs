using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What one run of the missing-clip sweep did (<see cref="MissingClipSweep"/>): for Diagnostics <c>ClipSweep</c> and
/// the maintenance step's result. Counts, and the clip folders the run left out with how many clips each holds and why
/// (admins only: Diagnostics), never a clip's name or path.
/// </summary>
internal sealed record MissingClipSweepResult
{
    /// <summary>Gets when the run started (UTC, on the lifecycle's clock).</summary>
    public required DateTime StartedUtc { get; init; }

    /// <summary>Gets how long it took.</summary>
    public long Milliseconds { get; init; }

    /// <summary>Gets why nothing was looked at (<c>ReadOnly</c>), or <c>null</c>.</summary>
    public string? Skipped { get; init; }

    /// <summary>Gets the retention in days the run used (0: never).</summary>
    public int RetentionDays { get; init; }

    /// <summary>Gets how many clips with a file were looked at (the clips being made not counted).</summary>
    public int Checked { get; init; }

    /// <summary>Gets how many of them have their file.</summary>
    public int Present { get; init; }

    /// <summary>Gets how many have lost their file from a clip folder that is there.</summary>
    public int Missing { get; init; }

    /// <summary>Gets how many of the missing ones were marked missing by this run.</summary>
    public int Marked { get; init; }

    /// <summary>Gets how many marked missing before have their file again (the mark cleared).</summary>
    public int Cleared { get; init; }

    /// <summary>Gets how many clips were being made (a job waiting or running): left as they are.</summary>
    public int InProgress { get; init; }

    /// <summary>Gets how many clips are in clip folders the run left out (not there, an unplugged drive; not recognised by
    /// their marker file; every clip gone at once): left as they are.</summary>
    public int FolderUnavailable { get; init; }

    /// <summary>Gets the clip folders the run left out, with how many clips each holds and why
    /// (<see cref="ClipFolderLeftOut"/>).</summary>
    public IReadOnlyDictionary<string, ClipFolderLeftOut> UnavailableFolders { get; init; } = new Dictionary<string, ClipFolderLeftOut>();

    /// <summary>Gets how many clip folders got their marker file in this run (<see cref="ClipRootMarker"/>: the backfill, the
    /// first time a folder's clips were found there and none gone; also a marker of an earlier build given its token,
    /// and the default clip folder made again).</summary>
    public int MarkersWritten { get; init; }

    /// <summary>Gets how many clip folders the run made again: the default clip folder, in the plugin's data folder,
    /// deleted while clips were recorded in it, made again with its marker file, its clips marked missing.</summary>
    public int FoldersMadeAgain { get; init; }

    /// <summary>Gets how many clip files couldn't be inspected (a folder the server may not read): left as they are.</summary>
    public int Unreadable { get; init; }

    /// <summary>Gets how many clip paths aren't ones Bookmarks may use (outside the clip folders, a reparse point on the way,
    /// not a clip file's name): left as they are.</summary>
    public int Refused { get; init; }

    /// <summary>Gets how many clips changed while the run looked at them (made again, deleted): left for the next run.</summary>
    public int Changed { get; init; }

    /// <summary>Gets how many deleted bookmarks' clips missing for the retention or longer moved into the archive.</summary>
    public int Archived { get; init; }

    /// <summary>Gets how many bookmarks' own clip entries missing for the retention or longer were cleared (the bookmarks
    /// stay).</summary>
    public int ClipsForgotten { get; init; }

    /// <summary>Gets the error of the retention, when it failed after the marks were saved, or <c>null</c>.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Describes the run with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["StartedUtc"] = StartedUtc,
        ["Milliseconds"] = Milliseconds,
        ["Skipped"] = Skipped,
        ["RetentionDays"] = RetentionDays,
        ["Checked"] = Checked,
        ["Present"] = Present,
        ["Missing"] = Missing,
        ["Marked"] = Marked,
        ["Cleared"] = Cleared,
        ["InProgress"] = InProgress,
        ["FolderUnavailable"] = FolderUnavailable,
        ["UnavailableFolders"] = UnavailableFolders
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Select(f => (IDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Folder"] = f.Key,
                ["Clips"] = f.Value.Clips,
                ["Reason"] = f.Value.Reason,

                // The id the admin's confirmation names the folder by, and whether it may.
                ["RootId"] = ClipRootPaths.RootId(f.Key),
                ["Confirmable"] = f.Value.Reason != ClipFolderLeftOut.MarkerNotWritten
            })
            .ToList(),
        ["MarkersWritten"] = MarkersWritten,
        ["FoldersMadeAgain"] = FoldersMadeAgain,
        ["Unreadable"] = Unreadable,
        ["Refused"] = Refused,
        ["Changed"] = Changed,
        ["Archived"] = Archived,
        ["ClipsForgotten"] = ClipsForgotten,
        ["Error"] = Error
    };
}
