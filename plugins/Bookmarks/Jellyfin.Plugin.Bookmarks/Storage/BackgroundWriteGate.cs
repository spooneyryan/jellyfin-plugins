using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// The one check every automatic writer of <c>bookmarks.json</c> makes before it works (the kit's rule: every read-only
/// status is inert): while the store is read-only, because the file was written by a newer Bookmarks
/// (<c>ReadOnlyNewerVersion</c>), upgrading it failed (<c>ReadOnlyMigrationFailed</c>), or it is missing or unreadable with
/// no snapshot to restore (<c>ReadOnlyDataMissing</c>, <c>ReadOnlyDataUnreadable</c>), the clip worker, the
/// <c>.partial</c> janitor, the identity service and the maintenance task stay idle. So no automatic write can save an
/// empty or partly read document over the data, and none keeps failing with 503.
/// </summary>
/// <remarks>
/// <para>The store's status is decided when it first loads (the kit store never loads twice) and stays until the server
/// restarts, except that an admin can recover missing or unreadable data from the settings page (Bookmarks 1.2), which
/// makes the store writable at once: the gate opens then, and the clip worker, which found it closed when it started, waits
/// for that (<see cref="WaitUntilOpenAsync"/>) rather than staying idle. Requests from users don't ask the gate: their
/// writes answer 503 from the store itself. Snapshots need no gate, the kit store writes none while
/// read-only, and neither do the kit's reconciler and deleted-user consumer, which skip an <c>IUserDataOwner</c> whose
/// <c>IsReadOnly</c> is true (the bookmark store's owner reports the store's).</para>
/// <para>The first refusal of each writer is logged once, as a Warning; the store has logged the cause as an Error when it
/// loaded.</para>
/// </remarks>
internal sealed class BackgroundWriteGate
{
    private readonly IBookmarkStore _store;
    private readonly ILogger _logger;
    private readonly int[] _logged = new int[Enum.GetValues<BackgroundWriter>().Length];

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundWriteGate"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="logger">The logger.</param>
    public BackgroundWriteGate(IBookmarkStore store, ILogger<BackgroundWriteGate> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Says whether a background writer may work now, loading the store if nothing has yet.
    /// </summary>
    /// <param name="writer">The writer asking.</param>
    /// <returns><c>true</c> while the store is writable; <c>false</c> while it is read-only (for the rest of the process,
    /// unless an admin recovers missing or unreadable data).</returns>
    /// <exception cref="Jellyfin.Plugin.Kit.Api.PluginException">503: the store file exists but can't be read at all (locked,
    /// no access). That isn't read-only: the store tries again on its next use, so the writer should too.</exception>
    public bool MayWrite(BackgroundWriter writer)
    {
        if (_store.ReadOnlyReason is not { } reason)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _logged[(int)writer], 1) == 0)
        {
            LogIdle(writer, reason);
        }

        return false;
    }

    /// <summary>
    /// Waits until the gate opens: at once while the store is writable, else until an admin recovers missing or unreadable
    /// data from the settings page; for the other read-only reasons only <paramref name="cancellationToken"/> ends it (the
    /// server's next start decides again).
    /// </summary>
    /// <param name="cancellationToken">Ends the wait (the server stopping).</param>
    /// <returns>A task that completes once the store can be written.</returns>
    /// <exception cref="Jellyfin.Plugin.Kit.Api.PluginException">503: the store file exists but can't be read at all.</exception>
    public Task WaitUntilOpenAsync(CancellationToken cancellationToken) => _store.Documents.WaitUntilWritableAsync(cancellationToken);

    // One constant template per writer, so the log reads as a sentence.
    private void LogIdle(BackgroundWriter writer, BookmarkStoreReadOnlyReason reason)
    {
        switch (writer)
        {
            case BackgroundWriter.ClipWorker:
                _logger.LogWarning("The bookmark data is read-only ({Reason}), so ClipWorker is idle: interrupted clip jobs are not resumed and no clip job starts until the data can be written (an admin recovers it on the settings page, or the server restarts with data it can write). See the plugin's Diagnostics", reason);
                break;
            case BackgroundWriter.PartialFileJanitor:
                _logger.LogWarning("The bookmark data is read-only ({Reason}), so PartialFileJanitor is idle: stale .partial clip files and old clip files waiting to be deleted are left alone until the data can be written (an admin recovers it on the settings page, or the server restarts with data it can write). See the plugin's Diagnostics", reason);
                break;
            case BackgroundWriter.IdentityService:
                _logger.LogWarning("The bookmark data is read-only ({Reason}), so IdentityService is idle: bookmarks are neither looked up, detached nor reattached until the data can be written (an admin recovers it on the settings page, or the server restarts with data it can write). See the plugin's Diagnostics", reason);
                break;
            default:
                _logger.LogWarning("The bookmark data is read-only ({Reason}), so {Writer} is idle: the daily maintenance (snapshots, user names, clip files) is skipped until the data can be written (an admin recovers it on the settings page, or the server restarts with data it can write). See the plugin's Diagnostics", reason, writer);
                break;
        }
    }
}
