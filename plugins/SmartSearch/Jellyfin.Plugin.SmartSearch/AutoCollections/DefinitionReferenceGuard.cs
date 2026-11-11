using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The saved queries' reference guard backed by the auto-collection definitions: every write of a saved query that an
/// auto-collection follows is checked against the definitions that follow it (<see cref="AutoCollectionChecks"/>, the
/// same rules a run applies), and refused when it would break one.
/// </summary>
/// <remarks>
/// <para><b>The lock</b> is the definitions store's gate (<see cref="AutoCollectionStore.AcquireAsync"/>). The guard takes
/// it, checks, commits its own part (detached copies), and only then calls the saved-query write it was handed, which
/// takes the saved-query store's lock: the definitions before the saved queries, as everywhere. Under the gate it never
/// calls the user reconciler or any owner of users' data, and never waits for a run (a run takes its own gate before the
/// definitions' gate). <see cref="CountReferences"/> and <see cref="FindBrokenReferences"/> read memory only.</para>
/// <para><b>Edits.</b> A change that keeps the query's meaning (its name, description or sharing; the same canonical
/// query) changes no collection and goes through for its owner. A change of the query needs a collection manager (403
/// <c>ManagerRequired</c>), so a creator who lost collection management can't change a server-wide collection through
/// their saved query (they can save a copy, or detach the collections first). It is then refused (409
/// <c>BreaksAutoCollections</c>) when a run of a following definition would stop because of it, and waits for a
/// confirmation (409 <c>AutoCollectionsNeedAttention</c>) when it adds or drops watch-state rules.</para>
/// <para><b>Detach.</b> An edit or a delete that detaches the references first commits each definition's copy of the
/// query as it is stored now, then writes the query: a crash between the two leaves detached definitions and the query
/// as it was. Detaching changes no collection's members, so the query's owner may do it without managing
/// collections.</para>
/// <para><b>Bulk changes</b> (an import, a restore, a remap, an owner change) run under the gate; the saved-query side
/// asks <see cref="FindBrokenReferences"/>, which checks each following definition against the document the change would
/// store (with a remap's map applied to the definitions too, which the reconciler remaps first).</para>
/// <para><b>What counts as a reference</b>: a definition that follows the query and whose creator owns it
/// (<see cref="AutoCollectionStore.ReferencesTo"/>). One whose creator doesn't own the query has stopped
/// (<c>SourceOwnerChanged</c>): it doesn't hold the owner's changes back, isn't named to them, and a detach gives it no
/// copy, so nobody's private query becomes part of someone else's definition. A delete leaves it following a missing
/// query (<c>QueryMissing</c>).</para>
/// <para><b>Definitions that couldn't be loaded</b> (<see cref="AutoCollectionStore.DefinitionsUnknown"/>): nothing tells
/// which queries are followed, so the guard fails closed. A delete and a change of the query are refused (409
/// <c>StoreReadOnly</c>), and so are the bulk changes that remove or change queries and an owner change (the saved-query
/// side asks <see cref="ReferencesUnknown"/>); a name, a description or sharing goes through, and no query goes with its
/// deleted owner.</para>
/// </remarks>
internal sealed class DefinitionReferenceGuard : ISavedQueryReferenceGuard
{
    private readonly AutoCollectionStore _store;
    private readonly SavedQueryStore _savedQueries;
    private readonly AutoCollectionChecks _checks;
    private readonly AutoCollectionCheckContextSource _context;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefinitionReferenceGuard"/> class.
    /// </summary>
    /// <param name="store">The definitions (their gate is the guard's lock).</param>
    /// <param name="savedQueries">The saved queries, read without their lock.</param>
    /// <param name="checks">The definitions' rules.</param>
    /// <param name="context">What the rules read.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public DefinitionReferenceGuard(AutoCollectionStore store, SavedQueryStore savedQueries, AutoCollectionChecks checks, AutoCollectionCheckContextSource context, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(savedQueries);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _savedQueries = savedQueries;
        _checks = checks;
        _context = context;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool ReferencesUnknown => _store.DefinitionsUnknown;

    /// <inheritdoc />
    public int CountReferences(Guid savedQueryId) => _savedQueries.Find(savedQueryId) is { } query ? _store.ReferencesTo(query).Count : 0;

    /// <inheritdoc />
    public IReadOnlyList<SmartSearchAutoCollectionRefDto> FindBrokenReferences(SavedQueryBulkChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.IsEmpty)
        {
            return [];
        }

