using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// A new snapshot is the index (<see cref="MovieIndexService.Published"/>): what was made from an older one is old.
/// </summary>
internal sealed class IndexPublishedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IndexPublishedEventArgs"/> class.
    /// </summary>
    /// <param name="version">The new snapshot's version.</param>
    public IndexPublishedEventArgs(long version)
    {
        Version = version;
    }

    /// <summary>Gets the new snapshot's version.</summary>
    public long Version { get; }
}
