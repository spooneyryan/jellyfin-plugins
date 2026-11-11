namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The state of the move journal file when it was read. Only <see cref="Ok"/> gives lines; in every other
/// state a consumer keeps its stored position and relinks nothing from the journal.
/// </summary>
internal enum MoveJournalStatus
{
    /// <summary>There is no journal: Library Maintenance isn't installed or has never moved anything.</summary>
    Missing,

    /// <summary>The file is empty, or holds only an unfinished first line (it is being created).</summary>
    Empty,

    /// <summary>The header was read; the Committed lines are available.</summary>
    Ok,

    /// <summary>
    /// The first line isn't a valid v1 header. Library Maintenance starts a new journal, with a new <c>JournalId</c>, when
    /// it finds one like this.
    /// </summary>
    InvalidHeader,

    /// <summary>The header's <c>V</c> is a version this kit doesn't know.</summary>
    UnsupportedVersion,

    /// <summary>The file couldn't be read (locked, no access, too large); it is tried again on the next read.</summary>
    Unreadable
}