        var removed = change.Removed.ToHashSet();
        var changed = change.Changed.ToHashSet();
        var before = change.Before ?? _savedQueries.Current;
        var stored = (before.Queries ?? []).Where(q => q is not null).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
        var broken = new List<SmartSearchAutoCollectionRefDto>();
        AutoCollectionCheckContext? context = null;
        foreach (var definition in _store.Current.Definitions)
        {
            if (Followed(definition) is not { } id
                || (!removed.Contains(id) && !changed.Contains(id))
                || !stored.TryGetValue(id, out var query)
                || !AutoCollectionStore.Follows(definition, query))
            {
                // Not a reference to a query the change touches (one whose creator doesn't own the query has stopped).
                continue;
            }

            if (removed.Contains(id))
            {
                broken.Add(Ref(definition, ["Its saved query would be removed: it would stop as Query missing until it follows another query."]));
                continue;
            }

            if (change.After is null)
            {
                broken.Add(Ref(definition, ["Its saved query would change."]));
                continue;
            }

            context ??= _context.Create();
            var mapped = change.UserMap is { Count: > 0 } map && !_store.IsReadOnly ? Remap(definition, map) : definition;
            var was = _checks.CheckForRun(definition, context with { SavedQueries = before });
            var now = _checks.CheckForRun(mapped, context with { SavedQueries = change.After });
            if (CausedBy(was, now))
            {
                broken.Add(Ref(definition, now.Messages));
            }
        }

