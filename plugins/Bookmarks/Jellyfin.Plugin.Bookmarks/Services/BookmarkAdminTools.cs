using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// Implements <see cref="IBookmarkAdminTools"/> over the kit's export envelope,
/// import and snapshots on <c>bookmarks.json</c>, the kit's archive of detached bookmarks (<see cref="DetachedRecords"/>
/// with <see cref="BookmarkIdentityService"/> as their owner), Jellyfin's task manager and the kit's shortcut check.
/// </summary>
/// <remarks>
/// <para>An import that writes and a restore change the bookmarks as a whole, so both are refused (409) while a clip is
/// queued or being made: the clip worker reports on the records it started from. Both check it before their pre-operation
/// snapshot (so a refused import takes none), and the import again under the store's lock in its write
/// (<see cref="BookmarkImportAdapter"/>). Afterwards the kit's reconciler checks the users (names, unknown ids) and the
/// verify sweep checks every bookmark's video.</para>
/// <para>A restore keeps what the data knows now about this server's clip files: the clip folders recorded since the
/// snapshot (a clip file there stays reachable) and the files waiting to be deleted (the retry never deletes one a record
/// uses). Since 1.2.0 a restored record naming a path its clip file has left since (renamed, or made again) names
/// the file again (<see cref="ClipFileOps.RelinkRenamed"/>: the record the data had before, else the renames' history), and so
/// does an import's, Replace or Merge; never a file another record names. Both finish the clip file renames not finished
/// first.</para>
/// <para>Neither brings back a clip job: a snapshot taken, or an export made, while a clip was being made
/// holds its record <c>Queued</c> or <c>Running</c>, which no clip worker would run before the server's next start. Such a
/// job is ended as it is written (<see cref="BookmarkRecordRules.EndClipJobNoWorkerRuns"/>): <c>Failed</c>, with a sentence
/// its owner reads, so the clip can be made again at once.</para>
/// </remarks>
internal sealed class BookmarkAdminTools : IBookmarkAdminTools
{
    /// <summary>
    /// The error of a clip job a restored snapshot held queued or being made, which its owner reads as
    /// "Clip failed: …".
    /// </summary>
    internal const string RestoredClipJobMessage = "The bookmark data was restored from a snapshot taken while this clip was being made. Make it again.";

    private const string ExportFileStem = "bookmarks";

    private readonly IPluginConfigurationProvider _configuration;
    private readonly IBookmarkStore _store;
    private readonly string _snapshotDirectory;
    private readonly UserReconciler _reconciler;
    private readonly IUserManager _userManager;
    private readonly DetachedRecords _detached;
    private readonly BookmarkIdentityService _owner;
    private readonly Func<AttachedRecordVerifier?> _verifier;
    private readonly JellyfinEnhancedDiagnostics _jellyfinEnhanced;
    private readonly Func<ITaskManager?> _taskManager;
    private readonly string? _serverId;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<MissingClipSweep?> _sweep;
    private readonly ClipFileOps? _files;
    private readonly Func<ClipFolderInLibrary?> _clipFolders;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkAdminTools"/> class.
    /// </summary>
    /// <param name="configuration">The plugin settings (exported for reference, and <c>SnapshotsToKeep</c>).</param>
    /// <param name="store">The bookmark store.</param>
    /// <param name="snapshotDirectory">The snapshot folder (<c>BookmarkPaths.SnapshotsDirectory</c>).</param>
    /// <param name="reconciler">The kit's reconciler (deleted users for the import, the check afterwards).</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="detached">The kit's detached records, for the archive.</param>
    /// <param name="owner">The bookmarks' record owner.</param>
    /// <param name="verifier">The kit's verify sweep, resolved when needed (<c>null</c> when there is none).</param>
    /// <param name="jellyfinEnhanced">The kit's Jellyfin Enhanced Diagnostics, for the shortcut check.</param>
    /// <param name="taskManager">Jellyfin's task manager, resolved when needed.</param>
    /// <param name="serverId">The server's id, for exports.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="sweep">The missing-clip sweep, resolved when an admin confirms a clip folder;
    /// none by default, and the confirmation is then refused (503).</param>
    /// <param name="files">The clip file operations, which name a restored or imported record's renamed clip file again and
    /// finish the renames not finished first; none by default (a test's tools), and nothing is relinked.</param>
    /// <param name="clipFolders">The check of a clip folder typed on the settings page (since 1.2.1); none by default, and
    /// the check then knows nothing.</param>
    public BookmarkAdminTools(
        IPluginConfigurationProvider configuration,
        IBookmarkStore store,
        string snapshotDirectory,
        UserReconciler reconciler,
        IUserManager userManager,
        DetachedRecords detached,
        BookmarkIdentityService owner,
        Func<AttachedRecordVerifier?> verifier,
        JellyfinEnhancedDiagnostics jellyfinEnhanced,
        Func<ITaskManager?> taskManager,
        string? serverId,
        ILogger logger,
        TimeProvider? timeProvider = null,
        Func<MissingClipSweep?>? sweep = null,
        ClipFileOps? files = null,
        Func<ClipFolderInLibrary?>? clipFolders = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(detached);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(jellyfinEnhanced);
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentNullException.ThrowIfNull(logger);
        _configuration = configuration;
        _store = store;
        _snapshotDirectory = snapshotDirectory;
        _reconciler = reconciler;
        _userManager = userManager;
        _detached = detached;
        _owner = owner;
        _verifier = verifier;
        _jellyfinEnhanced = jellyfinEnhanced;
        _taskManager = taskManager;
        _serverId = serverId;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sweep = sweep ?? (static () => null);
        _files = files;
        _clipFolders = clipFolders ?? (static () => null);
    }

