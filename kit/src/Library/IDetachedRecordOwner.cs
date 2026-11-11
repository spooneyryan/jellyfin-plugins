using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// A store of item-linked records whose detached records the kit re-resolves, archives after retention, reattaches from
/// the archive and purges, through <see cref="DetachedRecords"/>. Register it as a
/// singleton of its own type and add it with <c>AddKitRecordOwner&lt;T&gt;()</c> next to <c>AddKitDetachedRecords()</c>, which
/// makes it both the verify sweep's <see cref="IAttachedRecordOwner"/> and an <see cref="IDetachedRecordOwner"/>.
/// </summary>
/// <remarks>
/// <para><b>Nothing user-written is hard-deleted on a timer.</b> After <see cref="DetachedRetentionDays"/> a detached record
/// moves into the append-only <c>archive/&lt;Name&gt;-archived.jsonl</c>; it is exported, and still reattaches when its item
/// comes back. The only hard delete is the admin's <i>Purge</i> (<see cref="PurgeDetachedAsync"/> and the archive's own
/// purge), after a snapshot.</para>
/// <para>Every write happens under the store's lock and applies only to records that are still as the kit saw them
/// (detached, the same <c>DetachedUtc</c>), so a user's edit or a relink in between wins. None is called while
/// <see cref="IAttachedRecordOwner.IsReadOnly"/> is true.</para>
/// </remarks>
internal interface IDetachedRecordOwner : IAttachedRecordOwner
{
    /// <summary>
    /// Gets the owner's relink policy (Bookmarks' runtime check, Library Maintenance's Exact only).
    /// </summary>
    RelinkPolicy RelinkPolicy { get; }

    /// <summary>
    /// Gets the days a detached record stays in the live store before it moves to the archive; 0 keeps it there (90 by
    /// default; Bookmarks 0). Read at every run.
    /// </summary>
    int DetachedRetentionDays { get; }

    /// <summary>
    /// Gets the relink policy for one record: <see cref="RelinkPolicy"/> unless the owner overrides it (for example
    /// Bookmarks' own records never take a Strong candidate without a stored runtime, while a Jellyfin Enhanced import
    /// that never attached does). Called for every live detached record and every archived record the kit re-resolves;
    /// it must read memory only and not throw.
    /// </summary>
    /// <param name="record">The record about to be resolved (an <see cref="ArchivedRecord"/> for the reattach).</param>
    /// <returns>The policy to resolve it with.</returns>
    RelinkPolicy GetRelinkPolicy(DetachedRecord record) => RelinkPolicy;

    /// <summary>
    /// Lists the detached records in the live store. Reads memory only.
    /// </summary>
    /// <returns>The records.</returns>
    IReadOnlyList<DetachedRecord> GetDetached();

    /// <summary>
    /// Lists the records a full re-resolution resolves (the daily step, and
    /// <see cref="DetachedRecords.ResolveDetachedAsync(IDetachedRecordOwner, CancellationToken)"/>):
    /// <see cref="GetDetached"/> by default. An owner that keeps some records attached on purpose although their item
    /// is gone (User Ratings' mass-detach guard) may add them, each with the time it was held as its
    /// <see cref="DetachedRecord.DetachedUtc"/>, which its <see cref="ApplyResolutionsAsync"/> must then accept. Only
    /// the resolution reads this; Diagnostics' count, the retention and the purge read <see cref="GetDetached"/>. Reads
    /// memory only.
    /// </summary>
    /// <returns>The records.</returns>
    IReadOnlyList<DetachedRecord> GetResolvable() => GetDetached();

    /// <summary>
    /// Gets whether the live store holds a record with this id (attached or not). The archive ignores its copy of a record
    /// the live store holds, and the reattach never puts back a second one.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <returns>Whether it is live.</returns>
    bool ContainsRecord(string id);

    /// <summary>
    /// Gets whether an archived record is about to leave the archive for good: the owner deleted it, or its member, and
    /// the archive's rewrite that takes it out is still to run (it failed, or runs after the store's commit). Such a
    /// record is in no export (<see cref="DetachedRecords.ExportArchive"/>) and no count
    /// (<see cref="DetachedRecords.Describe"/>). <c>false</c> by default. Reads memory only (it is asked for every
    /// archived record), and must not throw.
    /// </summary>
    /// <param name="record">The archived record.</param>
    /// <returns>Whether its purge is pending.</returns>
    bool IsPurgePending(ArchivedRecord record) => false;

    /// <summary>
    /// Applies re-resolutions of live detached records: a record whose result relinks is attached to the result's item
    /// (its <c>ItemId</c>, a fresh snapshot, <c>AttachedUtc</c>, and after a non-Exact relink the file-specific fields
    /// cleared and the "relinked, check" mark set); the others may keep their outcome (ambiguous) for Diagnostics. Only
    /// records still detached with the same <see cref="DetachedRecord.DetachedUtc"/> are changed.
    /// </summary>
    /// <param name="resolutions">Every resolved record, relinked or not.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>How many records were relinked.</returns>
    Task<int> ApplyResolutionsAsync(IReadOnlyList<DetachedResolution> resolutions, CancellationToken cancellationToken);

    /// <summary>
    /// Moves detached records out of the live store into the archive, under the store's lock: picks the records detached at
    /// or before <paramref name="detachedBefore"/>, passes them to <paramref name="append"/> (which writes them to the archive
    /// and flushes it, or throws, and then nothing is removed), and removes exactly those.
    /// </summary>
    /// <remarks>
    /// Nothing should throw once the store's write has succeeded. If something does, the kit asks
    /// <see cref="ContainsRecord"/> for each archived record: the ones the store still holds have their archived copies
    /// marked superseded (the write failed), and the others keep them (they left the store; the archive holds them now).
    /// </remarks>
    /// <param name="detachedBefore">The retention cutoff.</param>
    /// <param name="append">Writes the records to the archive.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The records archived.</returns>
    Task<IReadOnlyList<ArchivedRecord>> ArchiveAsync(DateTime detachedBefore, Action<IReadOnlyList<ArchivedRecord>> append, CancellationToken cancellationToken);

    /// <summary>
    /// Puts archived records whose item was found back into the live store, attached to it (as
    /// <see cref="ApplyResolutionsAsync"/> relinks one), skipping any whose id the live store holds again.
    /// </summary>
    /// <param name="matches">The archived records, each with a result that relinks.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The ids put back.</returns>
    Task<IReadOnlyList<string>> RestoreFromArchiveAsync(IReadOnlyList<DetachedResolution> matches, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the pre-operation snapshot of the live store (a <c>purge</c> copy) before a purge.
    /// </summary>
    /// <param name="operation">One of <c>Storage.SnapshotOperations</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for another snapshot.</param>
    /// <returns>The snapshot's file name, or null when none was taken.</returns>
    Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes detached records from the live store for good (the admin's <i>Purge</i>).
    /// </summary>
    /// <param name="detachedBefore">Only records detached at or before this time; null for every detached record.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>How many records were deleted.</returns>
    Task<int> PurgeDetachedAsync(DateTime? detachedBefore, CancellationToken cancellationToken);
}
