using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Bookmarks.Tasks;

/// <summary>
/// Bookmarks' own steps of its daily "Bookmarks: Maintenance", around the kit's (the
/// retention archive step, the user reconciliation, the verify sweep, re-resolution and the reattach): the daily snapshot
/// of <c>bookmarks.json</c> first, then, last, the retries of the clip files an automatic delete couldn't delete and, since
/// 1.2.0, the missing-clip sweep (missing clip files marked, offline clip folders skipped, the retention into the
/// archive).
/// </summary>
internal static class BookmarkMaintenanceSteps
{
    /// <summary>The snapshot step's name.</summary>
    public const string SnapshotsStep = "Snapshots";

    /// <summary>The clip-file retry step's name.</summary>
    public const string ClipFilesStep = "ClipFiles";

    /// <summary>The missing-clip sweep's step name.</summary>
    public const string MissingClipsStep = "MissingClips";

    /// <summary>
    /// The missing-clip sweep's place: after the clip-file retries, the last step, so the day's snapshot (first) holds the
    /// records before anything is archived.
    /// </summary>
    public const int MissingClipsOrder = KitMaintenanceOrder.Plugin + 10;

    /// <summary>
    /// Adds Bookmarks' steps to the kit's maintenance.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddBookmarkMaintenanceSteps(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddKitMaintenanceStep(SnapshotsStep, KitMaintenanceOrder.Snapshots, SnapshotAsync);
        services.AddKitMaintenanceStep(ClipFilesStep, KitMaintenanceOrder.Plugin, RetryClipFilesAsync);
        services.AddKitMaintenanceStep(MissingClipsStep, MissingClipsOrder, SweepMissingClipsAsync);
        return services;
    }

    /// <summary>
    /// The daily snapshot of <c>bookmarks.json</c>: <c>SnapshotsToKeep</c> of them in
    /// <c>data/bookmarks/snapshots</c>, none while the store is read-only (the kit store writes none then) or with the
    /// setting at 0.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    /// <param name="cancellationToken">Cancels waiting for another snapshot.</param>
    /// <returns>What was done, for Diagnostics <c>Maintenance</c>.</returns>
    public static async Task<IDictionary<string, object?>?> SnapshotAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var keep = Math.Max(0, services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration().SnapshotsToKeep);
        var store = services.GetRequiredService<IBookmarkStore>();
        var path = await store.Documents.SnapshotAsync(services.GetRequiredService<BookmarkPaths>().SnapshotsDirectory, keep, cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Keep"] = keep,
            ["Snapshot"] = path is null ? null : System.IO.Path.GetFileName(path),
            ["Skipped"] = path is not null ? null : store.IsReadOnly ? "ReadOnly" : keep == 0 ? "SnapshotsToKeep is 0" : "NotWritten"
        };
    }

    /// <summary>
    /// The daily retry of <c>PendingFileDeletes</c>, next to the one at the clip worker's start, after the clip file renames
    /// not finished are finished (<c>RenamesFinished</c>, <c>RenamesWaiting</c> and <c>RenamesUnreadable</c> when there
    /// were any, and a Warning while any waits) and restored clips named again
    /// after their files' renames (<c>ClipsRelinked</c>), with the renames' history kept to its size and the renames of clips
    /// gone for good dropped (by count, never by the missing-clip retention, which was shorter than
    /// the snapshots and exports a restore starts from; <c>RenamesForgotten</c> when any); nothing while the store is
    /// read-only (<see cref="BackgroundWriter.Maintenance"/> asks the gate).
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done, for Diagnostics <c>Maintenance</c>.</returns>
    public static async Task<IDictionary<string, object?>?> RetryClipFilesAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var files = services.GetRequiredService<ClipFileOps>();

        // The clip file renames not finished, before the sweep's step judges their clips; a Warning while any
        // still waits, and the restored clips named again after their files' renames.
        var renames = await files.FinishRenamesAsync(BackgroundWriter.Maintenance, cancellationToken).ConfigureAwait(false);
        files.WarnIfRenamesWait(renames);
        var relinked = await files.RelinkRenamedAsync(BackgroundWriter.Maintenance, cancellationToken).ConfigureAwait(false);
        var store = services.GetRequiredService<IBookmarkStore>();
        var forgotten = store.IsReadOnly ? 0 : files.PruneRenameHistory();

        var retry = await files.RetryPendingDeletesAsync(BackgroundWriter.Maintenance, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Deleted"] = retry.Deleted,
            ["Gone"] = retry.Gone,
            ["Refused"] = retry.Refused,
            ["Referenced"] = retry.Referenced,
            ["Kept"] = retry.Kept
        };
        if (renames.Total > 0)
        {
            result["RenamesFinished"] = renames.Completed + renames.Written + renames.NotMoved + renames.MovedOn + renames.Lost;
            result["RenamesWaiting"] = renames.Waiting;
            result["RenamesUnreadable"] = renames.Unreadable;
        }

        if (relinked > 0)
        {
            result["ClipsRelinked"] = relinked;
        }

        if (forgotten > 0)
        {
            result["RenamesForgotten"] = forgotten;
        }

        return result;
    }

    /// <summary>
    /// The missing-clip sweep (<see cref="MissingClipSweep"/>): clip files gone from a clip folder that is
    /// there marked, those back cleared, clip folders that aren't there left out, then the retention into the archive;
    /// nothing while the store is read-only (<see cref="BackgroundWriter.Maintenance"/> asks the gate).
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    /// <param name="cancellationToken">Cancels between clips and waiting for the store.</param>
    /// <returns>What was done, for Diagnostics <c>Maintenance</c> (Diagnostics <c>ClipSweep</c> keeps it too).</returns>
    public static async Task<IDictionary<string, object?>?> SweepMissingClipsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var result = await services.GetRequiredService<MissingClipSweep>().RunAsync(cancellationToken).ConfigureAwait(false);
        return result.ToDictionary();
    }
}