    /// <inheritdoc />
    public IActionResult Export(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The data first: it refuses (503) a file whose upgrade failed, and passes on a newer version's file as it is. The
        // archive next: an export that would leave its records out is refused too (503).
        var data = _store.Documents.ExportData();
        var archive = _detached.ExportArchive(_owner);
        var document = _store.Documents.Current;
        var stored = document.Bookmarks.Where(b => b is not null).Select(b => b.UserId)
            .Concat(archive.Select(UserIdOf).Where(id => id is not null).Select(id => id!.Value))
            .Distinct()
            .ToList();
        var envelope = ExportEnvelope.Create(
            BookmarksKit.Options,
            data,
            ExportUser.For(stored, document.KnownUsers ?? KnownUsers.Empty, ServerUsers(), _reconciler.DeletedUserIds),
            _configuration.GetConfiguration(),
            archive,
            _serverId,
            _timeProvider.GetUtcNow().UtcDateTime);
        _logger.LogInformation(
            "An admin exported the bookmark data: {Records} records of {Users} users, {Archived} archived",
            document.Bookmarks.Count,
            stored.Count,
            archive.Count);
        return envelope.ToFileResult(context, ExportFileStem);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ImportAsync(HttpRequest request, string? mode, bool? dryRun, string? mapUsersBy)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parsed = ImportRequest.Parse(mode, dryRun, mapUsersBy);
        var cancellationToken = request.HttpContext.RequestAborted;
        var envelope = await ExportEnvelope.ReadAsync(request, cancellationToken).ConfigureAwait(false);

        // Refused before the kit takes its pre-operation snapshot: a refused import would otherwise leave
        // one holding the running job, and rotate an older one (a deleted user's, say) out of the five kept. The adapter
        // checks again under the store's lock.
        if (!parsed.DryRun && BookmarkImportAdapter.CountClipsBeingMade(_store.GetAll()) > 0)
        {
            throw PluginException.Conflict(BookmarkImportAdapter.ClipsBeingMadeMessage);
        }

        // Users mapped by name never get a deleted user's bookmarks: while the list of those users can't be read, refused, a
        // dry run too (fail closed).
        if (parsed.MapUsersBy == MapUsersBy.Name)
        {
            _reconciler.ThrowIfDeletedUsersUnknown("An import that maps users by name");
        }

        // The clip file renames not finished are finished first: their records name the path the file is at.
        if (!parsed.DryRun && _files is not null)
        {
            await _files.FinishRenamesAsync(null, cancellationToken).ConfigureAwait(false);
        }

        var adapter = new BookmarkImportAdapter(
            PhysicalClipFileSystem.Instance,
            (records, dry) => _detached.ImportArchived(_owner, records, dry),
            refuseWhileClipsAreMade: !parsed.DryRun,
            exportedUtc: envelope.ExportedUtc,
            relink: parsed.DryRun || _files is null ? null : RelinkImported);
        var report = await JsonStoreImport.RunAsync(
            _store.Documents,
            envelope,
            parsed,
            adapter,
            BookmarksKit.Options,
            ServerUsers(),
            _reconciler.DeletedUserIds,
            _snapshotDirectory,
            _logger,
            cancellationToken).ConfigureAwait(false);
        if (!parsed.DryRun)
        {
            await AfterDataChangedAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = report.ToDictionary();

        // A dry run says when the import itself would be refused for now (the page names it next to Import).
        result["ClipsBeingMade"] = BookmarkImportAdapter.CountClipsBeingMade(_store.GetAll());
        result["StoreReadOnly"] = _store.IsReadOnly;
        return result;
    }

    /// <inheritdoc />
    public IDictionary<string, object?> GetSnapshots() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Snapshots"] = _store.Documents.ListSnapshots(_snapshotDirectory).Select(s => s.ToDictionary()).ToList(),
        ["KeepSnapshotsBeforeUtc"] = _store.Documents.KeepSnapshotsBeforeUtc,
        ["KeptBecause"] = _store.Documents.KeptSnapshotsIncident?.ToString(),
        ["SnapshotsToKeep"] = Math.Max(0, _configuration.GetConfiguration().SnapshotsToKeep),
        ["StoreReadOnly"] = _store.IsReadOnly,
        ["StoreReadOnlyReason"] = _store.ReadOnlyReason?.ToString(),
        ["Recoverable"] = _store.Documents.CanRecover,
        ["ClipsBeingMade"] = BookmarkImportAdapter.CountClipsBeingMade(_store.GetAll())
    };

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> RestoreSnapshotAsync(string? name, Guid adminId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw PluginException.Invalid("name is required.");
        }

