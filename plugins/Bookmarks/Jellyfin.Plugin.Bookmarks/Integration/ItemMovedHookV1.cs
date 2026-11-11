using System;
using System.Threading;

namespace Jellyfin.Plugin.Bookmarks.Integration;

/// <summary>
/// The hook Library Maintenance calls right after it appends Committed lines to its move journal:
/// Bookmarks catches up with the journal at once instead of at its next start, so bookmarks follow their moved
/// videos (Exact relinks; clip files are never moved).
/// </summary>
/// <remarks>
/// <para>A cross-plugin facade: <c>public static</c>, BCL types only, located by Library Maintenance through
/// <c>AssemblyLoadContext.All</c> by the exact assembly name <c>Jellyfin.Plugin.Bookmarks</c>. Static classes are abstract
/// and sealed in IL, so Jellyfin never creates one.</para>
/// <para><b>The journal stays the source of truth.</b> The hook only asks Bookmarks' identity service for a catch-up from
/// its stored position (<c>MoveJournalId</c>, <c>LastMoveSeq</c>); the text it is given is not read, so a hook call that
/// is lost, early or repeated changes nothing: the service catches up at every start too, and a catch-up is idempotent.
/// Before the identity service runs (the startup gate), while Bookmarks' data is read-only, and on a Jellyfin line this
/// build isn't for, the call does nothing. It never throws and never blocks the caller.</para>
/// </remarks>
public static class ItemMovedHookV1
{
    private static Action? _catchUp;
    private static long _calls;

    /// <summary>
    /// Gets how many times the hook was called in this process (Diagnostics <c>Identity</c>).
    /// </summary>
    internal static long Calls => Interlocked.Read(ref _calls);

    /// <summary>
    /// Tells Bookmarks that Library Maintenance appended move-journal lines.
    /// </summary>
    /// <param name="movesJson">The moves as Library Maintenance describes them; not read (the journal is).</param>
    public static void OnItemsMoved(string movesJson)
    {
        _ = movesJson;
        Interlocked.Increment(ref _calls);
        try
        {
            Volatile.Read(ref _catchUp)?.Invoke();
        }
        catch (Exception)
        {
            // A cross-plugin call must never fail the caller; the next start's catch-up covers what this one missed.
        }
    }

    /// <summary>
    /// Makes the identity service the hook's target while it runs (Jellyfin 10.11 restarts in the same process with a new
    /// host, so the target is replaced, never kept from an old one).
    /// </summary>
    /// <param name="catchUp">Asks the service for a catch-up; must not block.</param>
    internal static void Attach(Action catchUp)
    {
        ArgumentNullException.ThrowIfNull(catchUp);
        Volatile.Write(ref _catchUp, catchUp);
    }

    /// <summary>
    /// Removes the target when its service stops, unless another one has replaced it meanwhile.
    /// </summary>
    /// <param name="catchUp">The target <see cref="Attach"/> was given.</param>
    internal static void Detach(Action catchUp) => Interlocked.CompareExchange(ref _catchUp, null, catchUp);
}
