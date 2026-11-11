using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Runs one definition: checks it, finds its collection, works out its members, compares them with the collection's live
/// links and writes only when something differs. Every stop before the write leaves the collection as it is.
/// </summary>
/// <remarks>
/// One run at a time (the worker schedules them; a delete takes the same lock). A definition
/// a newer version wrote, or a store a newer version wrote, is never run: its check stops it before anything is read.
/// </remarks>
internal sealed class DefinitionRunner : IDisposable
{
    private readonly AutoCollectionStore _store;
    private readonly AutoCollectionStateStore _state;
    private readonly AutoCollectionChecks _checks;
    private readonly AutoCollectionCheckContextSource _context;
    private readonly CollectionLocator _locator;
    private readonly LinkResolver _links;
    private readonly MembershipPlanner _planner;
    private readonly MixedAudienceCheck _mixedAudience;
    private readonly CollectionNameChecker _names;
    private readonly DirectReplaceWriter _writer;
    private readonly MassRemovalGuard _guard;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ManualEditKeeper? _keeper;
    private readonly ManagerApiWriter? _managerApi;
    private readonly AutoCollectionDefaultImage? _defaultImage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="DefinitionRunner"/> class.
    /// </summary>
    /// <param name="store">The definitions.</param>
    /// <param name="state">The run state.</param>
    /// <param name="checks">The definition checks.</param>
    /// <param name="context">The check context.</param>
    /// <param name="locator">The collection locator.</param>
    /// <param name="links">The link resolver.</param>
    /// <param name="planner">The membership planner.</param>
    /// <param name="mixedAudience">The mixed-audience check.</param>
    /// <param name="names">The name checker (a create).</param>
    /// <param name="writer">The writer.</param>
    /// <param name="guard">The mass-removal guard.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="keeper">Keeps the pins and excludes (edits made outside the plugin, moved and gone items); without,
    /// those edits wait and the entries are used as they are.</param>
    /// <param name="managerApi">The writer through the server's collection calls, used when the settings ask for it;
    /// without, the direct writer always writes.</param>
    /// <param name="defaultImage">Gives a collection without a poster the default one, once; without, none is given.</param>
    public DefinitionRunner(
        AutoCollectionStore store,
        AutoCollectionStateStore state,
        AutoCollectionChecks checks,
        AutoCollectionCheckContextSource context,
        CollectionLocator locator,
        LinkResolver links,
        MembershipPlanner planner,
        MixedAudienceCheck mixedAudience,
        CollectionNameChecker names,
        DirectReplaceWriter writer,
        MassRemovalGuard guard,
        TimeProvider time,
        ILogger logger,
        ManualEditKeeper? keeper = null,
        ManagerApiWriter? managerApi = null,
        AutoCollectionDefaultImage? defaultImage = null)
    {
        _store = store;
        _state = state;
        _checks = checks;
        _context = context;
        _locator = locator;
        _links = links;
        _planner = planner;
        _mixedAudience = mixedAudience;
        _names = names;
        _writer = writer;
        _guard = guard;
        _time = time;
        _logger = logger;
        _keeper = keeper;
        _managerApi = managerApi;
        _defaultImage = defaultImage;
    }

    // The writer through the server's collection calls when the settings ask for it, else null (the direct writer).
    private ManagerApiWriter? ManagerApi => _context.Settings.CollectionWriter == CollectionWriterKind.ManagerApi ? _managerApi : null;

