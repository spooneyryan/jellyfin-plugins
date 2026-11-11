using System;
using System.IO;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The plugin's data folder, <c>&lt;IApplicationPaths.DataPath&gt;/&lt;KitOptions.DataFolder&gt;</c>.
/// Nothing is created here; stores create their folders when they first write.
/// </summary>
internal sealed class KitPaths
{
    /// <summary>The snapshot folder's name under the data folder.</summary>
    public const string SnapshotsFolder = "snapshots";

    /// <summary>The archive folder's name under the data folder.</summary>
    public const string ArchiveFolder = "archive";

    /// <summary>
    /// Initializes a new instance of the <see cref="KitPaths"/> class.
    /// </summary>
    /// <param name="applicationPaths">The server's paths.</param>
    /// <param name="options">The plugin's kit options.</param>
    public KitPaths(IApplicationPaths applicationPaths, KitOptions options)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(options);

        Root = Path.Combine(applicationPaths.DataPath, options.DataFolder);
    }

    /// <summary>
    /// Gets the plugin's data folder.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Gets the snapshot folder, <c>&lt;Root&gt;/snapshots</c> (the daily snapshots, and the <c>pre-op</c> class in it).
    /// </summary>
    public string Snapshots => Path.Combine(Root, SnapshotsFolder);

    /// <summary>
    /// Gets the archive folder, <c>&lt;Root&gt;/archive</c>: each store's <c>&lt;store&gt;-archived.jsonl</c>, the detached
    /// records past retention (append-only, never rotated).
    /// </summary>
    public string Archive => Path.Combine(Root, ArchiveFolder);
}