        return broken;
    }

    /// <inheritdoc />
    public async Task<SavedQueryGuardOutcome> UpdateAsync(SavedQueryEdit edit, Func<CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(save);
        using var held = await _store.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var id = edit.Current.Id;
        var stored = _savedQueries.Find(id);
        if (stored is null
            || (edit.IfUpdatedAt is { } read && UtcDateTimeConverter.Utc(stored.UpdatedUtc).Ticks != UtcDateTimeConverter.Utc(read).Ticks))
        {
            // The save refuses anyway (the query gone or changed since it was read).
            await save(cancellationToken).ConfigureAwait(false);
            return SavedQueryGuardOutcome.Done;
        }

        var json = edit.QueryJson.ValueKind == JsonValueKind.Object ? edit.QueryJson : Element(edit.Query);
        var sameQuery = _checks.HashOf(stored.Query) is { } before && string.Equals(before, _checks.HashOf(json), StringComparison.Ordinal);
        if (_store.DefinitionsUnknown)
        {
            // Which definitions follow it is unknown: only what changes no collection goes through.
            if (!sameQuery)
            {
                return new SavedQueryGuardOutcome(SavedQueryGuardStatus.ReferencesUnknown, []);
            }

            await save(cancellationToken).ConfigureAwait(false);
            return SavedQueryGuardOutcome.Done;
        }

        var references = _store.ReferencesTo(stored);
        if (references.Count == 0)
        {
            await save(cancellationToken).ConfigureAwait(false);
            return SavedQueryGuardOutcome.Done;
        }

        if (edit.DetachReferences)
        {
            // The save's own checks first (its name, the version read), under the gate: a save they refuse detaches
            // nothing. A write of the query that fails after the detach leaves the definitions detached and the query as
            // it was; saving again goes through.
            edit.CheckSave?.Invoke(_savedQueries.Current);
            await DetachHeldAsync(held, stored, edit.Caller.Id, cancellationToken).ConfigureAwait(false);
            await save(cancellationToken).ConfigureAwait(false);
            return SavedQueryGuardOutcome.Done;
        }

        if (sameQuery)
        {
            // The same query: a name, a description or sharing changes no collection.
            await save(cancellationToken).ConfigureAwait(false);
            return SavedQueryGuardOutcome.Done;
        }

        if (!edit.CallerIsAdmin && !CurrentUser.HasAny(edit.Caller, PermissionKind.EnableCollectionManagement))
        {
            return new SavedQueryGuardOutcome(SavedQueryGuardStatus.ManagerRequired, references.Select(d => Ref(d, ["Only a collection manager can change what this auto-collection contains."])).ToList());
        }

        var document = _savedQueries.Current;
        var after = SavedQueryStore.WithQueries(document, document.Queries.Select(q => q.Id.Equals(id) ? q with { Query = json } : q));
        var context = _context.Create();
        var broken = new List<SmartSearchAutoCollectionRefDto>();
        var attention = new List<SmartSearchAutoCollectionRefDto>();
        var affected = new List<SmartSearchAutoCollectionRefDto>();
        foreach (var definition in references)
        {
            var was = _checks.CheckForRun(definition, context with { SavedQueries = document });
            var now = _checks.CheckForRun(definition, context with { SavedQueries = after });
            if (CausedBy(was, now))
            {
                if (now.Status == SmartSearchAutoCollectionStatus.EvaluationUserMissing && now.Reason == AutoCollectionReasons.EvaluationUserRequired)
                {
                    attention.Add(Ref(definition, ["This adds a watch-state rule: the auto-collection pauses until a manager picks whose watch state it follows."]));
                }
                else
                {
                    broken.Add(Ref(definition, now.Messages));
                }
            }
            else if (now.Ok && now.ClearEvaluationUser && !was.ClearEvaluationUser)
            {
                attention.Add(Ref(definition, ["The query no longer reads watch state: the auto-collection stops following its evaluation user's."]));
            }
            else
            {
                affected.Add(Ref(definition, []));
            }
        }

        if (broken.Count > 0)
        {
            return new SavedQueryGuardOutcome(SavedQueryGuardStatus.BreaksReferences, broken);
        }

        if (attention.Count > 0 && !edit.AcknowledgeWarnings)
        {
            return new SavedQueryGuardOutcome(SavedQueryGuardStatus.NeedsAttention, attention);
        }

        await save(cancellationToken).ConfigureAwait(false);
        return new SavedQueryGuardOutcome(SavedQueryGuardStatus.Done, [.. attention, .. affected]);
    }

    /// <inheritdoc />
    public async Task<SavedQueryGuardOutcome> DeleteAsync(SavedQueryDeletion deletion, Func<CancellationToken, Task> delete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentNullException.ThrowIfNull(delete);
        using var held = await _store.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var id = deletion.Current.Id;
        if (_savedQueries.Find(id) is not { } stored)
        {
            await delete(cancellationToken).ConfigureAwait(false);
            return SavedQueryGuardOutcome.Done;
        }

        if (_store.DefinitionsUnknown)
        {
            return new SavedQueryGuardOutcome(SavedQueryGuardStatus.ReferencesUnknown, []);
        }

        var references = _store.ReferencesTo(stored);
        if (references.Count == 0)
        {
            await delete(cancellationToken).ConfigureAwait(false);
            return SavedQueryGuardOutcome.Done;
        }

        if (!deletion.DetachReferences)
        {
            return new SavedQueryGuardOutcome(SavedQueryGuardStatus.Referenced, references.Select(d => Ref(d, ["Follows this query."])).ToList());
        }

        // The copies first, committed; the query only after that write succeeded.
        await DetachHeldAsync(held, stored, deletion.Caller.Id, cancellationToken).ConfigureAwait(false);
        await delete(cancellationToken).ConfigureAwait(false);
        return new SavedQueryGuardOutcome(SavedQueryGuardStatus.Done, references.Select(d => Ref(d, ["Detached: it keeps a copy of the query."])).ToList());
    }

    /// <inheritdoc />
    public async Task<T> BulkChangeAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var held = await _store.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await write(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A definition switched to its own copy of the query it followed.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="query">The query as stored now.</param>
    /// <param name="by">Who detaches it.</param>
    /// <param name="now">The time.</param>
    /// <returns>The detached definition.</returns>
    internal static AutoCollectionDefinition Detached(AutoCollectionDefinition definition, JsonElement query, Guid by, DateTime now)
        => definition with
        {
            Source = new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Snapshot, Snapshot = query.Clone(), SnapshotTakenUtc = now },
            UpdatedByUserId = by,
            UpdatedUtc = now > definition.UpdatedUtc ? now : definition.UpdatedUtc.AddTicks(1)
        };

    // The saved query a definition follows (by reference), or null.
    private static Guid? Followed(AutoCollectionDefinition definition)
        => definition.Source is { } source && source.Mode.Value != SmartSearchAutoCollectionSourceMode.Snapshot ? source.SavedQueryId : null;

    // Whether a change makes the definition stop: a stop of the query's making (not the creator's, the store's or the
    // definition's own version) that it didn't already have.
    private static bool CausedBy(AutoCollectionCheck was, AutoCollectionCheck now)
        => !now.Ok
            && now.Status is not (SmartSearchAutoCollectionStatus.CreatorMissing or SmartSearchAutoCollectionStatus.NeedsNewerVersion)
            && !(was.Status == now.Status && string.Equals(was.Reason, now.Reason, StringComparison.Ordinal));

    private static AutoCollectionDefinition Remap(AutoCollectionDefinition definition, IReadOnlyDictionary<Guid, Guid> map)
        => definition with
        {
            CreatedByUserId = map.TryGetValue(definition.CreatedByUserId, out var creator) ? creator : definition.CreatedByUserId,
            EvaluationUserId = definition.EvaluationUserId is { } e && map.TryGetValue(e, out var evaluation) ? evaluation : definition.EvaluationUserId
        };

    private static SmartSearchAutoCollectionRefDto Ref(AutoCollectionDefinition definition, IReadOnlyList<string> messages)
        => new() { Id = definition.Id, Name = definition.Name, Messages = messages };

    private static JsonElement Element(SmartSearchQueryDefinition query)
    {
        using var document = JsonDocument.Parse(QueryJson.Write(query));
        return document.RootElement.Clone();
    }

    // Every reference to the query (a definition of its owner's that follows it) gets its copy of it, in one write of the
    // definitions, under the gate. A definition of someone else's that follows it gets nothing of the owner's.
    private async Task DetachHeldAsync(AutoCollectionStore.Held held, SavedQueryRecord stored, Guid by, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var count = 0;
        var savedQueryId = stored.Id;
        await _store.UpdateHeldAsync(
            held,
            document =>
            {
                count = 0;
                var definitions = document.Definitions.Select(d =>
                {
                    if (!AutoCollectionStore.Follows(d, stored))
                    {
                        return d;
                    }

                    count++;
                    return Detached(d, stored.Query, by, now);
                }).ToList();
                return count == 0 ? document : AutoCollectionStore.WithDefinitions(document, definitions);
            },
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Count} auto-collection(s) detached from saved query {QueryId}", count, savedQueryId);
    }
}