        if (_store.Documents.CanRecover)
        {
            return await RecoverAsync(name, adminId, cancellationToken).ConfigureAwait(false);
        }

        if (_store.IsReadOnly)
        {
            throw PluginException.Conflict("The bookmark data is read-only, so no snapshot can be restored.");
        }

        if (BookmarkImportAdapter.CountClipsBeingMade(_store.GetAll()) > 0)
        {
            throw PluginException.Conflict(BookmarkImportAdapter.ClipsBeingMadeMessage);
        }

        // The clip file renames not finished are finished first, so the data before names where each file is.
        if (_files is not null)
        {
            await _files.FinishRenamesAsync(null, cancellationToken).ConfigureAwait(false);
        }

        var before = _store.Documents.Current;
        var restored = await _store.Documents.RestoreSnapshotAsync(_snapshotDirectory, name, cancellationToken).ConfigureAwait(false);
        var endedUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var jobsEnded = 0;
        var relinked = 0;
        var kept = await _store.UpdateDocumentAsync(
            document =>
            {
                relinked = 0;
                var after = AfterRestore(document, before, endedUtc, out jobsEnded);
                return _files is null ? after : _files.RelinkRenamed(after, before, out relinked);
            },
            cancellationToken).ConfigureAwait(false);
        if (jobsEnded > 0)
        {
            _logger.LogInformation("The restored snapshot had {Count} clip jobs queued or being made; they were marked failed, to be made again", jobsEnded);
        }

        await AfterDataChangedAsync(cancellationToken).ConfigureAwait(false);

        var result = restored.ToDictionary();
        result["Bookmarks"] = kept.Bookmarks.Count(b => b is not null && BookmarkRecordRules.IsLive(b));
        result["ClipJobsEnded"] = jobsEnded;
        result["ClipsRelinked"] = relinked;
        return result;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> StartEmptyAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var reason = _store.ReadOnlyReason;
        var recovery = await _store.Documents.RecoverAsync(_snapshotDirectory, null, startEmpty: true, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning(
            "An admin ({AdminId}) started the bookmark data empty while it was read-only ({Reason}); kept: {MovedAside} and every snapshot",
            adminId,
            reason,
            recovery.MovedAside.Count == 0 ? "nothing moved aside" : string.Join(", ", recovery.MovedAside));
        await AfterDataChangedAsync(cancellationToken).ConfigureAwait(false);
        var result = recovery.ToDictionary();
        result["Bookmarks"] = 0;
        return result;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> StartUserMarksEmptyAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var result = await _reconciler.StartMarksEmptyAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning(
            "An admin ({AdminId}) started the list of users deleted on this server (kit-users.json) empty while it couldn't be read; kept: {MovedAside}, and {MarksKept} marks made since the server started",
            adminId,
            result["MovedAside"] is IReadOnlyList<string> { Count: > 0 } moved ? string.Join(", ", moved) : "nothing moved aside",
            result["MarksKept"]);
        return result;
    }

