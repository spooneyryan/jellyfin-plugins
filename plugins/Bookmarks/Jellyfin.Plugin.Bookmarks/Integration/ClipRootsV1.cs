using System;
using System.Threading;

namespace Jellyfin.Plugin.Bookmarks.Integration;

/// <summary>
/// The clip folders, for other plugins (since 1.2.0): Library Maintenance's Duplicates excludes them from its duplicate
/// search next to its own path exclusions, so clips taken from a film are never offered as
/// its duplicates when an admin keeps clips inside a library.
/// </summary>
/// <remarks>
/// <para>A cross-plugin facade: <c>public static</c>, BCL types only, located by another plugin through
/// <c>AssemblyLoadContext.All</c> by the exact assembly name <c>Jellyfin.Plugin.Bookmarks</c> and called by reflection
/// (each plugin has its own load context, so no type is shared but the BCL's). Static classes are abstract and sealed in IL,
/// so Jellyfin never creates one. V1's meaning never changes; another meaning would be a <c>ClipRootsV2</c>.</para>
/// <para><b>What it answers.</b> Every folder Bookmarks has written clips to (the recorded clip folders, <c>ClipRoots</c>:
/// a clip folder is recorded before any clip file is written in it, and stays recorded when the setting changes),
/// and the folder new clips go to now (the <c>ClipOutputPath</c> setting, else <c>data/bookmarks/clips</c>), each once:
/// absolute, without a trailing separator (a drive or share root keeps its own), to be compared without regard to case on
/// Windows and on whole path segments. A clip file is only ever in one of them or a folder below it. A folder that is offline
/// (an unplugged drive) is still listed. A new array each call.</para>
/// <para><b>When.</b> Bookmarks answers once the server has finished starting (its services run after Jellyfin's startup
/// gate), also while its data is read-only (only read here). Before that, on a Jellyfin line this build isn't for (the
/// plugin stays inert), and after the plugin's services stopped, it answers an empty array. It never throws and never
/// blocks: it reads memory and the plugin's settings.</para>
/// <para><b>An empty array means "not answering", not "no clip folders"</b>: while Bookmarks answers there is
/// at least the folder new clips go to (unless the Clip folder setting isn't a folder path). So a caller must never keep an
/// empty answer, and asks again the next time it needs the folders: a duplicate search that ran before Bookmarks' services
/// started would otherwise leave the clips in for good. A non-empty answer may be kept for a while; folders are only ever
/// added (a new Clip folder setting), so asking once per run is simplest.</para>
/// </remarks>
public static class ClipRootsV1
{
    private static Func<string[]>? _source;
    private static long _calls;

    /// <summary>
    /// Gets how many times another plugin asked (<see cref="Get"/>) in this process (Diagnostics <c>ClipRoots</c>).
    /// </summary>
    internal static long Calls => Interlocked.Read(ref _calls);

    /// <summary>
    /// Gets a value indicating whether Bookmarks' services are answering (Diagnostics <c>ClipRoots</c>).
    /// </summary>
    internal static bool IsAttached => Volatile.Read(ref _source) is not null;

    /// <summary>
    /// Gets the folders Bookmarks writes clip files to (above, "What it answers").
    /// </summary>
    /// <returns>The folders, absolute; empty while Bookmarks isn't answering (not yet, or no longer), an answer a caller must
    /// not keep (above).</returns>
    public static string[] Get()
    {
        Interlocked.Increment(ref _calls);
        return Answer();
    }

    /// <summary>
    /// What <see cref="Get"/> answers, without counting a call (Diagnostics' own look).
    /// </summary>
    /// <returns>The folders; empty while Bookmarks isn't running.</returns>
    internal static string[] Answer()
    {
        try
        {
            return Volatile.Read(ref _source)?.Invoke() is { } folders ? (string[])folders.Clone() : [];
        }
        catch (Exception)
        {
            // A cross-plugin call must never fail its caller.
            return [];
        }
    }

    /// <summary>
    /// Makes Bookmarks' services the facade's source while they run (Jellyfin 10.11 restarts in the same process with a new
    /// host, so the source is replaced, never kept from an old one).
    /// </summary>
    /// <param name="source">Gives the folders; must not block.</param>
    internal static void Attach(Func<string[]> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Volatile.Write(ref _source, source);
    }

    /// <summary>
    /// Removes the source when its services stop, unless another one has replaced it meanwhile.
    /// </summary>
    /// <param name="source">The source <see cref="Attach"/> was given.</param>
    internal static void Detach(Func<string[]> source) => Interlocked.CompareExchange(ref _source, null, source);
}
