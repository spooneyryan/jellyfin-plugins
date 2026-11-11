using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Services;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// The saved queries' part of the kit's import: queries matched by id (<see cref="ImportMerge"/>), owners mapped to this
/// server's users (a query whose owner isn't found is skipped), each query checked as it is stored, and the change checked
/// against what references saved queries (<see cref="ISavedQueryReferenceGuard.FindBrokenReferences"/>). One instance
/// serves one import.
/// </summary>
/// <remarks>
/// <para>An import keeps every query it can place. A query this build reads and finds valid is stored as a save writes it
/// (<see cref="QueryJson.Write"/>: the model's member and enum names, whatever casing the file had); one it can't read
/// (a newer version) or with errors is stored as it came and listed as such (not supported, or not valid), never
/// dropped. What it changes besides: a name is trimmed and cut
/// to 100 characters (a blank one becomes "Imported query"), a description cut to 500, a name its owner already uses gets
/// a number, a shared query that uses an administrator-only field is no longer shared, times are taken as UTC, and the
/// hash is computed again.</para>
/// <para>What it skips, each with a note (the counts' <c>Skipped</c>): a record without a query, one whose query is larger
/// than a saved query may be, one with an empty id, a second record with an id the file already had, one that would give
/// a user who isn't an administrator a query using an administrator-only field, and one past its owner's
/// <c>MaxSavedQueriesPerUser</c>. Each kept record is serialised as it will be written, so a dry run finds what a real
/// run would fail on, and both report the same.</para>
/// <para>In a real run (<see cref="Apply"/> under the store's writer lock, inside
/// <see cref="ISavedQueryReferenceGuard.BulkChangeAsync{T}"/>) a change that would break references refuses with 409
/// <c>BreaksAutoCollections</c> unless forced; a dry run lists them in <see cref="Broken"/>.</para>
/// </remarks>
internal sealed class SavedQueryImportAdapter : JsonImportAdapter<SavedQueriesDocument>
{
    private static readonly IEqualityComparer<SavedQueryRecord> SameContent = ImportMerge.JsonContent<SavedQueryRecord>();

    private readonly SavedQueryReader _reader;
    private readonly ISavedQueryReferenceGuard _guard;
    private readonly Func<Guid, bool> _isAdministrator;
    private readonly int _maxPerUser;
    private readonly bool _refuseBreaking;
    private readonly bool _realRun;

