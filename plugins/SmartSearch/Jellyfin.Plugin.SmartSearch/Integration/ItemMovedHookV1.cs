using System;
using System.Threading;

namespace Jellyfin.Plugin.SmartSearch.Integration;

/// <summary>
/// The hook Library Maintenance calls right after it appends Committed lines to its move journal: Smart Search catches
/// up with the journal at once instead of at its next start, so the items pinned to or excluded from auto-collections
/// follow their moved items, and the collections affected run again.
/// </summary>
/// <remarks>
/// <para>A cross-plugin facade: <c>public static</c>, BCL types only, located by Library Maintenance through
/// <c>AssemblyLoadContext.All</c> by the exact assembly name <c>Jellyfin.Plugin.SmartSearch</c>. Static classes are abstract
/// and sealed in IL, so Jellyfin never creates one.</para>
/// <para><b>The journal stays the source of truth.</b> The hook only asks for a catch-up from the stored position (the
/// definitions' <c>MoveJournalId</c> and <c>LastMoveSeq</c>); the text it is given is not read, so a hook call that is
/// lost, early or repeated changes nothing: Smart Search catches up at every start too, and a catch-up is idempotent.
/// Before the listener runs (the server's startup) the call does nothing. It never throws and never blocks the
/// caller.</para>
/// </remarks>
public static class ItemMovedHookV1
{
    private static Action? _catchUp;
    private static long _calls;

    /// <summary>
    /// Gets how many times the hook was called in this process.
    /// </summary>
    internal static long Calls => Interlocked.Read(ref _calls);

    /// <summary>
    /// Tells Smart Search that Library Maintenance appended move-journal lines.
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
    /// Makes the listener the hook's target while it runs (Jellyfin 10.11 restarts in the same process with a new host,
    /// so the target is replaced, never kept from an old one).
    /// </summary>
    /// <param name="catchUp">Asks for a catch-up; must not block.</param>
    internal static void Attach(Action catchUp)
    {
        ArgumentNullException.ThrowIfNull(catchUp);
        Volatile.Write(ref _catchUp, catchUp);
    }

    /// <summary>
    /// Removes the target when its listener stops, unless another one has replaced it meanwhile.
    /// </summary>
    /// <param name="catchUp">The target <see cref="Attach"/> was given.</param>
    internal static void Detach(Action catchUp) => Interlocked.CompareExchange(ref _catchUp, null, catchUp);
}
