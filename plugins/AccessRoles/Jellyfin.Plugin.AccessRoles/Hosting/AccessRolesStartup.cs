using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Hosting;

/// <summary>
/// The plugin's start, after Jellyfin's startup gate: loads <c>accessroles.json</c>, says in the log when it
/// isn't <c>Ok</c>, and on a fresh install writes the empty schema 1 file, so the data folder exists from the first start.
/// A store the kit restored from a snapshot at load holds the library sync until an admin confirms it
/// (<see cref="RestoreHold"/>, since 0.9.4). The library sync (<c>Sync/LibrarySyncService</c>) waits on the same
/// gate, as does the rule-table self-check. Degraded mode is audited here, and the mirror's records it read
/// from a copy the store couldn't use are taken back into a writable store.
/// </summary>
internal sealed class AccessRolesStartup : KitBackgroundService
{
    private readonly AccessRolesStore _store;
    private readonly AccessRolesPaths? _paths;
    private readonly AccessRolesAuditLog? _audit;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesStartup"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="store">The roles and assignments store.</param>
    /// <param name="logger">The logger (category <c>Jellyfin.Plugin.AccessRoles.Kit.Startup</c>).</param>
    /// <param name="paths">The data folder's paths, for the restore hold; <c>null</c> leaves the hold to the sync's first run.</param>
    /// <param name="audit">The audit log, for Degraded mode's start and end; <c>null</c> audits nothing.</param>
    public AccessRolesStartup(IServerApplicationHost applicationHost, AccessRolesStore store, ILogger logger, AccessRolesPaths? paths = null, AccessRolesAuditLog? audit = null)
        : base(applicationHost, logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _paths = paths;
        _audit = audit;
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        var status = _store.Status;
        if (status != StoreStatus.Ok)
        {
            Logger.LogWarning("Access Roles' data file {File} is {Status}: {Message}", _store.Documents.FilePath, status, _store.Documents.StatusMessage);
        }

        if (_paths is not null)
        {
            RestoreHold.Ensure(_paths, _store, TimeProvider.GetUtcNow().UtcDateTime, Logger);
        }

        // Degraded mode: evaluated now if no request did first, and audited when this start began or ended it.
        var degraded = _store.Degraded;
        if (_audit is not null && degraded.Current is { } state && degraded.EnteredNow)
        {
            await _audit.AppendAsync([DegradedAudit.Entered(state)], stoppingToken).ConfigureAwait(false);
        }
        else if (_audit is not null && degraded.EndedNow is { } ended)
        {
            await _audit.AppendAsync([DegradedAudit.Ended(AuditActor.System, ended, degraded.EndedBecause ?? "TheDataFileManagesThemAgain", degraded.EndedEveryNonAdmin)], stoppingToken).ConfigureAwait(false);
        }

        await TakeBackMirrorRecordsAsync(degraded, stoppingToken).ConfigureAwait(false);

        if (await _store.CreateFileIfMissingAsync(stoppingToken).ConfigureAwait(false))
        {
            Logger.LogInformation("Access Roles created its data file {File} (schema {Version}); no user is managed yet", _store.Documents.FilePath, AccessRolesDocument.CurrentVersion);
        }
    }

    // The mirror's records a copy of the data file named (a file moved aside, the marker) that the document lacks: taken back,
    // so the mirror, Release all locks and the uninstall find every flag it turned off. A read-only store keeps them in the
    // marker, where those read them meanwhile; a save that fails leaves them there for the next start.
    private async Task TakeBackMirrorRecordsAsync(DegradedMode degraded, CancellationToken cancellationToken)
    {
        IReadOnlySet<Guid> records = degraded.MirrorEvidence;
        if (records.Count == 0 || _store.IsReadOnly)
        {
            return;
        }

        try
        {
            var now = TimeProvider.GetUtcNow().UtcDateTime;
            await _store.UpdateAsync(
                d =>
                {
                    var missing = records.Where(id => !d.PreferenceMirror.ContainsKey(id)).ToList();
                    if (missing.Count == 0)
                    {
                        return d;
                    }

                    var mirror = new Dictionary<Guid, MirroredPreference>(d.PreferenceMirror);
                    foreach (var id in missing)
                    {
                        mirror[id] = new MirroredPreference { WrittenUtc = now };
                    }

                    return d with { PreferenceMirror = mirror };
                },
                cancellationToken,
                notify: true).ConfigureAwait(false);
            degraded.ForgetMirrorEvidence(records);
            Logger.LogWarning(
                "Access Roles took back the records of {Count} users whose native preference access its mirror turned off, from a copy of its data file it couldn't use: the mirror turns each back on when their roles no longer lock all five sections, and Release all locks lists them",
                records.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Access Roles couldn't save the {Count} mirror records it read from a copy of its data file; its Degraded mode marker keeps them for the next start", records.Count);
        }
    }
}
