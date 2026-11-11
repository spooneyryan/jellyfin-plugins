using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// The envelope export and import of User Ratings, with the kit's <see cref="ExportEnvelope"/>.
/// </summary>
/// <remarks>
/// <para><b>Export:</b> every rating, attached and detached, with its identity, the former members, the moderation log, the
/// import ledger and its rows, the tombstones, and the archive (the kit's <see cref="DetachedRecords.ExportArchive"/>, which
/// leaves out a rating whose archive purge is pending; each line carries its identity, so an import can resolve it).</para>
/// <para><b>Import</b> (a job): the envelope is read and checked (400 another plugin's, 409 a newer schema), its users matched
/// (<see cref="ImportUserMap"/>; a user deleted on this server, or unknown, is never matched), its rows resolved outside the
/// writer lock (the item id, then the kit's resolver in the member's view; a row that doesn't resolve is stored detached), then
/// written in one transaction under the writer lock, after the kit's pre-operation <c>import</c> snapshot in the same hold. A
/// dry run is the same work rolled back, and answers a <c>PlanToken</c> over the file and every row's decision; the real run
/// needs it (<c>ifPlan</c>) and writes nothing, 409 <c>Changed</c>, when a decision differs. A rating with a tombstone here or
/// in the envelope never comes back; nor does a deleted user's rating, nor a former member's whose user this server deleted
/// and never anonymized. Merge: per rating id (or the earlier import's copy, or the member's rating on the resolved item) the
/// newer <c>UpdatedUtc</c> wins, equal times keep this server's; the envelope's tombstones, former members and ledger are
/// merged in, and a live rating its tombstone deleted later is deleted. Replace: every rating, the log and the ledger are
/// replaced by the envelope's; the tombstones and former members stay, merged with the envelope's.</para>
/// </remarks>
internal sealed class RatingsEnvelope
{
    /// <summary>The data's schema version in an export.</summary>
    public const int DataSchemaVersion = 1;

    /// <summary>The ledger's source for envelope rows (key: the row's id).</summary>
    public const string LedgerSource = "Envelope";

    /// <summary>The 409's code when the plan changed since the dry run.</summary>
    public const string ChangedCode = "Changed";

    /// <summary>The 409's words when the plan changed since the dry run.</summary>
    public const string ChangedMessage = "The import would now do something other than its dry run showed (the data, the library or the users changed since). Nothing was written: run the dry run again.";

