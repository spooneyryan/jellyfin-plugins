using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The kit's handling of detached item-linked records, for every
/// <see cref="IDetachedRecordOwner"/> of a plugin: re-resolution of live detached records, the retention step that moves
/// them into the <see cref="DetachedArchive"/>, the reattach from the archive, the reconcile at startup, the admin's
/// <i>Purge</i>, the archive's part of an export, and the Diagnostics <c>Detached</c> entry.
/// </summary>
/// <remarks>
/// <para><b>Resolving</b> uses <see cref="IdentityResolver"/> with the owner's <see cref="RelinkPolicy"/> for that record
/// (<see cref="IDetachedRecordOwner.GetRelinkPolicy"/>) and, for a record that belongs to a user, that user's view
/// (<see cref="IdentityResolver.VisibleTo"/>): a record of a user the server doesn't know (an orphan) has no
/// view and is left alone. Weak matches are never applied.</para>
/// <para><b>Retention</b> (<see cref="IDetachedRecordOwner.DetachedRetentionDays"/>, 0 = never): records detached longer
/// than that move to the archive. <b>Reattach</b> re-resolves at most <see cref="MaxReattachPerRun"/> archived
/// records per run, least recently checked first, and moves a matched one back into the live store. Both run in the
/// daily maintenance task.</para>
/// <para><b>An owner's own reasons</b>: <see cref="ArchiveForReasonAsync"/> moves records the owner picks for a reason
/// of its own (Bookmarks' deleted bookmarks whose clip file has been missing longer than its retention) into the same
/// archive, with the retention step's safety, and with their <see cref="ArchivedRecord.Reason"/>. They wait for no
/// item, so the reattach never re-resolves them; it only marks superseded the archived copy of one that is live again
/// (a snapshot restored, an import), as the reconcile does at startup.</para>
/// </remarks>
internal sealed class DetachedRecords
{
    /// <summary>The logger component, <c>Jellyfin.Plugin.&lt;X&gt;.Kit.Detached</c>.</summary>
    public const string LoggerComponent = "Detached";

    /// <summary>How many archived records one reattach run checks per owner.</summary>
    public const int MaxReattachPerRun = 500;

    /// <summary>The 503 of an export while the archive can't be read.</summary>
    public const string ArchiveUnreadableExport = "The archive of detached records can't be read, so an export would leave its records out. The plugin's Diagnostics say why.";

    /// <summary>The 503 of an import (or its dry run) while the archive can't be read.</summary>
    public const string ArchiveUnreadableImport = "The archive of detached records can't be read, so the export's archived records can't be checked or imported. The plugin's Diagnostics say why.";

    /// <summary>The 503 of a purge that includes the archive while the archive can't be read.</summary>
    public const string ArchiveUnreadablePurge = "The archive of detached records can't be read, so nothing was purged. The plugin's Diagnostics say why.";

    /// <summary>
    /// The property of an export's archived record that carries its owner's reason (the export's <c>Archive</c> entries
    /// are the records in their store's shape): left out for a detached record. A record's own field may never have
    /// this name.
    /// </summary>
    public const string ExportReasonProperty = "ArchiveReason";

    /// <summary>
    /// The property of an export's archived record that carries when its owner's reason began (the archive line's detach
    /// time), next to <see cref="ExportReasonProperty"/>.
    /// </summary>
    public const string ExportReasonSinceProperty = "ArchiveReasonSinceUtc";