    /// <summary>
    /// Initializes a new instance of the <see cref="SavedQueryImportAdapter"/> class.
    /// </summary>
    /// <param name="reader">Reads the imported queries.</param>
    /// <param name="guard">What references saved queries.</param>
    /// <param name="isAdministrator">Whether a user of this server (a mapped owner) is an administrator.</param>
    /// <param name="maxPerUser">The setting <c>MaxSavedQueriesPerUser</c>.</param>
    /// <param name="refuseBreaking">Whether a change that breaks references is refused: a real run that isn't forced.</param>
    /// <param name="realRun">Whether this is a real run: one that removes or changes queries is refused, forced or not,
    /// while the references can't be checked (<see cref="ISavedQueryReferenceGuard.ReferencesUnknown"/>).</param>
    public SavedQueryImportAdapter(SavedQueryReader reader, ISavedQueryReferenceGuard guard, Func<Guid, bool> isAdministrator, int maxPerUser, bool refuseBreaking, bool realRun = false)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(isAdministrator);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerUser, 1);
        _reader = reader;
        _guard = guard;
        _isAdministrator = isAdministrator;
        _maxPerUser = maxPerUser;
        _refuseBreaking = refuseBreaking;
        _realRun = realRun;
    }

    /// <summary>Gets the references the last <see cref="Apply"/> would break (or, forced, broke).</summary>
    public IReadOnlyList<SmartSearchAutoCollectionRefDto> Broken { get; private set; } = [];

    /// <inheritdoc />
    public override IReadOnlyDictionary<Guid, int> CountRecordsByUser(SavedQueriesDocument document) => SavedQueryStore.CountRecordsByUser(document);

    /// <inheritdoc />
    public override ImportApplied<SavedQueriesDocument> Apply(SavedQueriesDocument current, SavedQueriesDocument incoming, ImportMode mode, ImportUserMap users)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(users);

        var notes = new List<string>();
        var mapped = new List<SavedQueryRecord>();
        var seen = new HashSet<Guid>();
        int considered = 0, skipped = 0, unshared = 0;
        var records = incoming.Queries ?? [];
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record is null || !users.TryMap(record.OwnerUserId, out var owner))
            {
                continue;
            }

            considered++;
            var position = (i + 1).ToString(CultureInfo.InvariantCulture);
            var placed = Place(record, owner, users, position, seen, out var refusal, out var wasUnshared);
            if (placed is null)
            {
                skipped++;
                notes.Add(refusal!);
                continue;
            }

            mapped.Add(placed);
            unshared += wasUnshared ? 1 : 0;
        }

        // The quota: a query new to its owner (not stored as theirs) that takes them past it is skipped, the file's later
        // ones first; the queries already theirs stay.
        var merged = Merge(current, mapped, mode);
        var overQuota = OverQuota(current, merged.Records, mapped);
        if (overQuota.Count > 0)
        {
            skipped += overQuota.Count;
            notes.Add(overQuota.Count.ToString(CultureInfo.InvariantCulture) + " queries would take their owner past the most saved queries a user may have (" + _maxPerUser.ToString(CultureInfo.InvariantCulture) + ") and were skipped.");
            mapped = mapped.Where(q => !overQuota.Contains(q)).ToList();
            merged = Merge(current, mapped, mode);
        }

        // The queries already here keep their names; an imported one that takes a name its owner uses gets the number.
        var stored = current.Queries.ToDictionary(q => q.Id, q => q.Name);
        var ordered = merged.Records
            .OrderBy(q => stored.TryGetValue(q.Id, out var name) && string.Equals(name, q.Name, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(q => q.CreatedUtc)
            .ThenBy(q => q.Id)
            .ToList();
        var queries = SavedQueryNames.MakeUnique(ordered);
        var renamed = queries.Zip(ordered).Count(p => !string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal));

        var counts = merged.Counts with { Incoming = considered, Skipped = skipped };
        var changed = counts.Added + counts.Updated + counts.Removed + renamed > 0;
        var known = (current.KnownUsers ?? KnownUsers.Empty)
            .Refresh(queries.Select(q => q.OwnerUserId), users.Mapped.GroupBy(m => m.NewId).ToDictionary(g => g.Key, g => g.First().Name));
        if (unshared > 0)
        {
            notes.Add(unshared.ToString(CultureInfo.InvariantCulture) + " shared queries use an administrator-only field and were imported as not shared.");
        }

        if (renamed > 0)
        {
            notes.Add(renamed.ToString(CultureInfo.InvariantCulture) + " queries were renamed with a number, as their owner already had a query of that name.");
        }

        var document = !changed && known.Equals(current.KnownUsers ?? KnownUsers.Empty)
            ? current
            : SavedQueryStore.WithQueries(current, queries) with { KnownUsers = known };

        var change = SavedQueryBulkChange.Between(current, document);
        if (_guard.ReferencesUnknown && !change.IsEmpty)
        {
            if (_realRun)
            {
                throw SavedQueryErrors.ReferencesUnknown;
            }

            notes.Add("The auto-collections couldn't be loaded: an import that removes or changes queries is refused until they load.");
        }

        Broken = _guard.FindBrokenReferences(change);
        if (Broken.Count > 0 && _refuseBreaking)
        {
            throw SavedQueryErrors.BreaksReferences(Broken);
        }

        return new ImportApplied<SavedQueriesDocument>(document, counts, notes);
    }

    /// <inheritdoc />
    /// <remarks>A real run that isn't forced checks the references on the document stored now, so an import that breaks
    /// them is refused before the kit takes its pre-operation snapshot (under the guard's lock, held by the caller, no
    /// reference changes before the write).</remarks>
    public override void CheckBeforeWrite(SavedQueriesDocument current, SavedQueriesDocument incoming, ImportMode mode, ImportUserMap users)
    {
        if (_refuseBreaking || _realRun)
        {
            Apply(current, incoming, mode, users);
        }
    }

    private static MergeResult<SavedQueryRecord> Merge(SavedQueriesDocument current, IReadOnlyList<SavedQueryRecord> mapped, ImportMode mode)
        => mode == ImportMode.Replace
            ? ImportMerge.Replace(current.Queries, mapped, q => q.Id, SameContent)
            : ImportMerge.Merge(current.Queries, mapped, q => q.Id, q => q.UpdatedUtc, SameContent);

    // A record the store couldn't write (a value the serialiser refuses) is skipped, as a real run would fail on it.
    private static bool Writable(SavedQueryRecord record)
    {
        try
        {
            JsonSerializer.SerializeToUtf8Bytes(record, JsonStoreFormat.Options);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    // The record as it would be stored, or null with why it is skipped.
    private SavedQueryRecord? Place(SavedQueryRecord record, Guid owner, ImportUserMap users, string position, HashSet<Guid> seen, out string? refusal, out bool unshared)
    {
        unshared = false;
        refusal = null;
        if (record.Id.Equals(Guid.Empty))
        {
            refusal = "Record " + position + " has no id and was skipped.";
            return null;
        }

        if (!seen.Add(record.Id))
        {
            refusal = "Record " + position + " has the id of an earlier record in the file and was skipped.";
            return null;
        }

        if (record.Query.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            refusal = "Record " + position + " has no query and was skipped.";
            return null;
        }

        if (Encoding.UTF8.GetByteCount(record.Query.GetRawText()) > QueryLimits.MaxBytes)
        {
            refusal = "Record " + position + " has a query larger than a saved query may be (64 KB) and was skipped.";
            return null;
        }

        var facts = _reader.Read(record.Query);
        if (facts.UsesAdminOnlyFields && !_isAdministrator(owner))
        {
            refusal = "Record " + position + " uses an administrator-only field, and its owner here isn't an administrator: it was skipped.";
            return null;
        }

        // A query this build reads and finds valid is stored in the form a save writes (QueryJson.Write), whatever casing
        // or spacing the file had: the server reads member and enum names ignoring case, the page reads the written form.
        // Only a query it can't read, or one with errors, is kept as it came.
        var query = record.Query;
        if (facts is { Definition: { } definition, Supported: true, Valid: true })
        {
            var written = QueryJson.Write(definition);
            if (Encoding.UTF8.GetByteCount(written) > QueryLimits.MaxBytes)
            {
                refusal = "Record " + position + " has a query larger than a saved query may be (64 KB) and was skipped.";
                return null;
            }

            using var document = JsonDocument.Parse(written);
            query = document.RootElement.Clone();
        }

        var shared = record.Shared && !facts.UsesAdminOnlyFields;
        var placed = record with
        {
            Query = query,
            OwnerUserId = owner,
            UpdatedByUserId = users.TryMap(record.UpdatedByUserId, out var editor) ? editor : owner,
            Name = SavedQueryNames.Clean(record.Name),
            Description = SavedQueryNames.CleanDescription(record.Description),
            Shared = shared,
            QueryHash = facts.Hash,
            CreatedUtc = UtcDateTimeConverter.Utc(record.CreatedUtc),
            UpdatedUtc = UtcDateTimeConverter.Utc(record.UpdatedUtc),
            LastRunUtc = record.LastRunUtc is { } run ? UtcDateTimeConverter.Utc(run) : null
        };
        if (!Writable(placed))
        {
            refusal = "Record " + position + " can't be stored as it is and was skipped.";
            return null;
        }

        unshared = record.Shared && !shared;
        return placed;
    }

    // The imported records, last in the file first, that are new to their owner and take them past the quota.
    private HashSet<SavedQueryRecord> OverQuota(SavedQueriesDocument current, IReadOnlyList<SavedQueryRecord> result, IReadOnlyList<SavedQueryRecord> mapped)
    {
        var storedOwner = current.Queries.ToDictionary(q => q.Id, q => q.OwnerUserId);
        var resulting = new HashSet<SavedQueryRecord>(result, ReferenceEqualityComparer.Instance);
        var over = new HashSet<SavedQueryRecord>(ReferenceEqualityComparer.Instance);
        foreach (var group in result.GroupBy(q => q.OwnerUserId))
        {
            var excess = group.Count() - _maxPerUser;
            for (var i = mapped.Count - 1; i >= 0 && excess > 0; i--)
            {
                var record = mapped[i];
                var isNew = !storedOwner.TryGetValue(record.Id, out var was) || !was.Equals(record.OwnerUserId);
                if (record.OwnerUserId.Equals(group.Key) && isNew && resulting.Contains(record))
                {
                    over.Add(record);
                    excess--;
                }
            }
        }

        return over;
    }
}