    private readonly IRatingsStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly Func<IdentityResolver> _resolver;
    private readonly Func<DetachedRecords> _detached;
    private readonly Func<RatingsLibraryConsumer> _owner;
    private readonly Func<KnownUsers> _knownUsers;
    private readonly Func<IReadOnlySet<Guid>> _deletedUsers;
    private readonly ArchivedLinePurges _archivedLines;
    private readonly string? _serverId;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsEnvelope"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="library">The library.</param>
    /// <param name="users">The users.</param>
    /// <param name="resolver">The kit's identity resolver.</param>
    /// <param name="detached">The kit's detached records (the archive).</param>
    /// <param name="owner">The ratings' record owner.</param>
    /// <param name="knownUsers">The last-seen names.</param>
    /// <param name="deletedUsers">The users deleted on this server.</param>
    /// <param name="archivedLines">The archive's lines by rating id.</param>
    /// <param name="serverId">The server's id, for exports.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public RatingsEnvelope(
        IRatingsStore store,
        ILibraryManager library,
        IUserManager users,
        Func<IdentityResolver> resolver,
        Func<DetachedRecords> detached,
        Func<RatingsLibraryConsumer> owner,
        Func<KnownUsers> knownUsers,
        Func<IReadOnlySet<Guid>> deletedUsers,
        ArchivedLinePurges archivedLines,
        string? serverId,
        TimeProvider clock,
        ILogger logger)
    {
        _store = store;
        _library = library;
        _users = users;
        _resolver = resolver;
        _detached = detached;
        _owner = owner;
        _knownUsers = knownUsers;
        _deletedUsers = deletedUsers;
        _archivedLines = archivedLines;
        _serverId = serverId;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Builds the export.
    /// </summary>
    /// <returns>The envelope and its counts.</returns>
    /// <exception cref="PluginException">503 the database or the archive can't be read.</exception>
    public (ExportEnvelope Envelope, IDictionary<string, object?> Counts) Export()
    {
        var tables = _store.ReadTables();
        var owner = _owner();
        var detached = _detached();
        var exportedArchive = detached.ExportArchive(owner);
        var identities = detached.ArchiveOf(owner).Read().Records
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Identity, StringComparer.OrdinalIgnoreCase);
        var archive = new List<JsonObject>();
        var archiveUsers = new List<Guid>();
        foreach (var entry in exportedArchive)
        {
            var copy = (JsonObject)entry.DeepClone();
            var id = copy["Id"]?.GetValue<string>();
            if (id is not null && Guid.TryParse(id, out var guid) && identities.TryGetValue(guid.ToString("D", CultureInfo.InvariantCulture), out var identity))
            {
                copy["Identity"] = JsonSerializer.SerializeToNode(identity, JsonStoreFormat.Options);
            }

            if (copy["UserId"]?.GetValue<string>() is { } user && Guid.TryParse(user, out var userId))
            {
                archiveUsers.Add(userId);
            }

            archive.Add(copy);
        }

        var serverUsers = ServerUsers();
        var former = tables.FormerUsers.Select(f => f.Id).ToHashSet();
        var known = _knownUsers();
        var data = new JsonObject
        {
            ["Ratings"] = Array(tables.Ratings.Select(r => ExportRating.Of(r, former.Contains(r.UserId) ? null : NameOf(r.UserId, serverUsers, known)))),
            ["FormerUsers"] = Array(tables.FormerUsers),
            ["ModerationLog"] = Array(tables.ModerationLog.Select(ExportLogRow.Of)),
            ["ImportLedger"] = Array(tables.ImportLedger.Select(l => new ExportLedger(l.Source, l.SourceKey, l.SourceUpdatedUtc, l.ImportedUtc))),
            ["ImportLedgerRows"] = Array(tables.ImportLedger.SelectMany(l => l.RatingIds.Select(id => new ExportLedgerRow(l.Source, l.SourceKey, id)))),
            ["Tombstones"] = Array(tables.Tombstones)
        };

        var users = ExportUser.For(
            tables.Ratings.Select(r => r.UserId).Concat(archiveUsers).Where(id => !former.Contains(id)),
            known,
            serverUsers,
            _deletedUsers());
        // The plugin's clock, not the system's (the kit's default): the export job's file and GET Export of the same data are
        // the same bytes in the same second of that clock (a test of the two was flaky without it).
        var envelope = ExportEnvelope.Create(UserRatingsKit.Options, new StoreExportData(DataSchemaVersion, data), users, archive: archive, serverId: _serverId, exportedUtc: _clock.GetUtcNow().UtcDateTime);
        var counts = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Ratings"] = tables.Ratings.Count,
            ["Reviews"] = tables.Ratings.Count(r => r.ReviewText is not null),
            ["Detached"] = tables.Ratings.Count(r => r.DetachedUtc is not null),
            ["FormerUsers"] = tables.FormerUsers.Count,
            ["ModerationLog"] = tables.ModerationLog.Count,
            ["ImportLedger"] = tables.ImportLedger.Count,
            ["Tombstones"] = tables.Tombstones.Count,
            ["Archived"] = archive.Count
        };
        return (envelope, counts);
    }

    /// <summary>
    /// Runs an import from a stored file, as a job.
    /// </summary>
    /// <param name="file">The envelope, stored in the plugin's <c>tmp/</c>.</param>
    /// <param name="request">Mode, dry run and user matching.</param>
    /// <param name="ifPlan">The dry run's <c>PlanToken</c>; required for a real run.</param>
    /// <param name="adminId">The admin, for the moderation log; null for an API key.</param>
    /// <param name="job">The job, for progress and the write phase.</param>
    /// <param name="cancellationToken">Cancels it before the write phase.</param>
    /// <returns>The report.</returns>
    /// <exception cref="PluginException">400 not this plugin's export or no <c>ifPlan</c>; 409 a newer schema, read-only data,
    /// <see cref="ChangedCode"/>; 503 the database or the archive can't be read or written.</exception>
    public async Task<IDictionary<string, object?>> ImportAsync(string file, ImportRequest request, string? ifPlan, Guid? adminId, AdminJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(job);
        job.Progress("Read", 0, 0);
        var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        var fileHash = Convert.ToHexString(SHA256.HashData(bytes));
        var envelope = ExportEnvelope.Parse(bytes);
        envelope.EnsureFor(UserRatingsKit.Options, DataSchemaVersion);
        _store.ThrowIfReadOnly();
        var parsed = Parsed.Read(envelope.Data);
        var archiveRows = ArchiveRow.ReadAll(envelope.Archive, parsed.Unusable);
        var former = parsed.FormerUsers.ToDictionary(f => f.Id);
        var counts = parsed.Ratings.Where(r => !former.ContainsKey(r.UserId)).GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var entry in envelope.Archive)
        {
            if (entry?["UserId"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var archivedUser) && !former.ContainsKey(archivedUser))
            {
                counts[archivedUser] = counts.GetValueOrDefault(archivedUser) + 1;
            }
        }

        var deleted = _deletedUsers();
        var users = ImportUserMap.Build(counts, envelope.Users, ServerUsers(), deleted, request.MapUsersBy);

        // Resolve, outside the writer lock: each row's item, in its member's view.
        var resolved = new Dictionary<Guid, Target>();
        for (var i = 0; i < parsed.Ratings.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            job.Progress("Resolve", i, parsed.Ratings.Count);
            var row = parsed.Ratings[i];
            var member = former.ContainsKey(row.UserId) ? null : users.TryMap(row.UserId, out var uid) ? _users.GetUserById(uid) : null;
            resolved[row.Id] = Resolve(row, member, former.ContainsKey(row.UserId));
        }

        var view = _archivedLines.Look();
        var ownServer = IsOwnServer(envelope.ServerId);
        job.EnterWritePhase(parsed.Ratings.Count);
        var origin = RatingValues.DeletionOrigin.Import(job.Id);
        Applied Run(IRatingsWriter w)
        {
            // A plan of its own each time: the check's run and the write's each add every row once.
            var outcome = Apply(w, parsed, archiveRows, resolved, users, deleted, request.Mode, new Plan(fileHash, request), adminId, view, origin, ownServer);
            if (!request.DryRun && !string.Equals(ifPlan?.Trim(), outcome.PlanToken, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginException(PluginErrorKind.Conflict, ChangedMessage) { Code = ChangedCode };
            }

            return outcome;
        }

        // A real run compares its plan first, rolled back, before the import snapshot (as the
        // Jellyfin Enhanced import does): a refused run writes nothing and leaves no snapshot.
        var (applied, safety) = await _store.RunWriteAsync(
            w =>
            {
                var outcome = Run(w);
                if (!request.DryRun)
                {
                    // The archive as the import found it, beside the database's snapshot taken before this write:
                    // restoring that snapshot puts this copy back too, so the archived ratings the import
                    // adds go with the rest of it. A copy that can't be written rolls the import back: nothing is written.
                    _detached().ArchiveOf(_owner()).CopyToSnapshots(_detached().SnapshotDirectory, SnapshotOperations.Import, RatingsUserDataOwner.PreOpKeepWithin);
                }

                return outcome;
            },
            commit: !request.DryRun,
            preOpOperation: request.DryRun ? null : SnapshotOperations.Import,
            check: request.DryRun ? null : w => Run(w),
            CancellationToken.None).ConfigureAwait(false);
        job.LeaveWritePhase();
        var safetyName = safety is null ? null : Path.GetFileName(safety);

        if (!request.DryRun)
        {
            job.MarkCommitted(safetyName);
            if (applied.ArchivePurges > 0)
            {
                try
                {
                    await _archivedLines.RunAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Import: the archive lines of the ratings it deleted or whose review it replaced stay pending; the daily maintenance takes them out");
                }
            }

            // Replaced and deleted rows' pages are zeroed (secure_delete); the checkpoint takes their frames out of the -wal.
            RatingsCheckpoint.RequestAfter(_store, _logger, "the envelope import");
        }

        job.Progress("Archive", 0, envelope.Archive.Count);
        var archive = applied.Archive;
        string? archiveError = null;
        if (!request.DryRun && (applied.ArchiveToAdd.Count > 0 || request.Mode == ImportMode.Replace))
        {
            // After the commit: a failure here leaves the ratings imported, and says so.
            try
            {
                var written = _detached().ImportArchived(_owner(), applied.ArchiveToAdd, dryRun: false, replace: request.Mode == ImportMode.Replace);
                archive = archive with { Imported = written.Count };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PluginException)
            {
                _logger.LogError(ex, "Import: the ratings were imported, but the export's archived ratings couldn't be written; {Snapshot} undoes the import", safetyName ?? "no snapshot");
                archiveError = ex.Message;
                archive = archive with { Imported = 0 };
                applied.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"The ratings were imported, but the export's archived ratings couldn't be written ({ex.Message}). Restore {safetyName ?? "the import's snapshot"} to undo the import, the deletions it made too (deletions made on this server since then stay deleted), or import the file again once the archive can be written."));
            }
        }

        var report = new ImportReport
        {
            DryRun = request.DryRun,
            Mode = request.Mode,
            MapUsersBy = request.MapUsersBy,
            SourcePluginVersion = envelope.PluginVersion,
            SourceServerId = envelope.ServerId,
            ExportedUtc = envelope.ExportedUtc,
            SchemaVersion = envelope.SchemaVersion,
            MappedUsers = users.Mapped,
            UnknownUsers = users.Unknown,
            Records = applied.Counts,
            Archive = archive,
            SettingsIncluded = envelope.Settings is not null,
            SafetySnapshot = safetyName,
            Notes = applied.Notes
        }.ToDictionary();
        report["PlanToken"] = applied.PlanToken;
        report["ArchiveError"] = archiveError;
        report["Outcomes"] = applied.Outcomes;
        report["OwnServer"] = ownServer;
        report["Unusable"] = parsed.Unusable.Count;
        report["UnusableRows"] = parsed.Unusable.Listed;
        if (!request.DryRun)
        {
            _logger.LogInformation(
                "Imported an export of User Ratings {Version} ({Mode}, users by {MapUsersBy}): {Outcomes}; snapshot before: {Snapshot}",
                envelope.PluginVersion,
                request.Mode,
                request.MapUsersBy,
                string.Join(", ", applied.Outcomes.Select(o => o.Key + " " + o.Value.ToString(CultureInfo.InvariantCulture))),
                safetyName ?? "none");
        }

        return report;
    }

    private static JsonArray Array<T>(IEnumerable<T> rows)
        => new(rows.Select(r => JsonSerializer.SerializeToNode(r, JsonStoreFormat.Options)).ToArray());

    private static string? NameOf(Guid userId, IReadOnlyDictionary<Guid, string> serverUsers, KnownUsers known)
        => serverUsers.TryGetValue(userId, out var name) ? name : known.NameOf(userId);

    /// <summary>
    /// Whether an envelope's <c>ServerId</c> is this server's (ignoring case; two ids of one Guid alike): its log rows and
    /// ledger are this server's own, which a restore reads as its own. A file
    /// that claims this server's id only makes a restore keep more removals, never fewer.
    /// </summary>
    /// <param name="serverId">The envelope's server id.</param>
    /// <returns>Whether it is this server's.</returns>
    internal bool IsOwnServer(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(_serverId))
        {
            return false;
        }

        return Guid.TryParse(serverId.Trim(), out var a) && Guid.TryParse(_serverId.Trim(), out var b)
            ? a == b
            : string.Equals(serverId.Trim(), _serverId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void Count(Dictionary<string, int> outcomes, string outcome) => outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;

    private static void Count(Dictionary<string, int> outcomes, string outcome, int n)
    {
        if (n > 0)
        {
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + n;
        }
    }

    // A row's times no later than the import's (ImportTimes); an entry imported before with the same time keeps the time
    // that import wrote, so the same file again is Unchanged, and a member's change since still the newer.
    private static ExportRating Clamped(ExportRating r, ImportLedgerRecord? link, DateTime ceiling, ref int clamped)
    {
        var updatedCeiling = link is not null && link.SourceUpdatedUtc == r.UpdatedUtc ? ImportTimes.Written(link) : ceiling;
        if (!ImportTimes.IsAhead(r.UpdatedUtc, updatedCeiling) && !ImportTimes.IsAhead(r.CreatedUtc, updatedCeiling) && (r.ReviewUpdatedUtc is not { } reviewed || !ImportTimes.IsAhead(reviewed, updatedCeiling)))
        {
            return r;
        }

        clamped++;
        return r with
        {
            UpdatedUtc = ImportTimes.Clamp(r.UpdatedUtc, updatedCeiling),
            CreatedUtc = ImportTimes.Clamp(r.CreatedUtc, updatedCeiling),
            ReviewUpdatedUtc = r.ReviewUpdatedUtc is { } x ? ImportTimes.Clamp(x, updatedCeiling) : null
        };
    }

    private Dictionary<Guid, string> ServerUsers()
        => KitUsers.GetAll(_users).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().Username);

    private Target Resolve(ExportRating row, User? member, bool formerMember)
    {
        if (row.DetachedUtc is not null)
        {
            return new Target(null, row.Identity, "Detached");
        }

        if (member is null && !formerMember)
        {
            return new Target(null, row.Identity, "NoUser");
        }

        var visible = member is null ? null : IdentityResolver.VisibleTo(_library, member);
        if (row.Identity.ItemId != Guid.Empty && _library.GetItemById(row.Identity.ItemId) is BaseItem item && (visible is null || visible(item)))
        {
            return new Target(item.Id, ItemIdentity.Capture(item, _library), "Item");
        }

        var result = _resolver().Resolve(row.Identity, RelinkPolicy.Default, isVisible: visible);
        return result.Outcome == ResolveOutcome.Relink && result.Item is { } hit
            ? new Target(hit.Id, ItemIdentity.Capture(hit, _library), "Resolved")
            : new Target(null, row.Identity, result.Outcome == ResolveOutcome.Ambiguous ? RatingValues.DetachReason.Ambiguous : RatingValues.DetachReason.Unmatched);
    }

    private Applied Apply(
        IRatingsWriter w,
        Parsed parsed,
        IReadOnlyList<ArchiveRow> archiveRows,
        IReadOnlyDictionary<Guid, Target> resolved,
        ImportUserMap users,
        IReadOnlySet<Guid> deleted,
        ImportMode mode,
        Plan plan,
        Guid? adminId,
        ArchivedLinePurges.ArchiveView view,
        string origin,
        bool ownServer)
    {
        // The tombstones and former members this import writes carry its origin (since the 0.3.19 verification): they are
        // part of the import, so restoring its snapshot, or an earlier one, undoes them with the rest of it.
        var now = _clock.GetUtcNow().UtcDateTime;
        var ceiling = ImportTimes.Ceiling(now);
        var clamped = 0;
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        var notes = new List<string>();

        // The ledger's local rows of the file's ratings, read before Replace's wipe empties the ledger:
        // a rating the ledger maps to one deleted here never comes back, however new the file's copy.
        // The wipe keeps the rows that point at a deleted rating, and a skipped row writes its link again (found
        // while verifying 0.3.19), so the next import of the same file, Replace or Merge, still finds it.
        var ledgered = new Dictionary<Guid, ImportLedgerRecord>();
        foreach (var r in parsed.Ratings)
        {
            if (w.GetImport(LedgerSource, r.Id.ToString("N", CultureInfo.InvariantCulture)) is { RatingIds.Count: > 0 } earlier)
            {
                ledgered[r.Id] = earlier;
            }
        }

        var removed = mode == ImportMode.Replace ? w.WipeForReplace() : 0;
        var envelopeTombstones = parsed.Tombstones.Select(t => t.RatingId).ToHashSet();

        // Former members: kept when this server knows them, or when their user wasn't deleted here (another server's).
        var localFormer = w.GetFormerUsers();
        var formerOk = new HashSet<Guid>();
        foreach (var f in parsed.FormerUsers)
        {
            if (localFormer.ContainsKey(f.Id) || !deleted.Contains(f.OriginalUserId))
            {
                formerOk.Add(f.Id);
                w.AddFormerUserIfAbsent(f with { Origin = origin });
            }

            plan.Add("F", f.Id, formerOk.Contains(f.Id) ? "Kept" : "SkippedDeletedUser", null);
        }

        int added = 0, updated = 0, unchanged = 0, keptNewer = 0, unknownUser = 0, skipped = 0;
        var textReplaced = new List<Guid>();
        foreach (var raw in parsed.Ratings)
        {
            var target = resolved[raw.Id];
            string outcome;
            Guid? at = null;
            var isFormer = parsed.FormerUsers.Any(f => f.Id == raw.UserId);
            var uid = raw.UserId;
            var link = ledgered.GetValueOrDefault(raw.Id);

            // Times ahead of this server's clock are written as now, so a delete here is
            // never older than the row; an entry imported before with the same time keeps the time that import wrote.
            var r = Clamped(raw, link, ceiling, ref clamped);
            var ledgerIds = link?.RatingIds ?? [];
            var tombstone = w.GetTombstone(r.Id) ?? ledgerIds.Select(w.GetTombstone).FirstOrDefault(t => t is not null);
            if (tombstone is not null || envelopeTombstones.Contains(r.Id))
            {
                // Deleted here, the rating or the one the ledger maps it to: by its member, a moderator or a deleted-user
                // policy (SkippedDeleted), or an admin's Purge (PreviouslyPurged); or deleted in the file.
                outcome = tombstone?.DeletedBy == RatingValues.DeletedBy.Purge ? "PreviouslyPurged" : "SkippedDeleted";
                skipped++;

                // The link stays as it was: the next import of this file finds the deleted rating through it.
                if (link is not null)
                {
                    w.RecordImport(link);
                }
            }
            else if (isFormer ? !formerOk.Contains(r.UserId) : !users.TryMap(r.UserId, out uid))
            {
                outcome = isFormer ? "SkippedDeletedUser" : "SkippedUnknownUser";
                if (isFormer)
                {
                    skipped++;
                }
                else
                {
                    unknownUser++;
                }
            }
            else
            {
                var existing = w.Get(r.Id);
                if (existing is null && ledgerIds.Count > 0)
                {
                    existing = w.Get(ledgerIds[0]);
                }

                if (existing is null && target.ItemId is { } onItem)
                {
                    existing = w.GetAttached(uid, onItem);
                }

                if (existing is not null)
                {
                    at = existing.DetachedUtc is null ? existing.ItemId : null;
                    if (existing.UpdatedUtc > r.UpdatedUtc)
                    {
                        outcome = "KeptNewer";
                        keptNewer++;
                    }
                    else if (existing.UpdatedUtc == r.UpdatedUtc)
                    {
                        outcome = "Unchanged";
                        unchanged++;
                    }
                    else
                    {
                        w.Upsert(existing with
                        {
                            Stars2 = r.Stars2,
                            ReviewText = r.ReviewText,
                            IsSpoiler = r.IsSpoiler,
                            CreatedUtc = r.CreatedUtc < existing.CreatedUtc ? r.CreatedUtc : existing.CreatedUtc,
                            UpdatedUtc = r.UpdatedUtc,
                            ReviewUpdatedUtc = r.ReviewUpdatedUtc,
                            EditCount = Math.Max(existing.EditCount, r.EditCount)
                        });
                        outcome = "Updated";
                        updated++;

                        // The review it replaces leaves the archive's earlier lines too, as a member's own edit
                        // does.
                        if (existing.ReviewText is not null && !string.Equals(existing.ReviewText, r.ReviewText, StringComparison.Ordinal))
                        {
                            textReplaced.Add(existing.Id);
                        }
                    }

                    // The ledger keeps the file's own time. A match this import didn't write keeps the link it had:
                    // a new one would claim the rating's time as this import's, and a restore
                    // would take the member's own change since as the import's.
                    if (outcome == "Updated" || (link is null && existing.Id != r.Id))
                    {
                        w.RecordImport(new ImportLedgerRecord(LedgerSource, r.Id.ToString("N", CultureInfo.InvariantCulture), raw.UpdatedUtc, now, [existing.Id]));
                    }
                    else if (link is not null)
                    {
                        w.RecordImport(link);
                    }
                }
                else
                {
                    var attached = target.ItemId is not null;
                    w.Upsert(new RatingRecord
                    {
                        Id = r.Id,
                        UserId = uid,
                        Identity = target.Identity,
                        Stars2 = r.Stars2,
                        ReviewText = r.ReviewText,
                        IsSpoiler = r.IsSpoiler,
                        CreatedUtc = r.CreatedUtc,
                        UpdatedUtc = r.UpdatedUtc,
                        ReviewUpdatedUtc = r.ReviewUpdatedUtc,
                        EditCount = r.EditCount,
                        Origin = string.IsNullOrWhiteSpace(r.Origin) ? RatingValues.Origin.Import : r.Origin,
                        DetachedUtc = attached ? null : r.DetachedUtc ?? now,
                        DetachReason = attached ? null : r.DetachReason ?? target.Outcome,
                        AttachedUtc = attached ? now : null
                    });
                    at = target.ItemId;
                    outcome = attached ? "Added" : "StoredDetached";
                    added++;
                    w.RecordImport(new ImportLedgerRecord(LedgerSource, r.Id.ToString("N", CultureInfo.InvariantCulture), raw.UpdatedUtc, now, [r.Id]));
                }
            }

            Count(outcomes, outcome);
            plan.Add("R", r.Id, outcome, at);
        }

        // The envelope's tombstones: added; a live rating here that one deleted later is deleted, its archive lines with it.
        var deletedByTombstone = new List<Guid>();
        var tombstonesAdded = 0;
        foreach (var rawTombstone in parsed.Tombstones)
        {
            string outcome;
            var t = rawTombstone;
            if (ImportTimes.IsAhead(t.DeletedUtc, ceiling))
            {
                t = t with { DeletedUtc = ceiling };
                clamped++;
            }

            if (w.HasTombstone(t.RatingId))
            {
                outcome = "Known";
            }
            else if (w.Get(t.RatingId) is { } live)
            {
                if (t.DeletedUtc >= live.UpdatedUtc)
                {
                    w.Delete(live.Id, t.DeletedBy, t.DeletedUtc, origin);
                    deletedByTombstone.Add(live.Id);
                    outcome = "Deleted";
                }
                else
                {
                    outcome = "KeptNewer";
                }
            }
            else
            {
                w.AddTombstone(t.RatingId, users.TryMap(t.UserId, out var tu) ? tu : t.UserId, t.ItemId, t.DeletedBy, t.DeletedUtc, origin);
                tombstonesAdded++;
                outcome = "Added";
            }

            Count(outcomes, "Tombstone" + outcome);
            plan.Add("T", t.RatingId, outcome, null);
        }

        // The ledger of other sources (a JE import's keys), so a later import of the same source stays idempotent. Merge
        // keeps this server's entry of a key. Replace: its wipe kept the rows of any source that point at a
        // deleted rating, and the file's entry of the same key is joined to them (the file's times, both entries' ratings),
        // so a JE re-import never brings back a rating deleted here or in the file.
        foreach (var entry in parsed.Ledger.Where(l => !string.Equals(l.Source, LedgerSource, StringComparison.Ordinal)))
        {
            var kept = w.GetImport(entry.Source, entry.SourceKey);
            if (kept is null)
            {
                w.RecordImport(entry);
            }
            else if (mode == ImportMode.Replace)
            {
                w.RecordImport(entry with { RatingIds = entry.RatingIds.Union(kept.RatingIds).Distinct().Order().ToList() });
            }
        }

        if (clamped > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{clamped} {(clamped == 1 ? "time was" : "times were")} ahead of this server's clock (the other server's clock ran ahead, or its times were local), so the import wrote them as the time of the import."));
        }

        Count(outcomes, "Clamped", clamped);
        if (mode == ImportMode.Replace)
        {
            foreach (var row in parsed.ModerationLog)
            {
                w.AppendModerationLog(row);
            }
        }
        else if (parsed.ModerationLog.Count > 0)
        {
            notes.Add("Merge keeps this server's moderation log as it is: the export's log rows were not imported (Replace imports them).");
        }

        w.AppendModerationLog(new ModerationLogRecord
        {
            AtUtc = now,
            ActorUserId = adminId,
            Action = RatingValues.ModerationAction.ImportEnvelope,
            Details = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"Mode\":\"{mode}\",\"Added\":{added},\"Updated\":{updated},\"Removed\":{removed + deletedByTombstone.Count},\"Skipped\":{skipped + unknownUser},\"Origin\":\"{origin}\",\"TombstonesAdded\":{tombstonesAdded},\"DeletedByTombstones\":{deletedByTombstone.Count},\"OwnServer\":{(ownServer ? "true" : "false")}}}")
        });

        var (archive, archiveToAdd) = PlanArchive(w, archiveRows, parsed, formerOk, users, envelopeTombstones, mode, plan);
        var purges = deletedByTombstone.Count + textReplaced.Count == 0 ? 0 : ArchivedLinePurges.Record(w, view, deletedByTombstone.Concat(textReplaced));
        var counts = new ImportCounts
        {
            Incoming = parsed.Ratings.Count,
            Added = added,
            Updated = updated,
            Unchanged = unchanged,
            KeptNewer = keptNewer,
            Removed = removed + deletedByTombstone.Count,
            SkippedUnknownUser = unknownUser,
            Skipped = skipped
        };
        return new Applied(counts, notes, plan.Token(users), outcomes, purges, archive, archiveToAdd);
    }

    // The file's archived ratings, decided under the writer lock after the ratings and the tombstones, so the plan's token
    // covers them: one deleted here or in the file is left out (a tombstone the import just
    // added too), so is one whose member isn't here (a former member as the ratings' rule has it), one live here, and, for
    // Merge, one archived here already. Replace makes the archive the file's. A dry run reads the same.
    private (ArchiveImportResult Result, List<ArchivedRecord> ToAdd) PlanArchive(
        IRatingsWriter w,
        IReadOnlyList<ArchiveRow> rows,
        Parsed parsed,
        IReadOnlySet<Guid> formerOk,
        ImportUserMap users,
        IReadOnlySet<Guid> envelopeTombstones,
        ImportMode mode,
        Plan plan)
    {
        var toAdd = new List<ArchivedRecord>();
        if (rows.Count == 0 && mode != ImportMode.Replace)
        {
            return (new ArchiveImportResult(0, 0, null), toAdd);
        }

        var contents = _detached().ArchiveOf(_owner()).Read();
        if (contents.Problem is not null)
        {
            throw PluginException.Unavailable(DetachedRecords.ArchiveUnreadableImport);
        }

        var archived = mode == ImportMode.Replace ? new HashSet<string>(StringComparer.Ordinal) : contents.Records.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var former = parsed.FormerUsers.Select(f => f.Id).ToHashSet();
        int unusable = 0, unknown = 0, deletedRows = 0;
        foreach (var row in rows)
        {
            if (row.Rating is not { } rating || row.Entry is not { } entry || row.Snapshot is not { } identity)
            {
                unusable++;
                continue;
            }

            string outcome;
            var isFormer = former.Contains(rating.UserId);
            var uid = rating.UserId;
            if (envelopeTombstones.Contains(rating.Id) || w.HasTombstone(rating.Id))
            {
                outcome = "Deleted";
                deletedRows++;
            }
            else if (isFormer ? !formerOk.Contains(rating.UserId) : !users.TryMap(rating.UserId, out uid))
            {
                outcome = "UnknownUser";
                unknown++;
            }
            else if (w.Get(rating.Id) is not null)
            {
                outcome = "Live";
            }
            else if (archived.Contains(rating.Id.ToString("D", CultureInfo.InvariantCulture)))
            {
                outcome = "Archived";
            }
            else
            {
                var record = JsonSerializer.SerializeToNode(rating, JsonStoreFormat.Options)!.AsObject();
                record["UserId"] = JsonSerializer.SerializeToNode(uid, JsonStoreFormat.Options);
                toAdd.Add(entry.WithExportedFacts(new ArchivedRecord
                {
                    Id = rating.Id.ToString("D", CultureInfo.InvariantCulture),
                    UserId = isFormer ? null : uid,
                    Identity = identity,
                    AttachedUtc = rating.AttachedUtc,
                    DetachedUtc = rating.DetachedUtc,
                    Record = record
                }));
                outcome = "Added";
            }

            plan.Add("A", rating.Id, outcome, null);
        }

        var skipped = unusable + unknown + deletedRows;
        var note = skipped == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{deletedRows} archived ratings deleted here, {unknown} of users not found here and {unusable} unusable were left out.");
        if (mode == ImportMode.Replace && contents.Records.Count > 0)
        {
            note = (note is null ? string.Empty : note + " ") + string.Create(CultureInfo.InvariantCulture, $"Replace makes the archive the file's: the {contents.Records.Count} archived ratings here go (the import's snapshot keeps them).");
        }

        return (new ArchiveImportResult(rows.Count, toAdd.Count, note), toAdd);
    }

    // A row's item: where it attaches (null: stored detached) and the identity it keeps.
    private sealed record Target(Guid? ItemId, IdentitySnapshot Identity, string Outcome);

    // What the write did, and the archived ratings it adds after the commit.
    private sealed record Applied(ImportCounts Counts, List<string> Notes, string PlanToken, IReadOnlyDictionary<string, int> Outcomes, int ArchivePurges, ArchiveImportResult Archive, IReadOnlyList<ArchivedRecord> ArchiveToAdd);

    // One entry of the export's Archive, read outside the writer lock: null parts when it is unusable (the reason is in
    // the report's UnusableRows), its review normalised and its names cleaned when it is usable (ImportRowRules).
    private sealed record ArchiveRow(ExportedArchivedRecord? Entry, ArchivedRating? Rating, IdentitySnapshot? Snapshot)
    {
        public static List<ArchiveRow> ReadAll(IReadOnlyList<JsonObject> records, ImportUnusableRows unusable)
        {
            var rows = new List<ArchiveRow>(records.Count);
            var ids = new HashSet<Guid>();
            for (var i = 0; i < records.Count; i++)
            {
                var json = records[i];
                var entry = json is null ? null : DetachedRecords.ReadExported(json);
                ArchivedRating? rating;
                IdentitySnapshot? identity;
                string? reason = null;
                try
                {
                    rating = entry?.Record.Deserialize<ArchivedRating>(ImportJson.Options);
                    identity = entry?.Record["Identity"]?.Deserialize<IdentitySnapshot>(ImportJson.Options);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    rating = null;
                    identity = null;
                    reason = ImportRowRules.Unreadable(ex);
                }

                if (rating is not null)
                {
                    var review = rating.ReviewText;
                    reason = ImportRowRules.Rating(rating.Id, rating.UserId, ref identity, rating.Stars2, ref review, rating.EditCount, rating.Origin, rating.CreatedUtc, rating.UpdatedUtc, rating.DetachReason)
                        ?? (rating.DetachedUtc == default ? "It has no DetachedUtc." : null)
                        ?? (ids.Add(rating.Id) ? null : "Another archived rating in the file has the same Id.");
                    rating.ReviewText = review;
                }

                if (entry is null || rating is null || identity is null || reason is not null)
                {
                    unusable.Add("Archive", i, json, "Id", reason ?? ImportRowRules.Unreadable(null));
                    rows.Add(new ArchiveRow(null, null, null));
                }
                else
                {
                    rows.Add(new ArchiveRow(entry, rating, identity));
                }
            }

            return rows;
        }
    }

    // The plan's token: the file, the mode, the matching, every user mapped and every row's decision, in order.
    private sealed class Plan
    {
        private readonly StringBuilder _text = new();

        public Plan(string fileHash, ImportRequest request)
            => _text.Append("ur-import/1|").Append(fileHash).Append('|').Append(request.Mode).Append('|').Append(request.MapUsersBy).Append('\n');

        public void Add(string kind, Guid id, string outcome, Guid? itemId)
            => _text.Append(kind).Append(':').Append(id.ToString("N", CultureInfo.InvariantCulture)).Append(':').Append(outcome).Append(':')
                .Append(itemId?.ToString("N", CultureInfo.InvariantCulture)).Append('\n');

        public string Token(ImportUserMap users)
        {
            foreach (var (from, to) in users.Map.OrderBy(p => p.Key))
            {
                _text.Append("U:").Append(from.ToString("N", CultureInfo.InvariantCulture)).Append(':').Append(to.ToString("N", CultureInfo.InvariantCulture)).Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_text.ToString())).AsSpan(0, 16)).ToLowerInvariant();
        }
    }

    // The envelope's Data, read row by row: a row that breaks one of the store's rules is left
    // out with its reason (ImportRowRules), never failing the whole import at the write; a usable rating's review is
    // normalised as a member's write is, and its item's names cleaned.
    private sealed class Parsed
    {
        public List<ExportRating> Ratings { get; } = [];

        public List<FormerUserRecord> FormerUsers { get; } = [];

        public List<ModerationLogRecord> ModerationLog { get; } = [];

        public List<ImportLedgerRecord> Ledger { get; } = [];

        public List<TombstoneRecord> Tombstones { get; } = [];

        public ImportUnusableRows Unusable { get; } = new();

        public static Parsed Read(JsonObject data)
        {
            var parsed = new Parsed();
            var ids = new HashSet<Guid>();
            foreach (var (row, index, node) in parsed.Rows<ExportRating>(data, "Ratings", "Id"))
            {
                var identity = (IdentitySnapshot?)row.Identity;
                var review = row.ReviewText;
                var reason = ImportRowRules.Rating(row.Id, row.UserId, ref identity, row.Stars2, ref review, row.EditCount, row.Origin, row.CreatedUtc, row.UpdatedUtc, row.DetachReason)
                    ?? ((row.DetachedUtc is null) != (row.DetachReason is null) || row.DetachedUtc == default(DateTime)
                        ? "A detached rating has both its DetachedUtc and its DetachReason, an attached one neither."
                        : null)
                    ?? (ids.Add(row.Id) ? null : "Another rating in the file has the same Id.");
                if (reason is not null)
                {
                    parsed.Unusable.Add("Ratings", index, node, "Id", reason);
                    continue;
                }

                parsed.Ratings.Add(row with { Identity = identity!, ReviewText = review });
            }

            var formerIds = new HashSet<Guid>();
            foreach (var (row, index, node) in parsed.Rows<FormerUserRecord>(data, "FormerUsers", "Id"))
            {
                var former = row;
                var reason = ImportRowRules.FormerUser(ref former) ?? (formerIds.Add(former.Id) ? null : "Another former member in the file has the same Id.");
                parsed.Keep(parsed.FormerUsers, former, reason, ("FormerUsers", "Id"), index, node);
            }

            var tombstoneIds = new HashSet<Guid>();
            foreach (var (row, index, node) in parsed.Rows<TombstoneRecord>(data, "Tombstones", "RatingId"))
            {
                var reason = ImportRowRules.Tombstone(row) ?? (tombstoneIds.Add(row.RatingId) ? null : "Another tombstone in the file has the same RatingId.");
                parsed.Keep(parsed.Tombstones, row, reason, ("Tombstones", "RatingId"), index, node);
            }

            foreach (var (row, index, node) in parsed.Rows<ExportLogRow>(data, "ModerationLog", "Id"))
            {
                parsed.Keep(parsed.ModerationLog, row.ToRecord(), ImportRowRules.LogRow(row.Action, row.AtUtc), ("ModerationLog", "Id"), index, node);
            }

            var ledgerRows = new List<ExportLedgerRow>();
            foreach (var (row, index, node) in parsed.Rows<ExportLedgerRow>(data, "ImportLedgerRows", "SourceKey"))
            {
                parsed.Keep(ledgerRows, row, ImportRowRules.Ledger(row.Source, row.SourceKey, row.RatingId), ("ImportLedgerRows", "SourceKey"), index, node);
            }

            var byKey = ledgerRows.ToLookup(r => (r.Source, r.SourceKey), r => r.RatingId);
            foreach (var (row, index, node) in parsed.Rows<ExportLedger>(data, "ImportLedger", "SourceKey"))
            {
                parsed.Keep(parsed.Ledger, new ImportLedgerRecord(row.Source, row.SourceKey, row.SourceUpdatedUtc, row.ImportedUtc, byKey[(row.Source, row.SourceKey)].ToList()), ImportRowRules.Ledger(row.Source, row.SourceKey), ("ImportLedger", "SourceKey"), index, node);
            }

            return parsed;
        }

        private void Keep<T>(List<T> into, T row, string? reason, (string Name, string IdProperty) table, int index, JsonNode? node)
        {
            if (reason is null)
            {
                into.Add(row);
            }
            else
            {
                Unusable.Add(table.Name, index, node, table.IdProperty, reason);
            }
        }

        // The table's rows this version can read, with their index and node; one it can't read is left out, with its reason.
        private IEnumerable<(T Row, int Index, JsonNode? Node)> Rows<T>(JsonObject data, string name, string idProperty)
            where T : class
        {
            if (data[name] is not JsonArray array)
            {
                yield break;
            }

            for (var i = 0; i < array.Count; i++)
            {
                var node = array[i];
                T? row;
                Exception? error = null;
                try
                {
                    row = node?.Deserialize<T>(ImportJson.Options);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    row = null;
                    error = ex;
                }

                if (row is null)
                {
                    Unusable.Add(name, i, node, idProperty, node is null ? "The row is empty." : ImportRowRules.Unreadable(error));
                    continue;
                }

                yield return (row, i, node);
            }
        }
    }

    // A rating in the export: stars as decimals, the identity as captured.
    private sealed record ExportRating
    {
        public Guid Id { get; init; }

        public Guid UserId { get; init; }

        public string? UserName { get; init; }

        public Guid ItemId { get; init; }

        public IdentitySnapshot Identity { get; init; } = null!;

        public double? Stars { get; init; }

        public string? ReviewText { get; init; }

        public bool IsSpoiler { get; init; }

        public DateTime CreatedUtc { get; init; }

        public DateTime UpdatedUtc { get; init; }

        public DateTime? ReviewUpdatedUtc { get; init; }

        public int EditCount { get; init; }

        public string? Origin { get; init; }

        public DateTime? DetachedUtc { get; init; }

        public string? DetachReason { get; init; }

        public DateTime? AttachedUtc { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public int? Stars2 => Stars is { } s ? (int)Math.Round(s * 2, MidpointRounding.AwayFromZero) : null;

        public static ExportRating Of(RatingRecord r, string? userName) => new()
        {
            Id = r.Id,
            UserId = r.UserId,
            UserName = userName,
            ItemId = r.ItemId,
            Identity = r.Identity,
            Stars = r.Stars2 is { } s ? s / 2.0 : null,
            ReviewText = r.ReviewText,
            IsSpoiler = r.IsSpoiler,
            CreatedUtc = r.CreatedUtc,
            UpdatedUtc = r.UpdatedUtc,
            ReviewUpdatedUtc = r.ReviewUpdatedUtc,
            EditCount = r.EditCount,
            Origin = r.Origin,
            DetachedUtc = r.DetachedUtc,
            DetachReason = r.DetachReason,
            AttachedUtc = r.AttachedUtc
        };
    }

    private sealed record ExportLogRow
    {
        public long Id { get; init; }

        public DateTime AtUtc { get; init; }

        public Guid? ActorUserId { get; init; }

        public string Action { get; init; } = string.Empty;

        public Guid? RatingId { get; init; }

        public Guid? TargetUserId { get; init; }

        public Guid? ItemId { get; init; }

        public string? Reason { get; init; }

        public string? Details { get; init; }

        public static ExportLogRow Of(ModerationLogRecord r) => new()
        {
            Id = r.Id,
            AtUtc = r.AtUtc,
            ActorUserId = r.ActorUserId,
            Action = r.Action,
            RatingId = r.RatingId,
            TargetUserId = r.TargetUserId,
            ItemId = r.ItemId,
            Reason = r.Reason,
            Details = r.Details
        };

        public ModerationLogRecord ToRecord() => new()
        {
            AtUtc = AtUtc,
            ActorUserId = ActorUserId,
            Action = Action,
            RatingId = RatingId,
            TargetUserId = TargetUserId,
            ItemId = ItemId,
            Reason = Reason,
            Details = Details
        };
    }

    private sealed record ExportLedger(string Source, string SourceKey, DateTime SourceUpdatedUtc, DateTime ImportedUtc);

    private sealed record ExportLedgerRow(string Source, string SourceKey, Guid RatingId);
}
