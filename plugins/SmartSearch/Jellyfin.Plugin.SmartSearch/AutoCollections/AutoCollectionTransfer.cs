using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The definitions' part of the saved queries' export and import: the envelope's <c>Data</c> holds the saved-query
/// document's fields (<c>Queries</c> and the rest) and <see cref="DataKey"/>, the definitions document as stored, with its
/// own <c>Version</c>. The state isn't exported.
/// </summary>
/// <remarks>
/// <para><b>Import.</b> Definitions are matched by id and merged (a newer <c>UpdatedUtc</c> wins), in either import mode:
/// an import never removes a definition (Replace replaces the saved queries only). The creator, the evaluation user and the
/// last editor are mapped as the queries' owners are; a definition whose creator isn't found is skipped, one whose
/// evaluation user isn't found is kept and stops as <c>EvaluationUserMissing</c> until someone picks another. A new
/// definition has no collection (<c>CollectionId</c> cleared, status <c>NotCreated</c>) and nothing is created: a manager
/// makes it with Create on the Auto-collections tab (a run that finds a collection already carrying its mark, as on the
/// server it came from, binds that one). A definition already here keeps this server's <c>CollectionId</c>. An imported
/// (added or updated) definition whose name another definition has gets a number; the definitions the import doesn't
/// touch keep theirs. New definitions past <c>MaxAutoCollections</c> are skipped. A real import checks each imported
/// definition that follows a saved query against the saved queries as imported: one whose query is missing or isn't its
/// mapped creator's is kept (a new one without a collection, as every new one) and named in a note; it stops at its next
/// run until it follows a query of its creator's. An updated definition changes its collection at its next run, not
/// during the import.</para>
/// <para><b>Locks.</b> A real import changes the definitions under the store's gate only (<see cref="AutoCollectionStore.UpdateAsync"/>),
/// after the saved queries' import has let go of its locks; it never waits on a run.</para>
/// </remarks>
internal sealed class AutoCollectionTransfer
{
    /// <summary>The key of the definitions in the envelope's <c>Data</c>.</summary>
    public const string DataKey = "AutoCollections";

    /// <summary>The name a definition without a usable one gets.</summary>
    public const string ImportedName = "Imported auto-collection";

    private static readonly IEqualityComparer<AutoCollectionDefinition> SameContent = ImportMerge.JsonContent<AutoCollectionDefinition>();

    private readonly AutoCollectionStore _store;
    private readonly Func<AutoCollectionSettings> _settings;
    private readonly ILogger _logger;
    private readonly SavedQueryStore? _savedQueries;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionTransfer"/> class.
    /// </summary>
    /// <param name="store">The definitions.</param>
    /// <param name="settings">The settings (the most auto-collections), read on each import.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="savedQueries">The saved queries the imported definitions are checked against; none by default.</param>
    public AutoCollectionTransfer(AutoCollectionStore store, Func<AutoCollectionSettings> settings, ILogger logger, SavedQueryStore? savedQueries = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _settings = settings;
        _logger = logger;
        _savedQueries = savedQueries;
    }

    /// <summary>
    /// Gets the definitions for an export: the document as stored (a newer version's file as it is), and the users it
    /// names with their names.
    /// </summary>
    /// <param name="serverUsers">This server's users, id to name.</param>
    /// <param name="deletedUsers">The users deleted on this server.</param>
    /// <returns>The data and its users.</returns>
    /// <exception cref="PluginException">503: the store can't be read (as the saved queries' export).</exception>
    public (JsonObject Data, IReadOnlyList<ExportUser> Users) Export(IReadOnlyDictionary<Guid, string> serverUsers, IReadOnlySet<Guid> deletedUsers)
    {
        var data = _store.Documents.ExportData();
        var document = _store.Current;
        var users = ExportUser.For(AutoCollectionStore.CountRecordsByUser(document).Keys, document.KnownUsers ?? KnownUsers.Empty, serverUsers, deletedUsers);
        return (data.Data, users);
    }

    /// <summary>Returns the envelope's data without the definitions: what the saved queries' import reads.</summary>
    /// <param name="envelope">The export.</param>
    /// <returns>The envelope, the same when it has no definitions.</returns>
    public static ExportEnvelope WithoutDefinitions(ExportEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.Data.ContainsKey(DataKey))
        {
            return envelope;
        }

