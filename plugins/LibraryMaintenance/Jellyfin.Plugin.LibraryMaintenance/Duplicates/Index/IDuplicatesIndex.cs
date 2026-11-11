using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// The movie index as the Duplicates controller sees it: its status, and a rebuild.
/// </summary>
public interface IDuplicatesIndex
{
    /// <summary>
    /// Gets a value indicating whether Duplicates is turned on in the settings (<c>DuplicatesEnabled</c>).
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the index's status as a caller sees it: the libraries and counts of the libraries they can open,
    /// the build's state, and the last build's error.
    /// </summary>
    /// <param name="user">The caller, or null for an API key (every library).</param>
    /// <returns>The status.</returns>
    LibraryMaintenanceDuplicateIndexDto GetStatus(User? user);

    /// <summary>
    /// Starts a rebuild, or, while one runs, asks for one more after it (single-flight: never two at once). It runs even
    /// right after a failed build.
    /// </summary>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The status right after asking, as <see cref="GetStatus"/> gives it.</returns>
    LibraryMaintenanceDuplicateIndexDto RequestRebuild(User? user);

    /// <summary>
    /// Gets a value indicating whether a caller can open every library the index read and every movie in them (no parental
    /// rating or tag limit), so Diagnostics' server-wide numbers are theirs to see.
    /// </summary>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>Whether they see every library.</returns>
    bool SeesEveryLibrary(User? user);
}
