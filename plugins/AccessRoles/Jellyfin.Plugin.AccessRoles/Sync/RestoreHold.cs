using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// The library sync's hold after the kit restored the store from a snapshot at load (<see cref="StoreStatus.RestoredFromSnapshot"/>,
/// the kit's restore at load): the roles and assignments are then a snapshot's, possibly older than the users' native access, and
/// a run would write that older state over it. The sync is paused after a restore "until the admin has seen the
/// sync dry run (<c>POST Sync?dryRun=true</c>) and confirmed it"; a restore at load is the same, without an admin who
/// asked for it. Fail closed: nothing is written until an admin confirms with <c>POST Sync?confirmRestore=true</c>, which
/// is refused (409 <c>DryRunRequired</c>) until a dry run was made while held (the status page's buttons
/// do the dry run, then the confirmation).
/// </summary>
/// <remarks>
/// <para>The hold is the status of this process (until confirmed) and a file in the data folder,
/// <see cref="AccessRolesPaths.RestoreHoldFile"/>, written at the first look after such a load, so a restart (whose store
/// then loads <c>Ok</c>, the restored file having been saved) keeps it until the confirmation. Settings locks are not held
/// by it; the admin API still writes the store, so the roles can be put right before the sync is confirmed.</para>
/// <para><b>Each hold has an owner</b> (since 1.0.1): a nonce written in the file as
/// <c>Owner</c> by whoever wrote it. A restore that fails removes the hold only while it is still its own (putting back the
/// hold that was there before it), and a confirmation lifts only the hold it confirmed (<see cref="Current"/>, compared again
/// under <see cref="AccessRolesStore.WithHoldLock{T}"/>), never one a later restore wrote, and nothing while a restore is in
/// progress (<see cref="AccessRolesStore.RestoreInProgress"/>). A hold file of 1.0.0 has no owner: it is held as before,
/// and confirmed as the hold it is.</para>
/// </remarks>
internal static class RestoreHold
{
    private const string Confirm = "POST /AccessRoles/Admin/Sync?dryRun=true to see what the sync would write, then POST /AccessRoles/Admin/Sync?confirmRestore=true&ifPlan=<its PlanToken>.";

    /// <summary>What a confirmation lifts: <see cref="Release"/> lifts this one, or nothing.</summary>
    internal enum ReleaseOutcome
    {
        /// <summary>Nothing was held.</summary>
        NotHeld,

        /// <summary>The confirmed hold was lifted.</summary>
        Released,

        /// <summary>
        /// Another hold is there now (a restore began since the confirmation looked at it), or a restore is in progress:
        /// nothing was lifted.
        /// </summary>
        AnotherHold
    }

    /// <summary>A hold's kind: a file, this process's hold after a restore at load, or a file that can't be read.</summary>
    internal enum HoldKind
    {
        /// <summary>The hold file.</summary>
        File,

        /// <summary>This process's hold, with no file.</summary>
        Process,

        /// <summary>A hold file that can't be read.</summary>
        Unreadable
    }

    /// <summary>
    /// Gets whether the library sync is held: the store was restored from a snapshot at load in this process and no admin
    /// confirmed it since, or the hold file of an earlier process is there.
    /// </summary>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <returns><c>true</c> while held.</returns>
    public static bool IsHeld(AccessRolesPaths paths, AccessRolesStore store) => IsHeld(paths, store, except: null);

    /// <summary>
    /// Gets whether the library sync is held, by another hold than the one a confirmation confirmed: the file's owner is
    /// another (or the file is there again), or a restore is in progress, whose hold no confirmation may lift.
    /// </summary>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <param name="except">The hold the confirmation confirmed (<see cref="Current"/>), or <c>null</c> for any hold.</param>
    /// <returns><c>true</c> while held.</returns>
    public static bool IsHeld(AccessRolesPaths paths, AccessRolesStore store, HoldIdentity? except)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        if (except is not null && store.RestoreInProgress)
        {
            return true;
        }

        if (!File.Exists(paths.RestoreHoldFile))
        {
            return except is null && ProcessHeld(store);
        }

        if (except is null)
        {
            return true;
        }

