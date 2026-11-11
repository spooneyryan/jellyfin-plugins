namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// How a <see cref="ClipFileOps"/> operation on a clip file ended.
/// </summary>
internal enum ClipFileOutcome
{
    /// <summary>The file was deleted or moved.</summary>
    Done,

    /// <summary>The file wasn't there. A delete counts this as success; a move fails.</summary>
    Missing,

    /// <summary>Windows refused because the file is open without delete sharing (a download, a player, ffprobe): a sharing
    /// or lock violation after every attempt. Nothing changed.</summary>
    InUse,

    /// <summary><see cref="ClipFileGuard"/> refused the path; nothing was touched.</summary>
    Refused,

    /// <summary>The operation failed for another reason (no permission, a drive that isn't ready, a destination that exists).
    /// Nothing changed.</summary>
    Failed,

    /// <summary>A delete kept the file because another record still names it (Bookmarks 1.1.3): the caller's record may let
    /// go of it, as after a delete, and the file stays for the other record. Logged as a Warning.</summary>
    NamedByAnotherRecord
}