    private readonly string _archiveDirectory;
    private readonly string _snapshotDirectory;
    private readonly ILibraryManager _library;
    private readonly Func<Guid, User?> _findUser;
    private readonly IdentityResolver _resolver;
    private readonly MoveJournalReader _moves;
    private readonly Func<IEnumerable<IDetachedRecordOwner>> _owners;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DetachedArchive> _archives = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OwnerState> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="DetachedRecords"/> class.
    /// </summary>
    /// <param name="archiveDirectory">The archive folder (<c>&lt;data&gt;/&lt;folder&gt;/archive</c>).</param>
    /// <param name="snapshotDirectory">The snapshot folder, for the copy a purge of an archive takes first.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="findUser">Finds a user by id (never called with the empty GUID), for a record owner's view.</param>
    /// <param name="resolver">The identity resolver.</param>
    /// <param name="moves">The move-journal reader.</param>
    /// <param name="owners">The plugin's owners, asked at every use: <c>AddKitDetachedRecords</c> passes the owners of
    /// <c>AddKitRecordOwner</c>, each created on its own, one that can't be created left out and tried again next time
    /// (<see cref="Hosting.KitComponent{T}"/>).</param>
    /// <param name="logger">The logger (<c>Jellyfin.Plugin.&lt;X&gt;.Kit.Detached</c>).</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public DetachedRecords(
        string archiveDirectory,
        string snapshotDirectory,
        ILibraryManager library,
        Func<Guid, User?> findUser,
        IdentityResolver resolver,
        MoveJournalReader moves,
        Func<IEnumerable<IDetachedRecordOwner>> owners,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archiveDirectory);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(findUser);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(logger);
        _archiveDirectory = archiveDirectory;
        _snapshotDirectory = snapshotDirectory;
        _library = library;
        _findUser = findUser;
        _resolver = resolver;
        _moves = moves;
        _owners = owners;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the plugin's owners that could be created.
    /// </summary>
    public IReadOnlyList<IDetachedRecordOwner> Owners => _owners().Where(o => o is not null).ToList();

    /// <summary>
    /// Gets the snapshot folder, where a purge of an archive puts its copy first (for an owner that purges a deleted
    /// user's archived records itself: <see cref="DetachedArchive.Purge"/>).
    /// </summary>
    public string SnapshotDirectory => _snapshotDirectory;

    /// <summary>
    /// Gets an owner's archive.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <returns>Its archive (<c>archive/&lt;Name&gt;-archived.jsonl</c>).</returns>
    public DetachedArchive ArchiveOf(IDetachedRecordOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _archives.GetOrAdd(owner.Name, name => new DetachedArchive(_archiveDirectory, name, _logger, _timeProvider));
    }

    /// <summary>
    /// Re-resolves an owner's live detached records and relinks those the resolver finds (on Added and Updated batches,
    /// and daily), with the attached ones the owner adds to them (<see cref="IDetachedRecordOwner.GetResolvable"/>).
    /// Returns at once for a read-only owner.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done.</returns>
    public Task<DetachedRunResult> ResolveDetachedAsync(IDetachedRecordOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return ResolveDetachedAsync(owner, null, cancellationToken);
    }

    /// <summary>
    /// Re-resolves some of an owner's live detached records: those a library-change batch's items could
    /// match, which the owner picked out with an in-memory key set first, so a batch never resolves every detached record of
    /// a store that keeps many. The same step as <see cref="ResolveDetachedAsync(IDetachedRecordOwner, CancellationToken)"/>
    /// otherwise: the owner's policy and view, its <see cref="IDetachedRecordOwner.ApplyResolutionsAsync"/> with every
    /// result, the outcomes in Diagnostics. Returns at once for a read-only owner; an empty list resolves nothing.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="records">The owner's detached records to resolve, or null for all of them
    /// (<see cref="IDetachedRecordOwner.GetResolvable"/>: the detached ones, and those the owner adds).</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done.</returns>
    public async Task<DetachedRunResult> ResolveDetachedAsync(IDetachedRecordOwner owner, IReadOnlyList<DetachedRecord>? records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        const string Step = "Resolve";
        if (owner.IsReadOnly)
        {
            return Remember(owner, new DetachedRunResult { Step = Step, Owner = owner.Name, Skipped = "ReadOnly" });
        }

        var (resolutions, unknown) = Resolve(owner, records ?? owner.GetResolvable());
        State(owner).RecordOutcomes(resolutions);
        var relinked = resolutions.Count == 0 ? 0 : await owner.ApplyResolutionsAsync(resolutions, cancellationToken).ConfigureAwait(false);
        if (relinked > 0)
        {
            _logger.LogInformation("{Relinked} detached records of {Owner} were relinked", relinked, owner.Name);
        }

        return Remember(owner, new DetachedRunResult
        {
            Step = Step,
            Owner = owner.Name,
            Checked = resolutions.Count + unknown,
            Relinked = relinked,
            Ambiguous = resolutions.Count(r => r.Result.Outcome == ResolveOutcome.Ambiguous),
            OwnerUnknown = unknown
        });
    }

    /// <summary>
    /// The retention step: moves the owner's records detached longer than its
    /// <see cref="IDetachedRecordOwner.DetachedRetentionDays"/> into its archive. Nothing when the setting is 0 or the owner
    /// is read-only. When the owner throws after the archive was written, the archived copies of the records its store still
    /// holds (<see cref="IDetachedRecordOwner.ContainsRecord"/>: its write failed) are marked superseded at once; those it no
    /// longer holds (it threw after its write) stay archived. The owner's exception is passed on.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done.</returns>
    public async Task<DetachedRunResult> ArchiveDueAsync(IDetachedRecordOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        const string Step = "Archive";
        if (owner.IsReadOnly)
        {
            return Remember(owner, new DetachedRunResult { Step = Step, Owner = owner.Name, Skipped = "ReadOnly" });
        }

        var days = owner.DetachedRetentionDays;
        if (days <= 0)
        {
            return Remember(owner, new DetachedRunResult { Step = Step, Owner = owner.Name, Skipped = "RetentionOff" });
        }

        var archive = ArchiveOf(owner);
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(days);
        IReadOnlyList<ArchivedRecord>? appended = null;
        try
        {
            var moved = await owner.ArchiveAsync(cutoff, records => appended = archive.Append(records), cancellationToken).ConfigureAwait(false);
            if (moved.Count > 0)
            {
                _logger.LogInformation("{Count} records of {Owner} detached for more than {Days} days moved to its archive", moved.Count, owner.Name, days);
            }

            return Remember(owner, new DetachedRunResult { Step = Step, Owner = owner.Name, Checked = moved.Count, Archived = moved.Count });
        }
        catch (Exception) when (appended is { Count: > 0 })
        {
            SupersedeStillLive(owner, archive, appended);
            throw;
        }
    }

    /// <summary>
    /// Moves records the owner picks for a reason of its own into its archive (nothing user-written is hard-deleted on
    /// a timer): Bookmarks' deleted bookmarks whose clip file has been missing longer than its retention
    /// (<c>ClipMissing</c>). The owner's <paramref name="move"/> picks the records under its store's lock and passes
    /// them to the append it is given, which gives each <paramref name="reason"/>, writes them to the archive and
    /// flushes it (or throws, and then nothing may leave the store), and then removes exactly those from its store. As
    /// for the retention step, when <paramref name="move"/> throws after the archive was written, the archived copies
    /// of the records the store still holds are marked superseded at once, and those of the records that left it stay.
    /// Nothing while the owner is read-only. The records archived this way are never re-resolved by
    /// <see cref="ReattachFromArchiveAsync"/>.
    /// </summary>
    /// <param name="owner">The owner, whose archive takes the records.</param>
    /// <param name="reason">Why (<see cref="ArchivedRecord.Reason"/>): one word, letters and digits.</param>
    /// <param name="move">Picks the records under the store's lock, appends them (the archive's records as written come back)
    /// and removes them from the store; returns the records archived.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done (step <c>Archive</c> followed by the reason).</returns>
    /// <exception cref="ArgumentException">The reason is empty or not one word.</exception>
    public async Task<DetachedRunResult> ArchiveForReasonAsync(
        IDetachedRecordOwner owner,
        string reason,
        Func<Func<IReadOnlyList<ArchivedRecord>, IReadOnlyList<ArchivedRecord>>, CancellationToken, Task<IReadOnlyList<ArchivedRecord>>> move,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        ArgumentNullException.ThrowIfNull(move);
        if (!IsReason(reason))
        {
            throw new ArgumentException("An archive reason is one word of letters and digits.", nameof(reason));
        }

        var step = "Archive" + reason;
        if (owner.IsReadOnly)
        {
            return Remember(owner, new DetachedRunResult { Step = step, Owner = owner.Name, Skipped = "ReadOnly" });
        }

        var archive = ArchiveOf(owner);
        var appended = new List<ArchivedRecord>();
        try
        {
            var moved = await move(
                records =>
                {
                    ArgumentNullException.ThrowIfNull(records);
                    var written = archive.Append(records.Select(r => r with { Reason = reason }).ToList());
                    appended.AddRange(written);
                    return written;
                },
                cancellationToken).ConfigureAwait(false);
            if (moved.Count > 0)
            {
                _logger.LogInformation("{Count} records of {Owner} moved to its archive ({Reason})", moved.Count, owner.Name, reason);
            }

            return Remember(owner, new DetachedRunResult { Step = step, Owner = owner.Name, Checked = moved.Count, Archived = moved.Count });
        }
        catch (Exception) when (appended.Count > 0)
        {
            SupersedeStillLive(owner, archive, appended);
            throw;
        }
    }

    /// <summary>
    /// Reattaches archived records whose items are back: the next <see cref="MaxReattachPerRun"/> records, least
    /// recently checked first, are re-resolved; the matched ones go back into the live store (then the archive says
    /// so), and the archive remembers where the run left off. Records the owner archived for a reason of its own
    /// (<see cref="ArchivedRecord.Reason"/>) wait for no item: they are never re-resolved, and the archived copy of one
    /// the live store holds again is marked superseded.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was done.</returns>
    public async Task<DetachedRunResult> ReattachFromArchiveAsync(IDetachedRecordOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        const string Step = "Reattach";
        if (owner.IsReadOnly)
        {
            return Remember(owner, new DetachedRunResult { Step = Step, Owner = owner.Name, Skipped = "ReadOnly" });
        }

        var archive = ArchiveOf(owner);

        // A record archived for its owner's reason that is live again (a snapshot restored, an import) counts as live, as the
        // reconcile at startup decides for every record: its archived copy goes, so it never comes back from here later.
        archive.MarkSuperseded(archive.Read().Records.Where(r => r.Reason is not null && owner.ContainsRecord(r.Id)).Select(r => r.Id).ToList());
        var batch = archive.NextToCheck(MaxReattachPerRun, static r => r.Reason is null);
        if (batch.Count == 0)
        {
            return Remember(owner, new DetachedRunResult { Step = Step, Owner = owner.Name });
        }

        var live = batch.Where(r => owner.ContainsRecord(r.Id)).Select(r => r.Id).ToList();
        archive.MarkSuperseded(live);
        var (resolutions, unknown) = Resolve(owner, batch.Where(r => !live.Contains(r.Id)).ToList());
        var matches = resolutions.Where(r => r.Result.ShouldRelink).ToList();
        IReadOnlyList<string> restored = matches.Count == 0 ? [] : await owner.RestoreFromArchiveAsync(matches, cancellationToken).ConfigureAwait(false);
        archive.MarkReattached(restored);
        archive.MarkChecked(batch[^1].Seq);
        if (restored.Count > 0)
        {
            _logger.LogInformation("{Count} archived records of {Owner} were reattached and moved back", restored.Count, owner.Name);
        }

        return Remember(owner, new DetachedRunResult
        {
            Step = Step,
            Owner = owner.Name,
            Checked = batch.Count,
            Relinked = restored.Count,
            Ambiguous = resolutions.Count(r => r.Result.Outcome == ResolveOutcome.Ambiguous),
            OwnerUnknown = unknown
        });
    }

    /// <summary>
    /// Marks the archived copies of records the live store holds as superseded (a crash between the archive's write and
    /// the store's), for every owner that isn't read-only. Runs once after the startup gate.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task.</returns>
    public Task ReconcileAsync(CancellationToken cancellationToken)
    {
        foreach (var owner in Owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (owner.IsReadOnly || !File.Exists(ArchiveOf(owner).FilePath))
                {
                    continue;
                }

                var archive = ArchiveOf(owner);
                var live = archive.Read().Records.Where(r => owner.ContainsRecord(r.Id)).Select(r => r.Id).ToList();
                if (live.Count > 0)
                {
                    archive.MarkSuperseded(live);
                    _logger.LogWarning("{Count} records of {Owner} were in both its store and its archive; the store's copies count", live.Count, owner.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The archive of {Owner} couldn't be reconciled with its store", owner.Name);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The admin's <i>Purge</i> (<c>POST /&lt;Route&gt;/Admin/Detached/Purge?olderThanDays=&amp;includeArchive=</c>),
    /// the only hard delete of item-linked records: after a <c>purge</c> pre-operation snapshot of the live store (and
    /// a copy of the archive, when it is included), the detached records, detached at least that many days ago, are
    /// deleted.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="olderThanDays">Only records detached at least this many days ago; null for every detached record.</param>
    /// <param name="includeArchive">Whether archived records go too.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was deleted.</returns>
    /// <exception cref="PluginException">409 when the owner's data is read-only; 400 for a negative day count; 503 when the
    /// archive is included and can't be read (checked before anything is deleted) or rewritten.</exception>
    public async Task<DetachedRunResult> PurgeAsync(IDetachedRecordOwner owner, int? olderThanDays, bool includeArchive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (olderThanDays < 0)
        {
            throw PluginException.Invalid("olderThanDays can't be negative.");
        }

        if (owner.IsReadOnly)
        {
            throw PluginException.Conflict("The data is read-only, so nothing can be purged. The plugin's Diagnostics say why.");
        }

        if (includeArchive)
        {
            // Before the live store is touched, so an archive that can't be read doesn't leave the purge half done.
            ReadArchive(owner, ArchiveUnreadablePurge);
        }

        DateTime? cutoff = olderThanDays is { } days ? _timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(days) : null;
        var snapshots = new List<string>();
        var purged = 0;
        if (owner.GetDetached().Any(r => cutoff is null || r.DetachedUtc <= cutoff))
        {
            if (await owner.SnapshotBeforeAsync(SnapshotOperations.Purge, cancellationToken).ConfigureAwait(false) is { } snapshot)
            {
                snapshots.Add(snapshot);
            }

            purged = await owner.PurgeDetachedAsync(cutoff, cancellationToken).ConfigureAwait(false);
        }

        if (includeArchive)
        {
            int archived;
            string? copy;
            try
            {
                (archived, copy) = ArchiveOf(owner).Purge(r => cutoff is null || r.DetachedUtc <= cutoff, _snapshotDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The archive went bad since the check, or its rewrite failed (the file is unchanged then).
                _logger.LogError(ex, "Purge: {Count} detached records of {Owner} deleted, but its archive couldn't be purged", purged, owner.Name);
                throw new PluginException(
                    PluginErrorKind.Unavailable,
                    string.Create(CultureInfo.InvariantCulture, $"{purged} detached records were purged, but the archive couldn't be: nothing was deleted from it. The server log has the details."),
                    ex);
            }

            purged += archived;
            if (copy is not null)
            {
                snapshots.Add(copy);
            }
        }

        _logger.LogInformation("Purge: {Count} detached records of {Owner} deleted", purged, owner.Name);
        return Remember(owner, new DetachedRunResult { Step = "Purge", Owner = owner.Name, Purged = purged, Snapshots = snapshots });
    }

    /// <summary>
    /// The owner's archived records in its store's shape, for an export's <c>Archive</c>. One archived for the owner's
    /// own reason (<see cref="ArchiveForReasonAsync"/>) carries it in <see cref="ExportReasonProperty"/>, and when it
    /// began in <see cref="ExportReasonSinceProperty"/>, so an import keeps both for any owner
    /// (<see cref="ReadExported"/>); a detached record's entry is its record alone, as before.
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <returns>Copies of the records.</returns>
    /// <exception cref="PluginException">503 when the archive can't be read: an export without its records would be a
    /// backup that loses them (as <see cref="JsonDocumentStore{TDoc}.ExportData"/> refuses an export it can't make
    /// faithfully).</exception>
    public IReadOnlyList<JsonObject> ExportArchive(IDetachedRecordOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return ReadArchive(owner, ArchiveUnreadableExport).Records
            .Where(r => !owner.ContainsRecord(r.Id) && !owner.IsPurgePending(r))
            .Select(ToExported)
            .ToList();
    }

    /// <summary>
    /// Splits an export's archived record (an entry of its <c>Archive</c>) into the record as its store has it and what
    /// the kit's export carries next to it: the owner's reason and when it began. An owner's import adapter builds its
    /// <see cref="ArchivedRecord"/> from <see cref="ExportedArchivedRecord.Record"/> and passes it through
    /// <see cref="ExportedArchivedRecord.WithExportedFacts"/> before <see cref="ImportArchived"/>. A reason that isn't
    /// one word of letters and digits is left out, as is a time that isn't one.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The record, without the kit's properties, and the facts.</returns>
    public static ExportedArchivedRecord ReadExported(JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var record = (JsonObject)entry.DeepClone();
        var reason = record[ExportReasonProperty] is JsonValue reasonValue && reasonValue.TryGetValue<string>(out var text) && IsReason(text) ? text : null;
        var since = reason is null ? null : ReadUtc(record[ExportReasonSinceProperty]);
        record.Remove(ExportReasonProperty);
        record.Remove(ExportReasonSinceProperty);
        return new ExportedArchivedRecord(record, reason, since);
    }

    /// <summary>
    /// Adds imported archived records to the owner's archive: those that are neither live nor archived already. With
    /// <paramref name="replace"/> (an envelope Replace), the archive becomes these records, those not live, and the
    /// ones it had go (<see cref="DetachedArchive.ReplaceRecords"/>; take a pre-operation copy first).
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="records">The records, built by the plugin's import adapter from the export's <c>Archive</c>.</param>
    /// <param name="dryRun">Whether nothing is written.</param>
    /// <param name="replace">Whether the archive becomes these records.</param>
    /// <returns>The records that were (or would be) added; with <paramref name="replace"/>, the archive's records.</returns>
    /// <exception cref="PluginException">503 when the archive can't be read (a dry run too: it couldn't tell what is
    /// archived already); 409 when the owner's data is read-only and something would be written.</exception>
    public IReadOnlyList<ArchivedRecord> ImportArchived(IDetachedRecordOwner owner, IReadOnlyList<ArchivedRecord> records, bool dryRun, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(records);
        var archive = ArchiveOf(owner);
        var current = ReadArchive(owner, ArchiveUnreadableImport).Records;
        var archived = replace ? new HashSet<string>(StringComparer.Ordinal) : current.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var added = records.Where(r => r is not null && !archived.Contains(r.Id) && !owner.ContainsRecord(r.Id))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        if (dryRun || (added.Count == 0 && (!replace || current.Count == 0)))
        {
            return added;
        }

        if (owner.IsReadOnly)
        {
            throw PluginException.Conflict("The data is read-only, so the export's archived records can't be imported.");
        }

        return replace ? archive.ReplaceRecords(added) : archive.Append(added);
    }

    /// <summary>
    /// Describes every owner for Diagnostics (<c>Detached</c>, the Diagnostics shape's <c>Detached:{Count, Ambiguous,
    /// OldestDays, Archived, PendingRelinks}</c>), from memory and the archive files: the detached records, those left
    /// ambiguous by the last resolution, the oldest's age in days, the archived records, the attached records waiting
    /// on a pending relink, the retention, and the last run of each step.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var journal = _moves.Read();
        var pendingIds = journal.PendingRelinks(now).Select(l => l.NewId).ToHashSet();
        var owners = new List<IDictionary<string, object?>>();
        foreach (var owner in Owners)
        {
            try
            {
                IReadOnlyList<DetachedRecord> detached = owner.IsReadOnly ? [] : owner.GetDetached();
                var archive = ArchiveOf(owner).Read();
                var state = State(owner);
                var oldest = detached.Count == 0 ? (DateTime?)null : detached.Min(r => r.DetachedUtc);
                var archived = archive.Records.Where(r => !owner.ContainsRecord(r.Id) && !owner.IsPurgePending(r)).ToList();
                owners.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Owner"] = owner.Name,
                    ["ReadOnly"] = owner.IsReadOnly,
                    ["Count"] = detached.Count,
                    ["Ambiguous"] = detached.Count(r => state.IsAmbiguous(r)),
                    ["OldestDays"] = oldest is { } o ? (int)Math.Floor((now - o).TotalDays) : null,
                    ["Archived"] = archived.Count,

                    // Of those, the ones the owner archived for a reason of its own, by reason.
                    ["ArchivedFor"] = archived.Where(r => r.Reason is not null)
                        .GroupBy(r => r.Reason!, StringComparer.Ordinal)
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                    ["ArchiveProblem"] = archive.Problem,
                    ["PendingRelinks"] = owner.IsReadOnly ? 0 : owner.GetAttachedItemIds().Count(id => pendingIds.Contains(id) && !LibraryPresence.IsInLibrary(_library, id)),
                    ["RetentionDays"] = owner.DetachedRetentionDays,
                    ["LastRuns"] = state.LastRuns()
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                owners.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["Owner"] = owner.Name, ["Error"] = ex.Message });
            }
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Owners"] = owners,
            ["Summary"] = string.Join("; ", owners.Where(o => o.ContainsKey("Count")).Select(o => $"{o["Owner"]}: {o["Count"]} detached, oldest {o["OldestDays"] ?? 0} days; {o["Archived"]} archived"))
        };
    }

    private (List<DetachedResolution> Resolutions, int OwnerUnknown) Resolve(IDetachedRecordOwner owner, IReadOnlyList<DetachedRecord> records)
    {
        var journal = _moves.Read();
        var views = new Dictionary<Guid, Func<BaseItem, bool>?>();
        var resolutions = new List<DetachedResolution>();
        var unknown = 0;
        foreach (var record in records)
        {
            if (record?.Identity is null)
            {
                continue;
            }

            Func<BaseItem, bool>? isVisible = null;
            if (record.UserId is { } userId)
            {
                if (!views.TryGetValue(userId, out isVisible))
                {
                    var user = userId == Guid.Empty ? null : _findUser(userId);
                    isVisible = user is null ? null : IdentityResolver.VisibleTo(_library, user);
                    views[userId] = isVisible;
                }

                if (isVisible is null)
                {
                    // The server doesn't know the user: no view to resolve in, so the record stays as it is.
                    unknown++;
                    continue;
                }
            }

            var policy = owner.GetRelinkPolicy(record);
            resolutions.Add(new DetachedResolution(record, _resolver.Resolve(record.Identity, policy, journal, record.AttachedUtc, isVisible)));
        }

        return (resolutions, unknown);
    }

    // An owner's reason: one word of ASCII letters and digits.
    private static bool IsReason(string? reason) => !string.IsNullOrEmpty(reason) && reason.All(char.IsAsciiLetterOrDigit);

    // A time in an export's JSON, as UTC: one with an offset converted, one without taken as UTC; null when it isn't one.
    private static DateTime? ReadUtc(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<DateTime>(out var when))
        {
            return when.Kind switch
            {
                DateTimeKind.Utc => when,
                DateTimeKind.Local => when.ToUniversalTime(),
                _ => DateTime.SpecifyKind(when, DateTimeKind.Utc)
            };
        }

        return value.TryGetValue<string>(out var text)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)
                ? when
                : null;
    }

    // An archived record as an export's Archive carries it: its record, and for one archived for its owner's reason, that
    // reason and when it began, in the kit's two properties.
    private static JsonObject ToExported(ArchivedRecord record)
    {
        var exported = (JsonObject)record.Record.DeepClone();
        if (record.Reason is { } reason)
        {
            exported[ExportReasonProperty] = reason;
            exported[ExportReasonSinceProperty] = record.DetachedUtc;
        }

        return exported;
    }

    // The archive as it is, for the paths that must not act on part of it (an export, an import's check, a purge that
    // includes it): one that can't be read is a 503, as the store's own export is, before anything is written.
    private ArchiveContents ReadArchive(IDetachedRecordOwner owner, string refusal)
    {
        var contents = ArchiveOf(owner).Read();
        if (contents.Problem is not null)
        {
            _logger.LogWarning("The archive of {Owner} can't be read, so the request was refused: {Problem}", owner.Name, contents.Problem);
            throw PluginException.Unavailable(refusal);
        }

        return contents;
    }

    // The owner threw after the archive was written. The records its store still holds stayed live (its write failed), so
    // their archived copies must not count, or a record its user deletes later would come back from the archive. The ones
    // it no longer holds left the store (it threw after its write): their archived copies are all there is, and stay. A
    // failure here is logged and never hides the owner's exception; the next start's reconcile catches up.
    private void SupersedeStillLive(IDetachedRecordOwner owner, DetachedArchive archive, IReadOnlyList<ArchivedRecord> appended)
    {
        try
        {
            var live = appended.Select(r => r.Id).Where(owner.ContainsRecord).ToList();
            archive.MarkSuperseded(live);
            if (live.Count < appended.Count)
            {
                _logger.LogWarning(
                    "{Count} records of {Owner} left its store for its archive although archiving then failed; their archived copies count",
                    appended.Count - live.Count,
                    owner.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The archived copies of records {Owner} kept couldn't be marked superseded; the reconcile at the next start does it", owner.Name);
        }
    }

    private OwnerState State(IDetachedRecordOwner owner) => _states.GetOrAdd(owner.Name, _ => new OwnerState());

    private DetachedRunResult Remember(IDetachedRecordOwner owner, DetachedRunResult result)
    {
        State(owner).Remember(result, _timeProvider.GetUtcNow().UtcDateTime);
        return result;
    }

    /// <summary>What Diagnostics shows of an owner between runs.</summary>
    private sealed class OwnerState
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (DateTime Utc, DetachedRunResult Result)> _lastRuns = new(StringComparer.Ordinal);
        private Dictionary<string, DateTime> _ambiguous = new(StringComparer.Ordinal);

        public void RecordOutcomes(IReadOnlyList<DetachedResolution> resolutions)
        {
            var ambiguous = resolutions.Where(r => r.Result.Outcome == ResolveOutcome.Ambiguous)
                .ToDictionary(r => r.Record.Id, r => r.Record.DetachedUtc, StringComparer.Ordinal);
            lock (_lock)
            {
                _ambiguous = ambiguous;
            }
        }

        public bool IsAmbiguous(DetachedRecord record)
        {
            lock (_lock)
            {
                return _ambiguous.TryGetValue(record.Id, out var detachedUtc) && detachedUtc == record.DetachedUtc;
            }
        }

        public void Remember(DetachedRunResult result, DateTime utc)
        {
            lock (_lock)
            {
                _lastRuns[result.Step] = (utc, result);
            }
        }

        public List<IDictionary<string, object?>> LastRuns()
        {
            lock (_lock)
            {
                return _lastRuns.Values.OrderBy(r => r.Utc).Select(r =>
                {
                    var entry = r.Result.ToDictionary();
                    entry["Utc"] = r.Utc;
                    return entry;
                }).ToList();
            }
        }
    }
}