        return !except.Equals(store.WithHoldLock(() => Read(paths)));
    }

    /// <summary>
    /// Gets the hold there is now, for a confirmation to lift exactly that one: the file's owner and start, or this process's
    /// hold after a restore at load whose file couldn't be written.
    /// </summary>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <returns>The hold, or <c>null</c> when nothing is held.</returns>
    public static HoldIdentity? Current(AccessRolesPaths paths, AccessRolesStore store)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        return store.WithHoldLock(() => File.Exists(paths.RestoreHoldFile) ? Read(paths) : null)
            ?? (ProcessHeld(store) ? HoldIdentity.Process : null);
    }

    /// <summary>
    /// Writes the hold file when the store was restored from a snapshot at load and no admin confirmed it yet, so the hold
    /// outlives a restart. Does nothing otherwise, and never throws (a failure is logged; the process's status still holds).
    /// </summary>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <param name="now">The time, UTC.</param>
    /// <param name="logger">The logger.</param>
    /// <returns><c>true</c> when the file was written now.</returns>
    public static bool Ensure(AccessRolesPaths paths, AccessRolesStore store, DateTime now, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        if (store.Status != StoreStatus.RestoredFromSnapshot || store.RestoreConfirmed || File.Exists(paths.RestoreHoldFile))
        {
            return false;
        }

        try
        {
            var written = store.WithHoldLock(() =>
            {
                if (File.Exists(paths.RestoreHoldFile))
                {
                    return false;
                }

                Write(paths, NewOwner(), now, store.Documents.StatusMessage, snapshot: null);
                return true;
            });
            if (!written)
            {
                return false;
            }

            logger.LogWarning("Access Roles' data file was restored from a snapshot when it loaded: the library sync writes nothing until an admin confirms it (POST /AccessRoles/Admin/Sync?confirmRestore=true)");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Access Roles couldn't write the library sync's hold file {File}; the hold lasts until the server restarts", paths.RestoreHoldFile);
            return false;
        }
    }

    /// <summary>
    /// Holds the sync for an admin's <i>Restore snapshot</i>, before the restored document is saved:
    /// forgets any dry run seen, then writes the hold file with a new owner, so the restore's own document needs its own dry
    /// run and confirmation. Fail closed: when the file can't be written the restore isn't made (it would otherwise be held
    /// only until the server restarts, then written by the next run).
    /// </summary>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <param name="now">The time, UTC.</param>
    /// <param name="snapshot">The snapshot restored.</param>
    /// <returns>The restore's hold, and the one it replaced: <see cref="Cancel"/> puts that back if the save fails.</returns>
    /// <exception cref="PluginException">503: the hold file couldn't be written; nothing was restored.</exception>
    public static HoldTicket Begin(AccessRolesPaths paths, AccessRolesStore store, DateTime now, string snapshot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        var owner = NewOwner();
        try
        {
            return store.WithHoldLock(() =>
            {
                // The dry run seen is forgotten before the new hold is there, so no confirmation counts one for it.
                store.RestoreDryRunSeen = false;
                var existed = File.Exists(paths.RestoreHoldFile);
                string? previous = null;
                if (existed)
                {
                    try
                    {
                        previous = File.ReadAllText(paths.RestoreHoldFile);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Unknown: a failed restore then leaves its own hold rather than lose the earlier one.
                        previous = null;
                    }
                }

                Write(paths, owner, now, "An admin restored the snapshot " + snapshot + ".", snapshot);
                return new HoldTicket(owner, existed, previous);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException(PluginErrorKind.Unavailable, "The library sync's hold file couldn't be written, so the snapshot wasn't restored: the sync would write the restored access before you saw it. The server log has the details.", ex);
        }
    }

    /// <summary>
    /// Takes back the hold <see cref="Begin"/> wrote when the restore's save certainly saved nothing: only while the file is
    /// still its own (a later restore's hold is never touched), putting back the hold that was there before it (an earlier
    /// restore's, whose data is still the stored one), or deleting the file when there was none. Never throws.
    /// </summary>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <param name="ticket">What <see cref="Begin"/> answered.</param>
    /// <param name="logger">The logger.</param>
    public static void Cancel(AccessRolesPaths paths, AccessRolesStore store, HoldTicket ticket, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            store.WithHoldLock(() =>
            {
                if (!File.Exists(paths.RestoreHoldFile) || !string.Equals(Read(paths).Owner, ticket.Owner, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!ticket.Replaced)
                {
                    File.Delete(paths.RestoreHoldFile);
                }
                else if (ticket.Previous is { } previous)
                {
                    File.WriteAllText(paths.RestoreHoldFile, previous);
                }

                return true;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Access Roles couldn't take back the library sync's hold file {File} after a restore that failed; confirm the sync on the Overview", paths.RestoreHoldFile);
        }
    }

    /// <summary>
    /// Records a dry run of the sync while it is held (<c>POST Sync?dryRun=true</c>): in this process, and as
    /// <c>DryRunUtc</c> in the hold file, so it outlives a restart as the hold does. Does nothing while not held; never
    /// throws (a failure to write the file is logged, and this process still knows).
    /// </summary>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <param name="now">The time, UTC.</param>
    /// <param name="logger">The logger.</param>
    public static void RecordDryRun(AccessRolesPaths paths, AccessRolesStore store, DateTime now, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        if (!IsHeld(paths, store))
        {
            return;
        }

        store.RestoreDryRunSeen = true;
        try
        {
            store.WithHoldLock(() =>
            {
                if (!File.Exists(paths.RestoreHoldFile))
                {
                    return false;
                }

                var hold = JsonNode.Parse(File.ReadAllText(paths.RestoreHoldFile)) as JsonObject ?? new JsonObject();
                hold["DryRunUtc"] = now.ToString("O", CultureInfo.InvariantCulture);
                File.WriteAllText(paths.RestoreHoldFile, hold.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return true;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Access Roles couldn't note the sync's dry run in its hold file {File}; this process still knows it", paths.RestoreHoldFile);
        }
    }

    /// <summary>
    /// Gets whether an admin made the sync's dry run since the hold began: in this process, or as the hold file's
    /// <c>DryRunUtc</c> (a hold file is new for every hold, deleted when confirmed).
    /// </summary>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <returns><c>true</c> when the confirmation may lift the hold.</returns>
    public static bool DryRunSeen(AccessRolesPaths paths, AccessRolesStore store)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        if (store.RestoreDryRunSeen)
        {
            return true;
        }

        try
        {
            // Under the hold lock, as every read and write of the file is (1.0.1): a read of its own would hold the file open
            // while a restore's Begin writes it, a sharing violation on Windows that failed that restore with a 503.
            var text = store.WithHoldLock(() => File.Exists(paths.RestoreHoldFile) ? File.ReadAllText(paths.RestoreHoldFile) : null);
            return text is not null
                && JsonNode.Parse(text) is JsonObject hold
                && hold["DryRunUtc"] is JsonValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lifts the hold a confirmation confirmed (an admin's <c>POST Sync?confirmRestore=true</c>): confirmed in this process,
    /// and the hold file deleted, only while it is that hold and no restore is in progress (since
    /// 1.0.1): a hold a restore wrote since stays, and nothing is lifted.
    /// </summary>
    /// <param name="paths">The data folder's paths.</param>
    /// <param name="store">The store.</param>
    /// <param name="confirmed">The hold the confirmation confirmed (<see cref="Current"/>).</param>
    /// <returns>What was lifted.</returns>
    /// <exception cref="IOException">The hold file couldn't be deleted (the hold stays).</exception>
    public static ReleaseOutcome Release(AccessRolesPaths paths, AccessRolesStore store, HoldIdentity confirmed)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(confirmed);
        return store.WithHoldLock(() =>
        {
            if (store.RestoreInProgress)
            {
                return ReleaseOutcome.AnotherHold;
            }

            bool held;
            if (File.Exists(paths.RestoreHoldFile))
            {
                if (!confirmed.Equals(Read(paths)))
                {
                    return ReleaseOutcome.AnotherHold;
                }

                File.Delete(paths.RestoreHoldFile);
                held = true;
            }
            else
            {
                held = ProcessHeld(store);
            }

            store.RestoreConfirmed = true;
            store.RestoreDryRunSeen = false;
            return held ? ReleaseOutcome.Released : ReleaseOutcome.NotHeld;
        });
    }

    private static bool ProcessHeld(AccessRolesStore store) => store.Status == StoreStatus.RestoredFromSnapshot && !store.RestoreConfirmed;

    private static string NewOwner() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static void Write(AccessRolesPaths paths, string owner, DateTime now, string? message, string? snapshot)
    {
        var hold = new JsonObject
        {
            ["Reason"] = SyncReasons.StoreRestored,
            ["SinceUtc"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["Owner"] = owner,
            ["Message"] = message
        };
        if (snapshot is not null)
        {
            hold["Snapshot"] = snapshot;
        }

        hold["Confirm"] = Confirm;
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.RestoreHoldFile, hold.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // The hold file's owner and start; a file that can't be read is one hold too (held, and confirmed as it is), never
    // another's.
    private static HoldIdentity Read(AccessRolesPaths paths)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(paths.RestoreHoldFile)) is JsonObject hold)
            {
                return new HoldIdentity(Text(hold["Owner"]), Text(hold["SinceUtc"]), HoldKind.File);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return HoldIdentity.Unreadable;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>One hold, as a confirmation confirms it.</summary>
    /// <param name="Owner">The file's <c>Owner</c> (none in a 1.0.0 file).</param>
    /// <param name="SinceUtc">The file's <c>SinceUtc</c>.</param>
    /// <param name="Kind">The kind.</param>
    internal sealed record HoldIdentity(string? Owner, string? SinceUtc, HoldKind Kind)
    {
        /// <summary>Gets this process's hold, with no file.</summary>
        public static HoldIdentity Process { get; } = new(null, null, HoldKind.Process);

        /// <summary>Gets a hold file that can't be read.</summary>
        public static HoldIdentity Unreadable { get; } = new(null, null, HoldKind.Unreadable);
    }

    /// <summary>A restore's hold (<see cref="Begin"/>), for <see cref="Cancel"/>.</summary>
    /// <param name="Owner">The owner it wrote.</param>
    /// <param name="Replaced">Whether a hold file was there before it.</param>
    /// <param name="Previous">That file's text, or <c>null</c> when it couldn't be read (the restore's own hold then stays).</param>
    internal sealed record HoldTicket(string Owner, bool Replaced, string? Previous);
}