    /// <inheritdoc />
    public IDictionary<string, object?> RunMaintenance()
    {
        var tasks = _taskManager() ?? throw new PluginException(PluginErrorKind.Unavailable, "The server's scheduled tasks aren't available right now.");
        tasks.QueueScheduledTask<BookmarksMaintenanceTask>();
        _logger.LogInformation("An admin queued the daily maintenance to run now");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Queued"] = true,
            ["Task"] = "Bookmarks: Maintenance"
        };
    }

    /// <inheritdoc />
    public IDictionary<string, object?> CheckClipFolder(string? path, Guid adminId)
        => _clipFolders()?.Check(path, adminId)
            ?? new Dictionary<string, object?>(StringComparer.Ordinal) { ["Path"] = null, ["Valid"] = null, ["Warnings"] = Array.Empty<object>() };

    /// <inheritdoc />
    public IDictionary<string, object?> CheckShortcut(string? combo)
        => _jellyfinEnhanced.CheckShortcut(BookmarksKit.BookmarkNowShortcutName, combo)
            ?? throw new InvalidOperationException("Bookmarks' shortcut setting isn't known to the kit.");

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ConfirmClipFolderAsync(string? rootId, bool? dryRun, Guid adminId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootId))
        {
            throw PluginException.Invalid("rootId is required.");
        }

        var dry = dryRun ?? true;
        if (!dry && _store.IsReadOnly)
        {
            throw PluginException.Conflict("The bookmark data is read-only, so no clip folder can be confirmed.");
        }

        var sweep = _sweep() ?? throw PluginException.Unavailable("The missing-clip sweep isn't available, so no clip folder can be confirmed right now.");
        if (sweep.FolderOf(rootId) is null)
        {
            // Before the snapshot: an id no folder has changes nothing.
            throw PluginException.NotFound(MissingClipSweep.UnknownFolderMessage);
        }

        if (!dry)
        {
            // The marks are the start of the retention's countdown: the data before them is kept as a snapshot.
            await _store.Documents.PreOpSnapshotAsync(_snapshotDirectory, SnapshotOperations.Other, cancellationToken).ConfigureAwait(false);
        }

        return await sweep.ConfirmFolderAsync(rootId, dry, adminId, cancellationToken).ConfigureAwait(false);
    }

    // The restored document as it is written (the restore's second write, under the store's lock):
    // - the clip folders and the clip files waiting to be deleted the data knew before the restore stay: a folder recorded
    //   since the snapshot keeps its clip files reachable (the guard serves and deletes only inside recorded folders), and a
    //   waiting file stays listed (the retry never deletes one a record uses). Within the document's limits, the newer first;
    // - so do the clip folders' marker tokens it knew, over the snapshot's;
    // - a clip job the snapshot holds queued or running is ended, since no clip worker runs it: Failed,
    //   with RestoredClipJobMessage, at endedUtc (a tombstone's clip its file). jobsEnded counts the live bookmarks' jobs.
    internal static BookmarkStoreDocument AfterRestore(BookmarkStoreDocument restored, BookmarkStoreDocument before, DateTime endedUtc, out int jobsEnded)
    {
        ArgumentNullException.ThrowIfNull(restored);
        ArgumentNullException.ThrowIfNull(before);
        jobsEnded = restored.Bookmarks.Count(b => b is not null && BookmarkRecordRules.IsLive(b) && BookmarkRecordRules.HasClipJob(b));
        var bookmarks = restored.Bookmarks.Any(b => b is not null && BookmarkRecordRules.HasClipJob(b))
            ? restored.Bookmarks.Select(b => b is not null && BookmarkRecordRules.HasClipJob(b) ? BookmarkRecordRules.EndClipJobNoWorkerRuns(b, RestoredClipJobMessage, endedUtc) : b!).ToList()
            : null;
        var roots = before.ClipRoots
            .Concat(restored.ClipRoots)
            .Distinct(ClipRootPaths.Comparer)
            .Take(BookmarkStoreDocument.MaxClipRoots)
            .ToList();
        var pending = before.PendingFileDeletes
            .Concat(restored.PendingFileDeletes)
            .Where(p => p is not null)
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(BookmarkStoreDocument.MaxPendingFileDeletes)
            .ToList();
        // The clip folders' marker tokens the data knew before the restore win: the markers on
        // disk name them, and a snapshot's older token would make a folder read as another one.
        var tokens = restored.ClipRootTokens.ToDictionary(t => t.Key, t => t.Value, ClipRootPaths.Comparer);
        foreach (var (root, token) in before.ClipRootTokens)
        {
            tokens[root] = token;
        }

        var sameTokens = tokens.Count == restored.ClipRootTokens.Count
            && tokens.All(t => restored.ClipRootTokens.TryGetValue(t.Key, out var token) && string.Equals(token, t.Value, StringComparison.Ordinal));
        if (bookmarks is null && roots.SequenceEqual(restored.ClipRoots, ClipRootPaths.Comparer) && pending.SequenceEqual(restored.PendingFileDeletes) && sameTokens)
        {
            return restored;
        }

        return restored with { Bookmarks = bookmarks ?? restored.Bookmarks, ClipRoots = roots, ClipRootTokens = tokens, PendingFileDeletes = pending };
    }

    // Restore snapshot while the data is missing or unreadable (the kit's RecoverAsync): no safety snapshot (the store holds
    // an empty stand-in), what couldn't be read moved aside, every snapshot kept, and the clip jobs the snapshot holds ended
    // by the store's step after a restore (BookmarkStore.AfterRestoreAtLoad), counted here for the page.
    private async Task<IDictionary<string, object?>> RecoverAsync(string name, Guid adminId, CancellationToken cancellationToken)
    {
        var reason = _store.ReadOnlyReason;
        var startedUtc = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-1);
        var recovery = await _store.Documents.RecoverAsync(_snapshotDirectory, name, startEmpty: false, cancellationToken).ConfigureAwait(false);
        var document = _store.Documents.Current;
        var jobsEnded = document.Bookmarks.Count(b => b is not null && BookmarkRecordRules.IsLive(b)
            && b.Clip is { Status: BookmarkClipStatus.Failed } clip
            && string.Equals(clip.Error, RestoredClipJobMessage, StringComparison.Ordinal)
            && clip.CompletedAt >= startedUtc);
        _logger.LogWarning(
            "An admin ({AdminId}) restored the bookmark data from the snapshot {Snapshot} while it was read-only ({Reason}); kept: {MovedAside} and every snapshot",
            adminId,
            recovery.RestoredFrom,
            reason,
            recovery.MovedAside.Count == 0 ? "nothing moved aside" : string.Join(", ", recovery.MovedAside));
        await AfterDataChangedAsync(cancellationToken).ConfigureAwait(false);
        var result = recovery.ToDictionary();
        result["SafetySnapshot"] = null;
        result["Bookmarks"] = document.Bookmarks.Count(b => b is not null && BookmarkRecordRules.IsLive(b));
        result["ClipJobsEnded"] = jobsEnded;
        return result;
    }

    // An import's records named again after their clip files' renames: from the records the data had, else
    // the renames' history.
    private (BookmarkStoreDocument Document, int Relinked, int LetGo) RelinkImported(BookmarkStoreDocument document, BookmarkStoreDocument before)
    {
        var relinked = 0;
        var letGo = 0;
        var after = _files?.RelinkRestored(document, before, out relinked, out letGo) ?? document;
        return (after, relinked, letGo);
    }

    private static Guid? UserIdOf(JsonObject record)
        => record?["UserId"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : null;

    private async Task AfterDataChangedAsync(CancellationToken cancellationToken)
    {
        // The users: names refreshed, unknown ids listed (never deleted); then every bookmark's video checked.
        try
        {
            await _reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The user check after the data changed failed; the daily maintenance runs it again");
        }

        _verifier()?.Queue(VerifySweepReason.DataRestored);
    }

    private Dictionary<Guid, string> ServerUsers()
        => KitUsers.GetAll(_userManager).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().Username);
}