        var data = (JsonObject)envelope.Data.DeepClone();
        data.Remove(DataKey);
        return envelope with { Data = data };
    }

    /// <summary>Adds the definitions to the saved queries' export data.</summary>
    /// <param name="queries">The saved queries' data.</param>
    /// <param name="definitions">The definitions' data.</param>
    /// <returns>The combined data.</returns>
    public static JsonObject Combine(JsonObject queries, JsonObject definitions)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(definitions);
        var data = (JsonObject)queries.DeepClone();
        data[DataKey] = definitions.DeepClone();
        return data;
    }

    /// <summary>
    /// Reads the definitions of an export, before anything is imported: an export without them (Smart Search 1.0 and
    /// older) gives <c>null</c>.
    /// </summary>
    /// <param name="envelope">The export.</param>
    /// <param name="request">Mode, dry run and user matching.</param>
    /// <param name="serverUsers">This server's users, id to name.</param>
    /// <param name="deletedUsers">The users deleted on this server.</param>
    /// <returns>The plan, or <c>null</c>.</returns>
    /// <exception cref="PluginException">400 the definitions can't be read; 409 a newer version's, or a real import while
    /// the store is read-only.</exception>
    public Plan? Read(ExportEnvelope envelope, ImportRequest request, IReadOnlyDictionary<Guid, string> serverUsers, IReadOnlySet<Guid> deletedUsers)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(request);
        if (!envelope.Data.TryGetPropertyValue(DataKey, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonObject data)
        {
            throw PluginException.Invalid("The export's auto-collections aren't an object.");
        }

        var version = data[JsonStoreFormat.VersionProperty] is JsonValue declared && declared.TryGetValue<int>(out var v) ? v : AutoCollectionsDocument.CurrentVersion;
        if (version < 1)
        {
            throw PluginException.Invalid("The export's auto-collections have no schema version.");
        }

        var incoming = _store.Documents.ReadData(data, version, "The export's auto-collections");
        if (incoming.Extra is { Count: > 0 })
        {
            throw PluginException.Conflict("The export's auto-collections were written by a newer Smart Search; update Smart Search to import them.");
        }

        if (!request.DryRun && _store.IsReadOnly && (incoming.Definitions ?? []).Count > 0)
        {
            throw PluginException.Conflict("The auto-collections can't be changed now (a newer Smart Search's file, or one that couldn't be read); nothing was imported.");
        }

        var users = ImportUserMap.Build(AutoCollectionStore.CountRecordsByUser(incoming), envelope.Users, serverUsers, deletedUsers, request.MapUsersBy);
        return new Plan(incoming, users, request);
    }

    /// <summary>Imports the definitions: a dry run works out what would change; a real run writes it, after a snapshot.</summary>
    /// <param name="plan">The plan, from <see cref="Read"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The report's part: <c>Records</c>, <c>Notes</c>, <c>NotCreated</c> and <c>SafetySnapshot</c>.</returns>
    public async Task<IDictionary<string, object?>> ImportAsync(Plan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var max = _settings().MaxAutoCollections;
        Applied? applied = null;
        string? snapshot = null;
        if (plan.Request.DryRun || (plan.Incoming.Definitions ?? []).Count == 0)
        {
            applied = Apply(_store.Current, plan, max);
        }
        else
        {
            snapshot = await _store.SnapshotBeforeAsync(SnapshotOperations.Import, cancellationToken).ConfigureAwait(false);
            // The saved queries are read lock-free, as they are after their own import (the lock order: the definitions'
            // gate before the saved queries' lock, never the other way).
            await _store.UpdateAsync(
                current =>
                {
                    applied = Apply(current, plan, max, _savedQueries?.Current);
                    return applied.Changed ? applied.Document : current;
                },
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Imported auto-collections: {Added} added (without collections), {Updated} updated, {Unchanged} unchanged, {KeptNewer} kept newer, {Skipped} skipped; snapshot before: {Snapshot}",
                applied!.Counts.Added,
                applied.Counts.Updated,
                applied.Counts.Unchanged,
                applied.Counts.KeptNewer,
                applied.Counts.Skipped + applied.Counts.SkippedUnknownUser,
                snapshot ?? "none");
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Records"] = applied!.Counts.ToDictionary(),
            ["NotCreated"] = applied.Counts.Added,
            ["Notes"] = applied.Notes,
            ["SafetySnapshot"] = snapshot
        };
    }

    /// <summary>Works out the document after an import (pure).</summary>
    /// <param name="current">The definitions here.</param>
    /// <param name="plan">The import.</param>
    /// <param name="maxAutoCollections">The most auto-collections.</param>
    /// <param name="savedQueries">The saved queries the imported definitions are checked against (a real import, after
    /// the queries' own import); <c>null</c> checks nothing (a dry run, whose queries aren't imported yet).</param>
    /// <returns>The document, counts and notes.</returns>
    internal static Applied Apply(AutoCollectionsDocument current, Plan plan, int maxAutoCollections, SavedQueriesDocument? savedQueries = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(plan);
        var notes = new List<string>();
        var stored = current.Definitions.Where(d => d is not null).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<Guid>();
        var mapped = new List<AutoCollectionDefinition>();
        int considered = 0, unknownCreator = 0, skipped = 0, evaluationMissing = 0;
        var definitions = plan.Incoming.Definitions ?? [];
        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            if (definition is null)
            {
                continue;
            }

            if (!plan.Users.TryMap(definition.CreatedByUserId, out var creator))
            {
                unknownCreator++;
                continue;
            }

            considered++;
            var position = (i + 1).ToString(CultureInfo.InvariantCulture);
            if (definition.Id.Equals(Guid.Empty) || !seen.Add(definition.Id))
            {
                skipped++;
                notes.Add("Auto-collection " + position + (definition.Id.Equals(Guid.Empty) ? " has no id" : " has the id of an earlier one in the file") + " and was skipped.");
                continue;
            }

            Guid? evaluation = null;
            if (definition.EvaluationUserId is { } e)
            {
                if (plan.Users.TryMap(e, out var mappedEvaluation))
                {
                    evaluation = mappedEvaluation;
                }
                else
                {
                    evaluation = e;
                    evaluationMissing++;
                }
            }

            mapped.Add(definition with
            {
                Name = CleanName(definition.Name),
                CreatedByUserId = creator,
                UpdatedByUserId = plan.Users.TryMap(definition.UpdatedByUserId, out var editor) ? editor : creator,
                EvaluationUserId = evaluation,
                CollectionId = stored.TryGetValue(definition.Id, out var here) ? here.CollectionId : null,
                CreatedUtc = UtcDateTimeConverter.Utc(definition.CreatedUtc),
                UpdatedUtc = UtcDateTimeConverter.Utc(definition.UpdatedUtc)
            });
        }

        // New definitions past the most allowed are skipped, the file's later ones first.
        var newOnes = mapped.Where(d => !stored.ContainsKey(d.Id)).ToList();
        var room = Math.Max(0, maxAutoCollections - stored.Count);
        if (newOnes.Count > room)
        {
            var over = newOnes.Skip(room).ToHashSet(ReferenceEqualityComparer.Instance);
            mapped = mapped.Where(d => !over.Contains(d)).ToList();
            skipped += over.Count;
            notes.Add(over.Count.ToString(CultureInfo.InvariantCulture) + " auto-collections would go past the most there may be (" + maxAutoCollections.ToString(CultureInfo.InvariantCulture) + ") and were skipped.");
        }

        var merged = ImportMerge.Merge(current.Definitions.Where(d => d is not null), mapped, d => d.Id, d => d.UpdatedUtc, SameContent);

        // What the import added or updated: the merged record is one of the file's.
        var imported = merged.Records.Where(d => mapped.Contains(d, ReferenceEqualityComparer.Instance)).Select(d => d.Id).ToHashSet();

        // Names are unique among what the import touches: the definitions it leaves alone keep theirs (even two that share
        // one, as a rename in the server's editor once could make), then an imported one that keeps its name here, and an
        // imported one that takes a used name gets a number.
        var storedNames = stored.ToDictionary(p => p.Key, p => p.Value.Name);
        var ordered = merged.Records
            .OrderBy(d => !imported.Contains(d.Id) ? 0 : storedNames.TryGetValue(d.Id, out var name) && string.Equals(name, d.Name, StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(d => d.CreatedUtc)
            .ThenBy(d => d.Id)
            .ToList();
        var taken = new HashSet<string>(ordered.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamed = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (used.Add(ordered[i].Name) || !imported.Contains(ordered[i].Id))
            {
                continue;
            }

            var name = SavedQueryNames.Free(ordered[i].Name, taken);
            taken.Add(name);
            used.Add(name);
            ordered[i] = ordered[i] with { Name = name };
            renamed++;
        }

        var counts = merged.Counts with { Incoming = considered, Skipped = skipped, SkippedUnknownUser = unknownCreator, Removed = 0 };
        if (unknownCreator > 0)
        {
            notes.Add(unknownCreator.ToString(CultureInfo.InvariantCulture) + " auto-collections whose creator isn't a user here were skipped.");
        }

        if (evaluationMissing > 0)
        {
            notes.Add(evaluationMissing.ToString(CultureInfo.InvariantCulture) + " auto-collections read the watch state of a user who isn't here: each stops (Evaluation user missing) until someone picks another.");
        }

        if (renamed > 0)
        {
            notes.Add(renamed.ToString(CultureInfo.InvariantCulture) + " auto-collections were renamed with a number, as an auto-collection here already had the name.");
        }

        if (counts.Added > 0)
        {
            notes.Add(counts.Added.ToString(CultureInfo.InvariantCulture) + " auto-collections are new here and have no collection yet: make each with Create on the Auto-collections tab.");
        }

        if (counts.Updated > 0)
        {
            notes.Add(counts.Updated.ToString(CultureInfo.InvariantCulture) + " auto-collections already here were updated: their collections change at their next run.");
        }

        if (savedQueries is not null)
        {
            var queries = (savedQueries.Queries ?? []).Where(q => q is not null).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
            var unfollowable = ordered
                .Where(d => imported.Contains(d.Id)
                    && d.Source is { } source
                    && source.Mode.Value == SmartSearchAutoCollectionSourceMode.Reference
                    && (source.SavedQueryId is not { } id || !queries.TryGetValue(id, out var query) || !AutoCollectionStore.Follows(d, query)))
                .Select(d => d.Name)
                .ToList();
            if (unfollowable.Count > 0)
            {
                notes.Add(
                    unfollowable.Count.ToString(CultureInfo.InvariantCulture)
                    + " imported auto-collections follow a saved query that is missing here or isn't their creator's ("
                    + string.Join(", ", unfollowable.Take(10))
                    + (unfollowable.Count > 10 ? ", ..." : string.Empty)
                    + "): they were kept, aren't created, and stop at their next run until each follows a query of its creator's.");
            }
        }

        if (plan.Request.Mode == ImportMode.Replace && stored.Keys.Any(id => !seen.Contains(id)))
        {
            notes.Add("Replace doesn't remove auto-collections: the ones not in the file stay.");
        }

        var changed = counts.Added + counts.Updated + renamed > 0;
        var known = (current.KnownUsers ?? KnownUsers.Empty).Refresh(
            ordered.SelectMany(d => d.EvaluationUserId is { } e ? new[] { d.CreatedByUserId, e } : [d.CreatedByUserId]).Distinct(),
            plan.Users.Mapped.GroupBy(m => m.NewId).ToDictionary(g => g.Key, g => g.First().Name));
        changed |= !known.Equals(current.KnownUsers ?? KnownUsers.Empty);
        var document = changed ? AutoCollectionStore.WithDefinitions(current, ordered) with { KnownUsers = known } : current;
        return new Applied(document, changed, counts, notes);
    }

    private static string CleanName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length == 0 ? ImportedName : trimmed[..Math.Min(trimmed.Length, CollectionNameChecker.MaxNameLength)].TrimEnd();
    }

    /// <summary>An import's definitions, read and with their users matched.</summary>
    /// <param name="Incoming">The definitions at this version.</param>
    /// <param name="Users">The users matched.</param>
    /// <param name="Request">Mode, dry run and user matching.</param>
    internal sealed record Plan(AutoCollectionsDocument Incoming, ImportUserMap Users, ImportRequest Request);

    /// <summary>What an import makes of the definitions.</summary>
    /// <param name="Document">The document after it.</param>
    /// <param name="Changed">Whether anything changed.</param>
    /// <param name="Counts">The counts.</param>
    /// <param name="Notes">The notes.</param>
    internal sealed record Applied(AutoCollectionsDocument Document, bool Changed, ImportCounts Counts, IReadOnlyList<string> Notes);
}
