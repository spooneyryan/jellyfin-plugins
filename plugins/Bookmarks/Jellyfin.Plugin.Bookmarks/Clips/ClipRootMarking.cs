namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What <see cref="ClipFileOps.WriteRootMarkerAsync"/> did for a clip folder.
/// </summary>
internal enum ClipRootMarking
{
    /// <summary>Nothing: the marker couldn't be written or its token recorded (no permission, a read-only share, a store
    /// that can't be written), or the folder isn't recorded.</summary>
    Failed,

    /// <summary>The folder had its marker with the token recorded for it already: nothing was written.</summary>
    AlreadyMarked,

    /// <summary>The folder's marker names it with a token while none was recorded: that token was recorded, the file kept.</summary>
    Adopted,

    /// <summary>A marker file with a new token was written and the token recorded.</summary>
    Written
}