    /// <summary>Runs a definition: brings its collection in line with its query, writing only what differs.</summary>
    /// <param name="definitionId">The definition's id.</param>
    /// <param name="reason">Why it runs.</param>
    /// <param name="cancellationToken">Cancels the run before the write.</param>
    /// <param name="conditions">What the run shares with its pass and knows about the server; none by default.</param>
    /// <returns>The state after the run.</returns>
    /// <exception cref="PluginException">404 when no definition has the id.</exception>
    public async Task<AutoCollectionState> RunAsync(Guid definitionId, SmartSearchAutoCollectionRunReason reason, CancellationToken cancellationToken, AutoCollectionRunConditions? conditions = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunLockedAsync(definitionId, reason, create: false, conditions ?? AutoCollectionRunConditions.None, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Creates a definition's collection (a new definition, or one whose collection was deleted): after the same checks
    /// as a run, and only under a name no other collection uses. A collection that already carries the definition's mark
    /// is bound again instead.
    /// </summary>
    /// <param name="definitionId">The definition's id.</param>
    /// <param name="reason">Why it runs.</param>
    /// <param name="cancellationToken">Cancels the run before the write.</param>
    /// <param name="removeIfRefused">Whether a refused create (409) deletes the definition again: a new one that never had a collection.</param>
    /// <returns>The state after the run.</returns>
    /// <exception cref="PluginException">404 for no definition; 409 when it has a collection, or the name is taken.</exception>
    public async Task<AutoCollectionState> CreateAsync(Guid definitionId, SmartSearchAutoCollectionRunReason reason, CancellationToken cancellationToken, bool removeIfRefused = false)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunLockedAsync(definitionId, reason, create: true, AutoCollectionRunConditions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginException) when (removeIfRefused)
        {
            // Refused (the name was taken meanwhile): the definition goes too, nothing was made.
            await RemoveDefinitionAsync(definitionId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Detaches a definition from its collection: the mark is removed, the collection stays as an ordinary one, and the
    /// definition and its state are deleted.
    /// </summary>
    /// <param name="definitionId">The definition's id.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>Whether a collection was detached.</returns>
    /// <exception cref="PluginException">404 for no definition; 409 when the store is read-only.</exception>
    public Task<bool> DeleteKeepingCollectionAsync(Guid definitionId, CancellationToken cancellationToken)
        => DeleteAsync(definitionId, null, cancellationToken);

    /// <summary>
    /// Deletes a definition. With <paramref name="deleteCollectionAs"/>, its collection is deleted too, but only when it
    /// still carries the definition's mark and that user could delete it through the server's own endpoint (they can
    /// see it and may delete it); otherwise nothing is deleted. Without, the collection is detached and kept.
    /// </summary>
    /// <param name="definitionId">The definition's id.</param>
    /// <param name="deleteCollectionAs">The user deleting the collection too, or <c>null</c> to keep it.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>Whether a collection was detached or deleted.</returns>
    /// <exception cref="PluginException">404 for no definition; 403 when the collection can't be deleted by the user;
    /// 409 when the store is read-only.</exception>
    public async Task<bool> DeleteAsync(Guid definitionId, User? deleteCollectionAs, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var definition = _store.Find(definitionId) ?? throw AutoCollectionErrors.NotFound;
            if (_store.IsReadOnly)
            {
                throw AutoCollectionErrors.StoreReadOnly;
            }

            var location = _locator.Locate(definition);
            if (deleteCollectionAs is not null && location.Kind != CollectionLocationKind.NotCreated && location.Kind != CollectionLocationKind.Orphaned)
            {
                // The same checks as the server's own delete, and the mark just before it.
                var box = location.Writable ? _writer.FindDeletable(location.Box!.Id, deleteCollectionAs) : null;
                if (box is null || !CollectionLocator.HasTether(box, definitionId))
                {
                    throw AutoCollectionErrors.CannotDeleteCollection;
                }

                await _writer.DeleteAsync(box).ConfigureAwait(false);
                await RemoveDefinitionAsync(definitionId).ConfigureAwait(false);
                return true;
            }

            // The definition goes first: a detached collection whose definition couldn't be removed would be taken
            // over by nothing, while a kept mark without a definition is inert.
            await RemoveDefinitionAsync(definitionId).ConfigureAwait(false);
            return deleteCollectionAs is null && location.Writable && await _writer.DetachAsync(location.Box!, definition.Id, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task RemoveDefinitionAsync(Guid definitionId)
    {
        await _store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, d.Definitions.Where(x => !x.Id.Equals(definitionId))), CancellationToken.None).ConfigureAwait(false);
        await _state.PruneAsync(_store.Current.Definitions.Select(d => d.Id).ToList(), CancellationToken.None).ConfigureAwait(false);
    }

    // A run that stopped before its write: the hold stays, but its approval doesn't (a later run needs a new one).
    private static AutoCollectionState Stopped(AutoCollectionState? previous, SmartSearchAutoCollectionStatus status, string? reason, string? error, DateTime now, SmartSearchAutoCollectionRunReason runReason, string? safeError = null)
        => (previous ?? new AutoCollectionState()) with
        {
            Status = status,
            Reason = reason,
            LastError = safeError ?? (error is null ? null : AutoCollectionReasons.SafeText(reason)),
            LastErrorDetail = error,
            LastRunUtc = now,
            LastReason = runReason,
            PendingGuard = previous?.PendingGuard is { } held ? held with { ApprovedUtc = null } : null
        };

    private async Task<AutoCollectionState> RunLockedAsync(Guid definitionId, SmartSearchAutoCollectionRunReason reason, bool create, AutoCollectionRunConditions conditions, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        var phases = new RunPhases(_time);
        var now = _time.GetUtcNow().UtcDateTime;
        var definition = _store.Find(definitionId) ?? throw AutoCollectionErrors.NotFound;
        var previous = _state.Find(definitionId);

        // The edits made outside the plugin that this run sorted (none until it does): its state no longer keeps them.
        IReadOnlyList<AutoCollectionManualEdit> sortedEdits = [];

        // A create that stops before the collection exists stays asked for: the next run makes it.
        Task<AutoCollectionState> StopAsync(SmartSearchAutoCollectionStatus status, string? stopReason, string? error = null, string? safeError = null)
            => SaveStateAsync(definitionId, Stopped(previous, status, stopReason, error, now, reason, safeError) with { CreatePending = create || previous?.CreatePending == true }, previous, sortedEdits);

        if (!definition.Enabled && !create)
        {
            return await StopAsync(SmartSearchAutoCollectionStatus.Disabled, null).ConfigureAwait(false);
        }

        var check = _checks.CheckForRun(definition, _context.Create());
        if (check.ClearEvaluationUser && !_store.IsReadOnly)
        {
            definition = await _store.UpdateDefinitionAsync(definitionId, d => d with { EvaluationUserId = null }, cancellationToken).ConfigureAwait(false) ?? definition;
        }

        if (!check.Ok)
        {
            return await StopAsync(check.Status, check.Reason, string.Join(" ", check.Messages)).ConfigureAwait(false);
        }

        phases.Mark("check");
        var location = _locator.Locate(definition);
        phases.Mark("locate");

        // A run after a create that was refused (busy, timed out, too broad) makes the collection, as the create would have.
        var retried = !create && location.Kind == CollectionLocationKind.NotCreated && previous?.CreatePending == true;
        create |= retried;

        // A create that finds a collection already carrying the mark (a create that failed after the server made it, or
        // an import) binds it and updates it as a run does.
        create &= location.Kind != CollectionLocationKind.Rebound;
        if (create && location.Kind is not (CollectionLocationKind.NotCreated or CollectionLocationKind.Orphaned))
        {
            throw AutoCollectionErrors.HasCollection;
        }

        if (!create)
        {
            switch (location.Kind)
            {
                case CollectionLocationKind.NotCreated:
                    return await StopAsync(SmartSearchAutoCollectionStatus.NotCreated, null).ConfigureAwait(false);
                case CollectionLocationKind.Orphaned:
                    return await StopAsync(SmartSearchAutoCollectionStatus.Orphaned, AutoCollectionReasons.CollectionDeleted).ConfigureAwait(false);
                case CollectionLocationKind.TetherMissing:
                    return await StopAsync(SmartSearchAutoCollectionStatus.TetherMissing, AutoCollectionReasons.TetherMissing).ConfigureAwait(false);
            }
        }

        if (location.Duplicates.Count > 0)
        {
            _logger.LogWarning("Auto-collection {Id} has duplicate marked collections {Duplicates}; the oldest is used, the others are left alone.", definitionId, location.Duplicates);
        }

        if (location.Kind == CollectionLocationKind.Rebound)
        {
            definition = await _store.UpdateDefinitionAsync(definitionId, d => d with { CollectionId = location.Box!.Id }, cancellationToken).ConfigureAwait(false) ?? definition;
        }

        if (!create)
        {
            definition = await AdoptNativeRenameAsync(definition, location.Box!, previous, now, cancellationToken).ConfigureAwait(false);
        }

        // Pins and excludes on moved items follow them; those on gone items are detached (and come back with the item).
        if (_keeper is not null)
        {
            var fixes = _keeper.Maintain(definition.ManualEdits, now);
            if (fixes.Count > 0)
            {
                definition = await _store.UpdateDefinitionAsync(definitionId, d => d with { ManualEdits = ManualEditKeeper.ApplyFixes(d.ManualEdits, fixes) }, cancellationToken).ConfigureAwait(false) ?? definition;
            }
        }

        DesiredMembership desired;
        phases.Mark("rename");
        async Task<DesiredMembership> PlanAsync()
        {
            var planned = await _planner.PlanAsync(definition, check, EvaluationPurpose.AutoCollection, conditions.PassUtc ?? now, _context.Settings.MaxMembersPerCollection, cancellationToken, conditions.ItemCache).ConfigureAwait(false);
            phases.Mark("plan");
            planned = _guard.WithScopeCount(planned);
            phases.Mark("scope");
            return planned;
        }

        try
        {
            desired = await PlanAsync().ConfigureAwait(false);
        }
        catch (SmartSearchRefusedException ex)
        {
            return await StopAsync(SmartSearchAutoCollectionStatus.Error, ex.Code, ex.Message).ConfigureAwait(false);
        }

        // The edits made outside the plugin since the last run become pins and excludes (or are reverted) before anything
        // is compared; too many to keep stops the run, so nothing is reverted until a manager decides.
        ManualEditKeeper.Sorted? sorted = null;
        var edits = previous?.PendingEdits ?? [];
        if (_keeper is not null && !create && previous?.PendingEditsOverflow == true && definition.ManualEdits.Mode.Value == SmartSearchManualEditsMode.Respect)
        {
            // More edits than were kept: sorting only those would revert the rest.
            _logger.LogWarning("Auto-collection {Id}: more than {Max} items were added or removed outside the plugin; nothing is reverted until a manager decides.", definitionId, AutoCollectionState.MaxPendingEdits);
            var overflow = "More than " + AutoCollectionState.MaxPendingEdits + " items were added or removed outside the rules since the last update, more than can be sorted into pins and excludes. Nothing is reverted until a manager decides: switch the collection to Overwrite, or save it to keep the first " + AutoCollectionState.MaxPendingEdits + " edits and revert the rest.";
            return await StopAsync(SmartSearchAutoCollectionStatus.Guarded, AutoCollectionReasons.TooManyManualEdits, overflow, overflow).ConfigureAwait(false);
        }

        if (_keeper is not null && !create && edits.Count > 0 && desired.Ok)
        {
            sorted = _keeper.Sort(definition, location.Box!, edits, desired);
            if (sorted.TooMany)
            {
                _logger.LogWarning("Auto-collection {Id}: {Count} items added or removed outside the plugin would take the pins or excludes over {Max}; nothing is reverted until a manager decides.", definitionId, edits.Count, AutoCollectionManualEdits.MaxEntries);
                var tooMany = edits.Count + " items were added or removed outside the rules; keeping them would take the pins or excludes over " + AutoCollectionManualEdits.MaxEntries + ". Make room, or switch the collection to Overwrite.";
                return await StopAsync(SmartSearchAutoCollectionStatus.Guarded, AutoCollectionReasons.TooManyManualEdits, tooMany, tooMany).ConfigureAwait(false);
            }

            sortedEdits = edits;
            if (sorted.Changes.Count > 0)
            {
                var changes = sorted.Changes;
                var keeper = _keeper;
                definition = await _store.UpdateDefinitionAsync(
                    definitionId,
                    d => d with { ManualEdits = keeper.Apply(d.ManualEdits, changes, now), UpdatedUtc = now > d.UpdatedUtc ? now : d.UpdatedUtc.AddTicks(1) },
                    cancellationToken).ConfigureAwait(false) ?? definition;
                try
                {
                    desired = await PlanAsync().ConfigureAwait(false);
                }
                catch (SmartSearchRefusedException ex)
                {
                    return await StopAsync(SmartSearchAutoCollectionStatus.Error, ex.Code, ex.Message).ConfigureAwait(false);
                }
            }

            if (sorted.Pinned + sorted.Excluded + sorted.Reverted > 0)
            {
                _logger.LogInformation("Auto-collection {Id}: of the edits made outside the plugin, {Pinned} kept as pins, {Excluded} as excludes, {Reverted} reverted.", definitionId, sorted.Pinned, sorted.Excluded, sorted.Reverted);
            }
        }

        if (!desired.Ok)
        {
            var tooBig = "The collection would have " + desired.Ids.Count + " items; at most " + _context.Settings.MaxMembersPerCollection + " are allowed.";
            return await StopAsync(SmartSearchAutoCollectionStatus.Error, desired.Error, tooBig, tooBig).ConfigureAwait(false);
        }

        var mixed = _mixedAudience.CountPartialViewers(desired.Ids, cancellationToken, desired.SourceLibraryIds, conditions.Audience) > 0;
        phases.Mark("audience");
        if (create)
        {
            try
            {
                return await CreateLockedAsync(definition, check, desired, mixed, previous, reason, started, now, cancellationToken).ConfigureAwait(false);
            }
            catch (PluginException ex) when (retried)
            {
                // No caller to answer: the run records why (a name taken meanwhile), and the next one tries again.
                return await StopAsync(SmartSearchAutoCollectionStatus.Error, ex.Code, ex.Message).ConfigureAwait(false);
            }
        }

        var box = location.Box!;
        var links = _links.Resolve(box);
        phases.Mark("links");

        // Through the server's calls the order is the name's, and dead and duplicate links stay (counted, no change).
        var managerApi = ManagerApi;
        var shown = managerApi?.Shown(definition) ?? definition;
        var diff = CollectionDiff.Compute(box, links, desired.Ids, shown);
        if (managerApi is not null)
        {
            diff = ManagerApiWriter.Writable(diff);
        }

        var unprunable = managerApi is null ? 0 : links.Dead;
        phases.Mark("diff");
        var outcome = RunOutcome.NoChange;

        // The guard: emptying a collection that has members (dead links count: a missing drive leaves only those) is never
        // done on its own; large removals while the library looks unhealthy wait for a review, and the adds go ahead. A
        // manager's approval lets through the removals that were approved, or fewer, and nothing more.
        var current = links.Members.Count + links.Dead;
        var linked = links.Members.ToHashSet();
        GuardVerdict Judge(IReadOnlyCollection<Guid> removed, int dead, DesiredMembership wanted)
        {
            var verdict = _guard.Check(definition, previous, current, removed.Count + dead, wanted, check.QueryHash, _context.Settings, conditions, reason);
            if (verdict.Tripped && previous?.PendingGuard is { } approved && approved.Covers(removed, dead, verdict.Empty))
            {
                _logger.LogInformation("Auto-collection {Id}: the approved removals ({Count}) go.", definitionId, removed.Count + dead);
                return GuardVerdict.Pass;
            }

            return verdict;
        }

        async Task<AutoCollectionState> HoldAsync(GuardVerdict verdict, IReadOnlyCollection<Guid> removed, int dead, bool writeAdds)
        {
            var held = AutoCollectionPendingGuard.Of(previous?.PendingGuard, removed, dead, verdict, now);
            _logger.LogWarning("Auto-collection {Id} would remove {Count} of {Current} members ({Reasons}); the removals are held for a review.", definitionId, verdict.Removals, current, string.Join(", ", verdict.Reasons));
            if (verdict.Empty)
            {
                return await SaveStateAsync(definitionId, Stopped(previous, SmartSearchAutoCollectionStatus.Guarded, GuardReasons.Empty, null, now, reason) with { PendingGuard = held }, previous, sortedEdits).ConfigureAwait(false);
            }

            // Adds and presentation only: the members it would remove stay after the others, and so do the dead links.
            // When a member can't be loaded any more the write would drop it: then nothing is written.
            var partial = diff with { Removed = [], Dead = 0 };
            var heldOutcome = RunOutcome.NoChange;
            if (writeAdds && !partial.IsEmpty)
            {
                bool KeepsEveryMember(IReadOnlyCollection<Guid> missing) => managerApi is not null || !missing.Any(linked.Contains);
                var written = managerApi is not null
                    ? await managerApi.WriteAsync(box, partial, [.. desired.Ids, .. diff.Removed], shown, cancellationToken, KeepsEveryMember).ConfigureAwait(false)
                    : await _writer.WriteAsync(box, links, [.. desired.Ids, .. diff.Removed], definition, cancellationToken, keepDeadLinks: true, KeepsEveryMember).ConfigureAwait(false) is not null;
                heldOutcome = written ? RunOutcome.Written : RunOutcome.NoChange;
            }

            // The candidate count and query of the last run that wasn't held stay the baseline, so a drop keeps holding
            // the runs after this one until the items come back (or the removals are applied or kept).
            var heldDuration = (long)_time.GetElapsedTime(started).TotalMilliseconds;
            var heldState = Succeeded(previous, check, desired, links.Hash, mixed, now, reason, heldDuration, partial, heldOutcome, sorted) with
            {
                Status = SmartSearchAutoCollectionStatus.Guarded,
                Reason = verdict.Reasons[0],
                LastSuccessUtc = previous?.LastSuccessUtc,
                LastQueryHash = previous is { LastCandidateCount: > 0 } ? previous.LastQueryHash : check.QueryHash,
                LastCandidateCount = previous is { LastCandidateCount: > 0 } ? previous.LastCandidateCount : desired.CandidateCount,
                LastMemberCount = desired.Ids.Count + diff.Removed.Count,
                PendingGuard = held,
                UnprunableDeadLinks = unprunable,
                DefaultImage = await DefaultImageAsync(box, previous, now).ConfigureAwait(false)
            };
            return await SaveStateAsync(definitionId, heldState, previous, sortedEdits).ConfigureAwait(false);
        }

        var verdict = Judge(diff.Removed, diff.Dead, desired);
        if (verdict.Tripped)
        {
            return await HoldAsync(verdict, diff.Removed, diff.Dead, writeAdds: true).ConfigureAwait(false);
        }

        if (!diff.IsEmpty)
        {
            // The writer loads the members just before it writes, and one gone since the plan is dropped too: the guard
            // looks again at what the write really leaves, and holds instead when that is too much.
            (GuardVerdict Verdict, List<Guid> Removed)? refused = null;
            bool Accept(IReadOnlyCollection<Guid> missing)
            {
                if (missing.Count == 0)
                {
                    return true;
                }

                // The direct writer drops a linked member it can't load; the server's calls only skip a new one.
                var gone = missing.ToHashSet();
                var lost = managerApi is null ? desired.Ids.Where(id => gone.Contains(id) && linked.Contains(id)).Distinct().ToList() : [];
                var left = desired.Ids.Where(id => !gone.Contains(id) || (managerApi is not null && linked.Contains(id))).ToList();
                var removed = diff.Removed.Concat(lost).Distinct().ToList();
                var again = Judge(removed, diff.Dead, desired with { Ids = left });
                if (!again.Tripped)
                {
                    return true;
                }

                refused = (again, removed);
                return false;
            }

            var written = managerApi is not null
                ? await managerApi.WriteAsync(box, diff, desired.Ids, shown, cancellationToken, Accept).ConfigureAwait(false)
                : await _writer.WriteAsync(box, links, desired.Ids, definition, cancellationToken, accept: Accept).ConfigureAwait(false) is not null;
            if (!written && refused is { } held)
            {
                return await HoldAsync(held.Verdict, held.Removed, diff.Dead, writeAdds: false).ConfigureAwait(false);
            }

            outcome = written ? RunOutcome.Written : RunOutcome.NoChange;
        }

        phases.Mark("write");
        var image = await DefaultImageAsync(box, previous, now).ConfigureAwait(false);
        phases.Mark("image");
        var duration = (long)_time.GetElapsedTime(started).TotalMilliseconds;
        var saved = await SaveStateAsync(definitionId, Succeeded(previous, check, desired, links.Hash, mixed, now, reason, duration, diff, outcome, sorted) with { UnprunableDeadLinks = unprunable, DefaultImage = image }, previous, sortedEdits).ConfigureAwait(false);
        phases.Mark("state");
        _logger.LogDebug("Auto-collection {Id} ran: {Phases} (ms).", definitionId, phases);
        return saved;
    }

    private async Task<AutoCollectionState> CreateLockedAsync(
        AutoCollectionDefinition definition,
        AutoCollectionCheck check,
        DesiredMembership desired,
        bool mixed,
        AutoCollectionState? previous,
        SmartSearchAutoCollectionRunReason reason,
        long started,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var name = await _names.CheckAsync(definition.Name, definition.Id, cancellationToken).ConfigureAwait(false);
        if (name.Rebind is { } rebind)
        {
            // A collection already carries the mark under this name: bind it again, then update it as a run does.
            await _store.UpdateDefinitionAsync(definition.Id, d => d with { CollectionId = rebind }, cancellationToken).ConfigureAwait(false);
            return await RunLockedAsync(definition.Id, reason, create: false, AutoCollectionRunConditions.None, cancellationToken).ConfigureAwait(false);
        }

        if (!name.Available)
        {
            // No caller to show the other collection to: only that the name is taken, with the suggestions.
            throw AutoCollectionErrors.NameTaken(name.ForViewer(_ => false));
        }

        // The name again just before the server creates the collection: loading the members takes a while, and a
        // collection made meanwhile under the name would otherwise lose its row to ours.
        async Task RecheckNameAsync()
        {
            var again = await _names.CheckAsync(definition.Name, definition.Id, CancellationToken.None).ConfigureAwait(false);
            if (!again.Available)
            {
                throw AutoCollectionErrors.NameTaken(again.ForViewer(_ => false));
            }
        }

        var box = await _writer.CreateAsync(ManagerApi?.Shown(definition) ?? definition, desired.Ids, cancellationToken, RecheckNameAsync).ConfigureAwait(false);
        await _store.UpdateDefinitionAsync(definition.Id, d => d with { CollectionId = box.Id }, CancellationToken.None).ConfigureAwait(false);
        var links = _links.Resolve(box);
        var image = await DefaultImageAsync(box, previous, now).ConfigureAwait(false);
        var duration = (long)_time.GetElapsedTime(started).TotalMilliseconds;
        var diff = new CollectionDiff(desired.Ids, [], 0, 0, false, []);
        return await SaveStateAsync(definition.Id, Succeeded(previous, check, desired, links.Hash, mixed, now, reason, duration, diff, RunOutcome.Created) with { UnprunableDeadLinks = 0, DefaultImage = image }, previous).ConfigureAwait(false);
    }

    // The default poster, after the collection's own write: once per collection, only while the setting is on, and only
    // when it has none (AutoCollectionDefaultImage). It goes through the server's image saving, which raises no collection
    // event, so the edit listener records nothing, and the members, their hash and the links are as the run left them.
    private async Task<AutoCollectionDefaultImageRecord?> DefaultImageAsync(BoxSet box, AutoCollectionState? previous, DateTime now)
        => _defaultImage is null
            ? previous?.DefaultImage
            : await _defaultImage.ApplyAsync(box, previous?.DefaultImage, _context.Settings.DefaultImage, now).ConfigureAwait(false);

    // A rename made in the server's own editor is kept when the definition wasn't changed since its last run: the
    // definition takes the collection's name (as a change, so a client holding the old version must read it again).
    // Changed since, the definition's name wins and the run writes it back. A name that isn't one (empty, too long), or one
    // another definition has (names are unique among the definitions, and an import's numbering counts on it), is never
    // taken: the run writes the definition's name back, and the log says why.
    private async Task<AutoCollectionDefinition> AdoptNativeRenameAsync(AutoCollectionDefinition definition, BoxSet box, AutoCollectionState? previous, DateTime now, CancellationToken cancellationToken)
    {
        var native = (box.Name ?? string.Empty).Trim();
        if (string.Equals(native, definition.Name, StringComparison.Ordinal)
            || native.Length is 0 or > CollectionNameChecker.MaxNameLength
            || previous?.LastRunUtc is not { } lastRun
            || definition.UpdatedUtc > lastRun)
        {
            return definition;
        }

        var clash = false;
        var adopted = await _store.UpdateDefinitionAsync(
            definition.Id,
            d =>
            {
                if (d.UpdatedUtc > lastRun)
                {
                    return d;
                }

                // Read under the definitions' gate, which every change of a name holds.
                clash = _store.Current.Definitions.Any(o => !o.Id.Equals(d.Id) && string.Equals(o.Name, native, StringComparison.OrdinalIgnoreCase));
                return clash ? d : d with { Name = native, UpdatedUtc = now > d.UpdatedUtc ? now : d.UpdatedUtc.AddTicks(1) };
            },
            cancellationToken).ConfigureAwait(false);
        if (clash)
        {
            _logger.LogWarning("Auto-collection {Id}: its collection was renamed in the server's editor to a name another auto-collection has; the auto-collection's own name is written back.", definition.Id);
            _logger.LogDebug("Auto-collection {Id} keeps the name {Name} over {Native}.", definition.Id, definition.Name, native);
        }

        if (adopted is not null && string.Equals(adopted.Name, native, StringComparison.Ordinal))
        {
            _logger.LogInformation("Auto-collection {Id} takes the name its collection was given in the server's editor.", definition.Id);
            _logger.LogDebug("Auto-collection {Id} is now named {Name}.", definition.Id, native);
        }

        return adopted ?? definition;
    }

    private static AutoCollectionState Succeeded(
        AutoCollectionState? previous,
        AutoCollectionCheck check,
        DesiredMembership desired,
        string linkHash,
        bool mixed,
        DateTime now,
        SmartSearchAutoCollectionRunReason reason,
        long duration,
        CollectionDiff diff,
        string outcome,
        ManualEditKeeper.Sorted? sorted = null)
    {
        var state = previous ?? new AutoCollectionState();
        var written = outcome != RunOutcome.NoChange;
        var entry = new AutoCollectionHistoryEntry
        {
            Utc = now,
            Reason = reason,
            Added = written ? diff.Added.Count : 0,
            Removed = written ? diff.Removed.Count : 0,
            Dead = written ? diff.Dead + diff.Duplicates : 0,
            Outcome = outcome,
            DurationMs = duration,
            RemovedIds = written ? diff.Removed.Take(AutoCollectionState.MaxRemovedIds).ToList() : [],
            Pinned = sorted?.Pinned ?? 0,
            Excluded = sorted?.Excluded ?? 0,
            Reverted = sorted?.Reverted ?? 0
        };
        return state with
        {
            Status = SmartSearchAutoCollectionStatus.Ok,
            Reason = null,
            LastError = null,
            LastErrorDetail = null,
            LastRunUtc = now,
            LastSuccessUtc = now,
            LastReason = reason,
            LastQueryHash = check.QueryHash,
            LastResultHash = MembershipHash.Of(desired.Ids),
            LastLinkHash = linkHash,
            LastMemberCount = desired.Ids.Count,
            LastCandidateCount = desired.CandidateCount,
            LastAdded = entry.Added,
            LastRemoved = entry.Removed,
            LastDeadLinks = entry.Dead,
            LastDurationMs = duration,
            MixedAudience = mixed,
            CreatePending = false,
            PendingGuard = null,
            PendingEditsOverflow = false,
            History = [entry, .. state.History.Take(AutoCollectionState.MaxHistory - 1)]
        };
    }

    // The run's state replaces the stored one, except for what changed while it ran: the edits made outside the plugin
    // (those the run sorted go, those recorded meanwhile stay, and so does an overflow meanwhile), an approval given
    // meanwhile for the hold the run leaves as it found it, a Keep that cleared that hold, and a preview accepted
    // meanwhile. What the run read is used up: any run's end clears the approval and the accepted preview's removals.
    private Task<AutoCollectionState> SaveStateAsync(Guid definitionId, AutoCollectionState state, AutoCollectionState? previous, IReadOnlyList<AutoCollectionManualEdit>? sortedEdits = null)
        => _state.UpdateAsync(
            definitionId,
            current =>
            {
                var pending = current?.PendingEdits ?? [];
                if (sortedEdits is { Count: > 0 })
                {
                    var done = sortedEdits.ToHashSet();
                    pending = pending.Where(e => !done.Contains(e)).ToList();
                }

                var result = state with
                {
                    PendingEdits = pending,
                    PendingEditsOverflow = state.PendingEditsOverflow || (current?.PendingEditsOverflow == true && previous?.PendingEditsOverflow != true)
                };

                var hold = result.PendingGuard;
                if (current?.PendingGuard is { ApprovedUtc: { } approvedUtc } granted
                    && approvedUtc != previous?.PendingGuard?.ApprovedUtc
                    && hold is not null && hold.DetectedUtc == granted.DetectedUtc)
                {
                    result = result with { PendingGuard = hold with { ApprovedUtc = approvedUtc } };
                }
                else if (previous?.PendingGuard is { } before && current is not null && current.PendingGuard is null
                    && hold is not null && hold.DetectedUtc == before.DetectedUtc)
                {
                    result = result with { PendingGuard = null };
                }

                var accepted = current is not null
                    && (current.AcceptedRemovals != previous?.AcceptedRemovals || !string.Equals(current.AcceptedResultHash, previous?.AcceptedResultHash, StringComparison.Ordinal));
                return result with
                {
                    AcceptedRemovals = accepted ? current!.AcceptedRemovals : null,
                    AcceptedResultHash = accepted ? current!.AcceptedResultHash : null
                };
            },
            CancellationToken.None);

    // How long each step of a run took, for the debug log.
    private sealed class RunPhases(TimeProvider time)
    {
        private readonly List<string> _parts = [];
        private long _last = time.GetTimestamp();

        public void Mark(string phase)
        {
            var now = time.GetTimestamp();
            _parts.Add(phase + " " + time.GetElapsedTime(_last, now).TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture));
            _last = now;
        }

        public override string ToString() => string.Join(", ", _parts);
    }
}
