using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The auto-collection endpoints' work, behind <see cref="ISmartSearchAutoCollections"/>.</summary>
/// <remarks>
/// Saves happen in the request; collection writes are asked of the queue, which runs one at a time. A create waits for
/// its collection up to <see cref="CreateBudget"/>, then answers with the state so far (the run goes on). A request the
/// client drops after the definition was saved leaves the definition and its queued run in place.
/// </remarks>
internal sealed class AutoCollectionService : ISmartSearchAutoCollections
{
    /// <summary>The most samples a preview shows of each side.</summary>
    public const int MaxSamples = 20;

    /// <summary>409: the server has the most auto-collections allowed.</summary>
    public const string TooManyAutoCollectionsCode = "TooManyAutoCollections";

    /// <summary>409: the auto-collection changed since the caller read it.</summary>
    public const string ChangedCode = "AutoCollectionChanged";

    /// <summary>400: a list of pins or excludes is too long.</summary>
    public const string TooManyEntriesCode = "TooManyEntries";

    /// <summary>How long a create waits for its collection before it answers 202.</summary>
    public static readonly TimeSpan CreateBudget = TimeSpan.FromSeconds(60);

    private readonly CollectionNameChecker _names;
    private readonly AutoCollectionStore _store;
    private readonly AutoCollectionStateStore _state;
    private readonly AutoCollectionChecks _checks;
    private readonly AutoCollectionCheckContextSource _context;
    private readonly MembershipPlanner _planner;
    private readonly MixedAudienceCheck _mixedAudience;
    private readonly PageHydrator _hydrator;
    private readonly LibraryAccess _access;
    private readonly DefinitionRunner _runner;
    private readonly IAutoCollectionQueue _queue;
    private readonly CollectionLocator _locator;
    private readonly LinkResolver _links;
    private readonly SavedQueryStore _savedQueries;
    private readonly Func<ISmartSearchSavedQueries> _savedQueryService;
    private readonly PreviewLedger _ledger;
    private readonly TimeProvider _time;
    private readonly MassRemovalGuard _guard;
    private readonly Func<bool> _scanRunning;
    private readonly ManualEditKeeper? _keeper;
    private readonly Func<int> _maxSavedQueriesPerUser;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionService"/> class.
    /// </summary>
    /// <param name="parts">The parts it works with.</param>
    public AutoCollectionService(AutoCollectionServiceParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        _names = parts.Names;
        _store = parts.Store;
        _state = parts.State;
        _checks = parts.Checks;
        _context = parts.Context;
        _planner = parts.Planner;
        _mixedAudience = parts.MixedAudience;
        _hydrator = parts.Hydrator;
        _access = parts.Access;
        _runner = parts.Runner;
        _queue = parts.Queue;
        _locator = parts.Locator;
        _links = parts.Links;
        _savedQueries = parts.SavedQueries;
        _savedQueryService = parts.SavedQueryService;
        _ledger = parts.Ledger;
        _guard = parts.Guard;
        _scanRunning = parts.ScanRunning;
        _keeper = parts.Keeper;
        _time = parts.Time;
        _maxSavedQueriesPerUser = parts.MaxSavedQueriesPerUser;
    }

    /// <inheritdoc />
    public async Task<SmartSearchAutoCollectionNameCheckDto> CheckNameAsync(User caller, bool callerIsAdmin, SmartSearchAutoCollectionCheckNameRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireManager(caller, callerIsAdmin);
        var check = await _names.CheckAsync(request.Name, request.DefinitionId, cancellationToken).ConfigureAwait(false);
        return ToDto(check.ForViewer(CanSee(caller)));
    }

    /// <inheritdoc />
    public async Task<SmartSearchAutoCollectionPreviewDto> PreviewAsync(User caller, bool callerIsAdmin, SmartSearchAutoCollectionPreviewRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireManager(caller, callerIsAdmin);
        var started = _time.GetTimestamp();
        var now = _time.GetUtcNow().UtcDateTime;
        var saved = request.DefinitionId is { } id ? _store.Find(id) ?? throw AutoCollectionErrors.NotFound : null;
        var definition = saved ?? NewDefinition(string.Empty, caller, now);
        if (request.Definition is { } changes)
        {
            definition = Apply(definition, changes, saved, caller, callerIsAdmin, now, ChangesSource(saved, changes, now), _ => null);
        }

        // A shared query of someone else's is previewed as the copy a save would make.
        definition = ForeignReferenceAsSnapshot(definition, caller, callerIsAdmin, now);
        var check = _checks.CheckForSave(definition, _context.Create(), caller, callerIsAdmin, saved?.EvaluationUserId);
        if (!check.Ok)
        {
            throw AutoCollectionErrors.Of(check);
        }

        var cap = _context.Settings.MaxMembersPerCollection;
        var desired = await _planner.PlanAsync(definition, check, EvaluationPurpose.Preview, null, cap, cancellationToken).ConfigureAwait(false);
        if (!desired.Ok)
        {
            throw AutoCollectionErrors.TooManyMembers(desired.Ids.Count, cap);
        }

        var current = saved is null ? [] : CurrentMembers(saved);
        var currentSet = current.ToHashSet();
        var desiredSet = desired.Ids.ToHashSet();
        var adds = desired.Ids.Where(i => !currentSet.Contains(i)).ToList();
        var removes = current.Where(i => !desiredSet.Contains(i)).Distinct().ToList();

        // The samples: only items the caller may open; the rest are counted, never named.
        var callerLibraries = _access.VisibleLibraries(caller);
        var visible = _access.EligibleIds(caller, callerLibraries, adds.Concat(removes).ToList());
        var libraryIds = callerLibraries.Select(l => l.Id).ToList();
        var addSamples = _hydrator.Hydrate(adds.Where(visible.Contains).Take(MaxSamples).ToList(), caller, libraryIds, cancellationToken).Items;
        var removeSamples = _hydrator.Hydrate(removes.Where(visible.Contains).Take(MaxSamples).ToList(), caller, libraryIds, cancellationToken).Items;

        var mixed = _mixedAudience.CountPartialViewers(desired.Ids, cancellationToken, desired.SourceLibraryIds) > 0;
        var warnings = new List<string>();
        if (desired.PinsSkipped > 0)
        {
            warnings.Add(desired.PinsSkipped + " pinned item(s) are left out: they no longer exist, or are outside the libraries the collection's creator can see.");
        }

        if (mixed)
        {
            warnings.Add(AutoCollectionErrors.MixedAudienceMessage);
        }

        var hash = MembershipHash.Of(desired.Ids);
        _ledger.Record(hash, desired.Ids, removes.Count, saved?.Id);
        return new SmartSearchAutoCollectionPreviewDto
        {
            PreviewHash = hash,
            Total = desired.Ids.Count,
            WouldAdd = adds.Count,
            WouldRemove = removes.Count,
            AddSamples = addSamples,
            RemoveSamples = removeSamples,
            HiddenFromCaller = adds.Count(i => !visible.Contains(i)) + removes.Count(i => !visible.Contains(i)),
            PinsSkipped = desired.PinsSkipped,
            MixedAudience = mixed,
            Guard = PreviewGuard(saved, definition, current.Count, removes.Count, desired, check.QueryHash),
            Warnings = warnings,
            ElapsedMs = (long)_time.GetElapsedTime(started).TotalMilliseconds
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SmartSearchAutoCollectionDto>> ListAsync(User? caller, bool callerIsAdmin, string? status, CancellationToken cancellationToken)
    {
        if (caller is not null)
        {
            RequireManager(caller, callerIsAdmin);
        }

        SmartSearchAutoCollectionStatus? wanted = null;
        if (!string.IsNullOrEmpty(status))
        {
            wanted = Enum.TryParse<SmartSearchAutoCollectionStatus>(status, true, out var parsed) && parsed != SmartSearchAutoCollectionStatus.Unknown && !int.TryParse(status, out _)
                ? parsed
                : throw AutoCollectionErrors.Invalid("status", QueryErrorCodes.InvalidValue, "status must be one of the statuses an auto-collection can have.");
        }

        IReadOnlyList<SmartSearchAutoCollectionDto> list = _store.Current.Definitions
            .Where(d => d is not null)
            .Select(d => (Definition: d, State: StateOf(d.Id)))
            .Where(x => wanted is null || (x.State?.Status.Value ?? SmartSearchAutoCollectionStatus.NotCreated) == wanted)
            .OrderBy(x => x.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => ToDto(x.Definition, x.State, caller, callerIsAdmin, details: false))
            .ToList();
        return Task.FromResult(list);
    }

    /// <inheritdoc />
    public Task<SmartSearchAutoCollectionDto> GetAsync(User caller, bool callerIsAdmin, Guid id, CancellationToken cancellationToken)
    {
        RequireManager(caller, callerIsAdmin);
        var definition = _store.Find(id) ?? throw AutoCollectionErrors.NotFound;
        return Task.FromResult(ToDto(definition, StateOf(id), caller, callerIsAdmin, details: true));
    }

    /// <inheritdoc />
    public async Task<SmartSearchAutoCollectionDto> CreateAsync(User caller, bool callerIsAdmin, SmartSearchAutoCollectionRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireManager(caller, callerIsAdmin);
        RequireOn();
        RequireWritable();
        RequireRoom(_store.Current.Definitions.Count);

        var now = _time.GetUtcNow().UtcDateTime;
        var definition = Apply(NewDefinition(string.Empty, caller, now), request.Definition ?? new(), null, caller, callerIsAdmin, now, sourceChanged: true, Capture);

        // Someone else's shared query: checked and evaluated as the copy that is made just before the save.
        var foreign = ForeignReference(definition);
        if (foreign is not null && request.DuplicateSourceQuery != true)
        {
            throw AutoCollectionErrors.NotQueryOwner;
        }

        var evaluated = ForeignReferenceAsSnapshot(definition, caller, callerIsAdmin, now);
        var check = _checks.CheckForSave(evaluated, _context.Create(), caller, callerIsAdmin);
        if (!check.Ok)
        {
            throw AutoCollectionErrors.Of(check);
        }

        await RequireNameAsync(definition, caller, cancellationToken).ConfigureAwait(false);
        var cap = _context.Settings.MaxMembersPerCollection;
        var desired = await _planner.PlanAsync(evaluated, check, EvaluationPurpose.Preview, null, cap, cancellationToken).ConfigureAwait(false);
        if (!desired.Ok)
        {
            throw AutoCollectionErrors.TooManyMembers(desired.Ids.Count, cap);
        }

        if (!_ledger.Matches(request.AcceptedPreviewHash, desired.Ids, null))
        {
            throw AutoCollectionErrors.PreviewStale;
        }

        if (foreign is { } original)
        {
            var copy = await _savedQueryService().DuplicateAsync(caller, callerIsAdmin, original, new SmartSearchSavedQueryDuplicateDto(), cancellationToken).ConfigureAwait(false);
            definition = definition with { Source = definition.Source with { SavedQueryId = copy.Id } };
        }

        await _store.UpdateAsync(
            d =>
            {
                RequireRoom(d.Definitions.Count);
                RequireFollowable(definition);
                return AutoCollectionStore.WithDefinitions(d, [.. d.Definitions, definition]);
            },
            cancellationToken).ConfigureAwait(false);

        var run = _queue.RequestAsync(new AutoCollectionRequest(definition.Id, SmartSearchAutoCollectionRunReason.Saved, AutoCollectionRequestKind.CreateNew));
        await WaitForCreateAsync(run, cancellationToken).ConfigureAwait(false);
        var stored = _store.Find(definition.Id) ?? throw AutoCollectionErrors.NotFound;
        return ToDto(stored, StateOf(definition.Id), caller, callerIsAdmin, details: true);
    }

    /// <inheritdoc />
    public async Task<SmartSearchAutoCollectionDto> UpdateAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchAutoCollectionRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireManager(caller, callerIsAdmin);
        RequireWritable();
        var saved = _store.Find(id) ?? throw AutoCollectionErrors.NotFound;
        RequireUnchanged(saved, request.IfUpdatedAt);

        var now = _time.GetUtcNow().UtcDateTime;
        var sourceChanged = ChangesSource(saved, request.Definition, now);
        var definition = Apply(saved, request.Definition ?? new(), saved, caller, callerIsAdmin, now, sourceChanged, Capture);
        if (sourceChanged && ForeignReference(definition) is { } original)
        {
            if (request.DuplicateSourceQuery != true)
            {
                throw AutoCollectionErrors.NotQueryOwner;
            }

            var evaluated = ForeignReferenceAsSnapshot(definition, caller, callerIsAdmin, now);
            var precheck = _checks.CheckForSave(evaluated, _context.Create(), caller, callerIsAdmin, saved.EvaluationUserId);
            if (!precheck.Ok)
            {
                throw AutoCollectionErrors.Of(precheck);
            }

            var copy = await _savedQueryService().DuplicateAsync(caller, callerIsAdmin, original, new SmartSearchSavedQueryDuplicateDto(), cancellationToken).ConfigureAwait(false);
            definition = definition with { Source = definition.Source with { SavedQueryId = copy.Id } };
        }

        // Every change moves UpdatedUtc forward, so it tells two versions apart.
        definition = definition with { UpdatedByUserId = caller.Id, UpdatedUtc = now > saved.UpdatedUtc ? now : saved.UpdatedUtc.AddTicks(1) };
        var check = _checks.CheckForSave(definition, _context.Create(), caller, callerIsAdmin, saved.EvaluationUserId);
        if (!check.Ok)
        {
            throw AutoCollectionErrors.Of(check);
        }

        if (!string.Equals(definition.Name, saved.Name, StringComparison.Ordinal))
        {
            await RequireNameAsync(definition, caller, cancellationToken).ConfigureAwait(false);
        }

        // Saved under the store's lock, against the version the caller read.
        await _store.UpdateAsync(
            d =>
            {
                var current = d.Definitions.FirstOrDefault(x => x.Id.Equals(id)) ?? throw AutoCollectionErrors.NotFound;
                RequireUnchanged(current, request.IfUpdatedAt);
                if (sourceChanged)
                {
                    RequireFollowable(definition);
                }

                return AutoCollectionStore.WithDefinitions(d, d.Definitions.Select(x => x.Id.Equals(id) ? definition with { CollectionId = current.CollectionId } : x));
            },
            cancellationToken).ConfigureAwait(false);

        // The accepted preview (of this definition) lets the next run remove about as many without a review, when that
        // run finds the same members. A save is also a manager's answer to edits that overflowed: the runs go on.
        var accepted = _ledger.Find(request.AcceptedPreviewHash, id);
        if (accepted is not null || _state.Find(id)?.PendingEditsOverflow == true)
        {
            await _state.UpdateAsync(
                id,
                s => (s ?? new AutoCollectionState()) with
                {
                    AcceptedRemovals = accepted is { } preview ? preview.WouldRemove : s?.AcceptedRemovals,
                    AcceptedResultHash = accepted is not null ? request.AcceptedPreviewHash : s?.AcceptedResultHash,
                    PendingEditsOverflow = false
                },
                CancellationToken.None).ConfigureAwait(false);
        }

        var updated = _store.Find(id) ?? definition;
        if (updated.CollectionId is not null && updated.Enabled && _context.Settings.Enabled)
        {
            Observe(_queue.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.Saved)));
        }

        return ToDto(_store.Find(id) ?? updated, StateOf(id), caller, callerIsAdmin, details: true);
    }

    /// <inheritdoc />
    public Task<SmartSearchAutoCollectionDto> RefreshAsync(User? caller, bool callerIsAdmin, Guid id, CancellationToken cancellationToken)
    {
        if (caller is not null)
        {
            RequireManager(caller, callerIsAdmin);
        }

        RequireOn();
        var definition = _store.Find(id) ?? throw AutoCollectionErrors.NotFound;
        if (!definition.Enabled)
        {
            throw AutoCollectionErrors.Disabled;
        }

        if (definition.FromNewerVersion() is not null || _store.IsReadOnly)
        {
            throw new PluginException(PluginErrorKind.Conflict, "This auto-collection was saved by a newer version of Smart Search; it isn't updated here.") { Code = AutoCollectionReasons.NewerDefinition };
        }

        Observe(_queue.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.Manual)));
        return Task.FromResult(ToDto(definition, StateOf(id), caller, callerIsAdmin, details: false));
    }

    /// <inheritdoc />
    public Task RefreshAllAsync(User? caller, bool callerIsAdmin, CancellationToken cancellationToken)
    {
        if (caller is not null)
        {
            RequireManager(caller, callerIsAdmin);
        }

        RequireOn();
        Observe(_queue.RequestPassAsync(SmartSearchAutoCollectionRunReason.Manual));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<SmartSearchAutoCollectionDto> RecreateAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchAutoCollectionRecreateRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireManager(caller, callerIsAdmin);
        RequireOn();
        RequireWritable();
        var definition = _store.Find(id) ?? throw AutoCollectionErrors.NotFound;
        var location = _locator.Locate(definition);
        if (location.Kind is not (CollectionLocationKind.Orphaned or CollectionLocationKind.NotCreated or CollectionLocationKind.TetherMissing))
        {
            throw AutoCollectionErrors.NotRecreatable;
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? definition.Name : request.Name.Trim();
        var renames = !string.Equals(name, definition.Name, StringComparison.Ordinal);
        if (renames && !callerIsAdmin && !definition.CreatedByUserId.Equals(caller.Id))
        {
            throw AutoCollectionErrors.CreatorOnly;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await RequireNameAsync(definition with { Name = name }, caller, cancellationToken).ConfigureAwait(false);

        // A collection at the stored id that isn't ours any more stays the user's: the new one gets its own id.
        await _store.UpdateDefinitionAsync(
            id,
            d => d with
            {
                Name = name,
                CollectionId = location.Kind == CollectionLocationKind.TetherMissing ? null : d.CollectionId,
                UpdatedByUserId = renames ? caller.Id : d.UpdatedByUserId,
                UpdatedUtc = !renames ? d.UpdatedUtc : (now > d.UpdatedUtc ? now : d.UpdatedUtc.AddTicks(1))
            },
            cancellationToken).ConfigureAwait(false);

        var run = _queue.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.Manual, AutoCollectionRequestKind.Create));
        await WaitForCreateAsync(run, cancellationToken).ConfigureAwait(false);
        return ToDto(_store.Find(id) ?? throw AutoCollectionErrors.NotFound, StateOf(id), caller, callerIsAdmin, details: true);
    }

    /// <inheritdoc />
    public async Task<SmartSearchAutoCollectionDto> ApplyGuardAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchAutoCollectionGuardRequestDto request, CancellationToken cancellationToken)
    {
        var (definition, _) = RequireHeld(caller, callerIsAdmin, id, request);
        var now = _time.GetUtcNow().UtcDateTime;
        AutoCollectionState? changed = null;
        var stale = false;
        await _state.UpdateAsync(
            id,
            s =>
            {
                // Read again under the store's lock: a run may have cleared or replaced them meanwhile.
                if (s?.PendingGuard is not { } held || held.DetectedUtc != request.DetectedUtc)
                {
                    stale = s?.PendingGuard is not null;
                    return s!;
                }

                changed = s with { PendingGuard = held with { ApprovedUtc = now } };
                return changed;
            },
            CancellationToken.None).ConfigureAwait(false);
        if (changed is null)
        {
            throw stale ? AutoCollectionErrors.HoldChanged : AutoCollectionErrors.GuardNotPending;
        }

        Observe(_queue.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.Manual)));
        return ToDto(definition, StateOf(id), caller, callerIsAdmin, details: true);
    }

    /// <inheritdoc />
    public async Task KeepGuardAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchAutoCollectionGuardRequestDto request, CancellationToken cancellationToken)
    {
        var (_, held) = RequireHeld(caller, callerIsAdmin, id, request);
        var now = _time.GetUtcNow().UtcDateTime;

        // The held members become pins (they stay whatever the query says); dead links can't be pinned and go with the
        // next run. All of them or none: a hold with more than can be pinned stays, with nothing changed.
        await _store.UpdateDefinitionAsync(
            id,
            d =>
            {
                var pinned = (d.ManualEdits.Pinned ?? []).Select(e => e.ItemId).Concat(held.WouldRemove ?? []).Distinct().Count();
                if (!held.Complete || pinned > AutoCollectionManualEdits.MaxEntries)
                {
                    throw AutoCollectionErrors.TooManyToKeep(held.HeldMembers, AutoCollectionManualEdits.MaxEntries);
                }

                return d with
                {
                    ManualEdits = d.ManualEdits with
                    {
                        Pinned = Entries((d.ManualEdits.Pinned ?? []).Select(e => e.ItemId).Concat(held.WouldRemove ?? []), d.ManualEdits.Pinned, now, "ManualEdits.Pinned", Capture)
                    },
                    UpdatedByUserId = caller.Id,
                    UpdatedUtc = now > d.UpdatedUtc ? now : d.UpdatedUtc.AddTicks(1)
                };
            },
            cancellationToken).ConfigureAwait(false);
        await _state.UpdateAsync(id, s => s?.PendingGuard is { } current && current.DetectedUtc == held.DetectedUtc ? s with { PendingGuard = null } : s!, CancellationToken.None).ConfigureAwait(false);
        Observe(_queue.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.Manual)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SmartSearchAutoCollectionHistoryDto>> GetHistoryAsync(User caller, bool callerIsAdmin, Guid id, CancellationToken cancellationToken)
    {
        RequireManager(caller, callerIsAdmin);
        _ = _store.Find(id) ?? throw AutoCollectionErrors.NotFound;
        IReadOnlyList<SmartSearchAutoCollectionHistoryDto> list = (_state.Find(id)?.History ?? [])
            .Where(e => e is not null)
            .Select(e =>
            {
                var removed = e.RemovedIds ?? [];
                IReadOnlySet<Guid> visible = removed.Count == 0 ? new HashSet<Guid>() : _access.EligibleIds(caller, _access.VisibleLibraries(caller), removed.ToList());
                var shown = removed.Where(visible.Contains).ToList();
                return new SmartSearchAutoCollectionHistoryDto
                {
                    Utc = e.Utc,
                    Reason = e.Reason.ToString(),
                    Added = e.Added,
                    Removed = e.Removed,
                    Dead = e.Dead,
                    Pinned = e.Pinned,
                    Excluded = e.Excluded,
                    Reverted = e.Reverted,
                    Outcome = e.Outcome,
                    DurationMs = e.DurationMs,
                    RemovedIds = shown,
                    HiddenCount = removed.Count - shown.Count
                };
            })
            .ToList();
        return Task.FromResult(list);
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetCollectionIds(User caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!_context.Settings.Enabled)
        {
            return [];
        }

        return _store.Current.Definitions
            .Where(d => d.CollectionId is { } id && VisibleTethered(caller, id, d.Id) is not null)
            .Select(d => d.CollectionId!.Value)
            .Distinct()
            .ToList();
    }

    /// <inheritdoc />
    public SmartSearchAutoCollectionNoteDto GetNote(User caller, bool callerIsAdmin, Guid boxSetId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!_context.Settings.Enabled)
        {
            throw AutoCollectionErrors.NotFound;
        }

        var definition = _store.Current.Definitions
            .Where(d => d.CollectionId is { } id && id.Equals(boxSetId))
            .FirstOrDefault(d => VisibleTethered(caller, boxSetId, d.Id) is not null)
            ?? throw AutoCollectionErrors.NotFound;
        var manages = callerIsAdmin || CurrentUser.HasAny(caller, PermissionKind.EnableCollectionManagement);
        return new SmartSearchAutoCollectionNoteDto
        {
            Name = definition.Name,
            LastSuccessUtc = _state.Find(definition.Id)?.LastSuccessUtc,
            CanManage = manages,
            DefinitionId = manages ? definition.Id : null
        };
    }

    /// <inheritdoc />
    public async Task<SmartSearchAutoCollectionDto> DetachAsync(User caller, bool callerIsAdmin, Guid id, CancellationToken cancellationToken)
    {
        RequireManager(caller, callerIsAdmin);
        RequireWritable();
        var definition = _store.Find(id) ?? throw AutoCollectionErrors.NotFound;
        if (!callerIsAdmin && !definition.CreatedByUserId.Equals(caller.Id))
        {
            throw AutoCollectionErrors.NotCreator;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await _store.UpdateDefinitionAsync(
            id,
            d =>
            {
                if (d.Source.Mode.Value != SmartSearchAutoCollectionSourceMode.Reference)
                {
                    throw AutoCollectionErrors.AlreadySnapshot;
                }

                var record = d.Source.SavedQueryId is { } queryId ? _savedQueries.Find(queryId) : null;
                if (record is null || record.Query.ValueKind != JsonValueKind.Object)
                {
                    throw AutoCollectionErrors.QueryMissing;
                }

                // Only the creator's own query is copied: someone else's (after a forced owner change, a remap refused
                // part-way, an import) stays theirs, and the definition stays stopped until it follows another.
                if (!AutoCollectionStore.Follows(d, record))
                {
                    throw AutoCollectionErrors.SourceOwnerChanged;
                }

                return DefinitionReferenceGuard.Detached(d, record.Query, caller.Id, now);
            },
            cancellationToken).ConfigureAwait(false);
        return ToDto(_store.Find(id) ?? throw AutoCollectionErrors.NotFound, StateOf(id), caller, callerIsAdmin, details: true);
    }

    /// <inheritdoc />
    public async Task<SmartSearchAutoCollectionDto> ReassignAsync(User caller, bool callerIsAdmin, Guid id, SmartSearchSavedQueryOwnerDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireManager(caller, callerIsAdmin);
        if (!callerIsAdmin)
        {
            throw AutoCollectionErrors.AdministratorRequired;
        }

        RequireWritable();
        _ = _store.Find(id) ?? throw AutoCollectionErrors.NotFound;
        var user = _context.FindUser(request.UserId)
            ?? throw AutoCollectionErrors.Invalid(nameof(request.UserId), SavedQueryErrors.UnknownUserCode, "No user of this server has this id.");
        if (!CurrentUser.IsAdmin(user) && !CurrentUser.HasAny(user, PermissionKind.EnableCollectionManagement))
        {
            throw AutoCollectionErrors.Invalid(nameof(request.UserId), AutoCollectionErrors.ManagerRequiredCode, "Only an administrator or a user allowed to manage collections can be given an auto-collection.");
        }

        // The definitions' gate, then the saved queries' lock (the lock order); never a run's gate meanwhile.
        var now = _time.GetUtcNow().UtcDateTime;
        List<Guid> moved;
        using (var held = await _store.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            var definition = _store.Find(id) ?? throw AutoCollectionErrors.NotFound;
            var creator = definition.CreatedByUserId;
            if (_context.FindUser(creator) is not null)
            {
                throw AutoCollectionErrors.CreatorNotMissing;
            }

            // The saved query moves with every definition of the same creator that follows it (a followed query is always
            // its creator's), so none of them is left following someone else's query.
            var queryId = definition.Source.Mode.Value == SmartSearchAutoCollectionSourceMode.Reference ? definition.Source.SavedQueryId : null;
            var record = queryId is { } qid && _savedQueries.Find(qid) is { } found && found.OwnerUserId.Equals(creator) ? found : null;
            moved = _store.Current.Definitions
                .Where(d => d.Id.Equals(id) || (record is not null && d.CreatedByUserId.Equals(creator) && d.Source.Mode.Value == SmartSearchAutoCollectionSourceMode.Reference && Nullable.Equals(d.Source.SavedQueryId, queryId)))
                .Select(d => d.Id)
                .ToList();

            // Refused before anything is written when the new creator couldn't run it (an administrator-only field), or
            // couldn't own one more saved query (MaxSavedQueriesPerUser, as an owner change refuses it).
            if (record is not null && _savedQueries.Current.Queries.Count(q => q.OwnerUserId.Equals(user.Id)) >= _maxSavedQueriesPerUser())
            {
                throw SavedQueryErrors.QuotaFor(_maxSavedQueriesPerUser());
            }

            var context = _context.Create();
            var given = record is null ? null : record with { OwnerUserId = user.Id };
            var after = given is null ? context.SavedQueries : SavedQueryStore.WithQueries(context.SavedQueries, context.SavedQueries.Queries.Select(q => q.Id.Equals(given.Id) ? given : q));
            var check = _checks.CheckForRun(definition with { CreatedByUserId = user.Id }, context with { SavedQueries = after });
            if (check.Reason == AutoCollectionReasons.AdminOnlyField)
            {
                throw new PluginException(PluginErrorKind.Conflict, "The query uses an administrator-only field, and only an administrator may own it.") { Code = QueryErrorCodes.AdminOnlyField };
            }

            if (record is not null)
            {
                await _savedQueries.UpdateAsync(
                    document =>
                    {
                        var stored = document.Queries.FirstOrDefault(q => q.Id.Equals(record.Id));
                        if (stored is null || !stored.OwnerUserId.Equals(creator))
                        {
                            return document;
                        }

                        var owned = document.Queries.Where(q => q.OwnerUserId.Equals(user.Id)).ToList();
                        if (owned.Count >= _maxSavedQueriesPerUser())
                        {
                            throw SavedQueryErrors.QuotaFor(_maxSavedQueriesPerUser());
                        }

                        var taken = owned.Select(q => q.Name).ToHashSet(SavedQueryNames.Comparer);
                        var moving = stored with
                        {
                            OwnerUserId = user.Id,
                            Name = SavedQueryNames.Free(stored.Name, taken),
                            UpdatedUtc = now > stored.UpdatedUtc ? now : stored.UpdatedUtc.AddTicks(1)
                        };
                        return SavedQueryStore.WithQueries(document, document.Queries.Select(q => q.Id.Equals(stored.Id) ? moving : q)) with
                        {
                            KnownUsers = (document.KnownUsers ?? KnownUsers.Empty).With(user)
                        };
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            var set = moved.ToHashSet();
            await _store.UpdateHeldAsync(
                held,
                d => AutoCollectionStore.WithDefinitions(d, d.Definitions.Select(x => set.Contains(x.Id)
                    ? x with { CreatedByUserId = user.Id, UpdatedByUserId = caller.Id, UpdatedUtc = now > x.UpdatedUtc ? now : x.UpdatedUtc.AddTicks(1) }
                    : x)) with { KnownUsers = (d.KnownUsers ?? KnownUsers.Empty).With(user) },
                cancellationToken).ConfigureAwait(false);
        }

        if (_context.Settings.Enabled)
        {
            foreach (var definitionId in moved)
            {
                if (_store.Find(definitionId) is { Enabled: true, CollectionId: not null })
                {
                    Observe(_queue.RequestAsync(new AutoCollectionRequest(definitionId, SmartSearchAutoCollectionRunReason.Saved)));
                }
            }
        }

        return ToDto(_store.Find(id) ?? throw AutoCollectionErrors.NotFound, StateOf(id), caller, callerIsAdmin, details: true);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(User caller, bool callerIsAdmin, Guid id, bool deleteCollection, CancellationToken cancellationToken)
    {
        RequireManager(caller, callerIsAdmin);
        return await _runner.DeleteAsync(id, deleteCollection ? caller : null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refuses a caller who doesn't manage collections: neither an administrator nor allowed to manage collections.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="callerIsAdmin">Whether the request passed the server's administrator policy.</param>
    /// <exception cref="PluginException">403 <c>ManagerRequired</c>.</exception>
    internal static void RequireManager(User caller, bool callerIsAdmin)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!callerIsAdmin && !CurrentUser.HasAny(caller, PermissionKind.EnableCollectionManagement))
        {
            throw AutoCollectionErrors.ManagerRequired;
        }
    }

    /// <summary>Maps a name check to its DTO; a name that isn't one is refused.</summary>
    /// <param name="check">The check.</param>
    /// <returns>The DTO.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>.</exception>
    internal static SmartSearchAutoCollectionNameCheckDto ToDto(CollectionNameCheck check)
    {
        if (check.Error is not null)
        {
            throw AutoCollectionErrors.Invalid(nameof(SmartSearchAutoCollectionCheckNameRequestDto.Name), QueryErrorCodes.InvalidValue, check.Error);
        }

        return new SmartSearchAutoCollectionNameCheckDto
        {
            Name = check.Name,
            Available = check.Available,
            Conflicts = check.Conflicts.Select(c => new SmartSearchAutoCollectionNameConflictDto
            {
                Kind = c.Kind.ToString(),
                CollectionId = c.Kind == CollectionNameConflictKind.AutoCollection ? null : c.Id,
                DefinitionId = c.Kind == CollectionNameConflictKind.AutoCollection ? c.Id : null,
                Name = c.Name
            }).ToList(),
            Warnings = check.Warnings,
            Suggestions = check.Suggestions
        };
    }

    /// <summary>Maps a definition and its state to the DTO.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="state">Its run state, or <c>null</c> before its first run.</param>
    /// <param name="caller">The caller, or <c>null</c> for an API key (no query, no names).</param>
    /// <param name="callerIsAdmin">Whether the caller is an administrator.</param>
    /// <param name="details">Whether to add the manual edits.</param>
    /// <returns>The DTO.</returns>
    internal SmartSearchAutoCollectionDto ToDto(AutoCollectionDefinition definition, AutoCollectionState? state, User? caller, bool callerIsAdmin, bool details)
    {
        ArgumentNullException.ThrowIfNull(definition);
        state ??= new AutoCollectionState();
        var presentation = definition.Presentation ?? new AutoCollectionPresentation();
        var edits = definition.ManualEdits ?? new AutoCollectionManualEdits();
        var safety = definition.Safety ?? new AutoCollectionSafety();
        SmartSearchAutoCollectionManualEditsDto? editsDto = null;
        if (details)
        {
            editsDto = new SmartSearchAutoCollectionManualEditsDto
            {
                Mode = edits.Mode.ToString(),
                Pinned = Items(edits.Pinned, caller),
                Excluded = Items(edits.Excluded, caller)
            };
        }

        return new SmartSearchAutoCollectionDto
        {
            Id = definition.Id,
            Name = definition.Name,
            CollectionId = definition.CollectionId,
            Enabled = definition.Enabled,
            Source = SourceDto(definition, caller, callerIsAdmin),
            EvaluationUserId = definition.EvaluationUserId,
            EvaluationUserName = caller is not null && definition.EvaluationUserId is { } evaluationId ? _context.FindUser(evaluationId)?.Username : null,
            Presentation = new SmartSearchAutoCollectionPresentationDto
            {
                SortMode = presentation.SortMode.ToString(),
                Lock = presentation.Lock,
                OverviewMode = presentation.OverviewMode.ToString(),
                Overview = presentation.Overview
            },
            ManualEdits = editsDto,
            Safety = new SmartSearchAutoCollectionSafetyDto { AllowEmpty = safety.AllowEmpty, MaxRemovalPercent = safety.MaxRemovalPercent },
            CreatedByUserId = definition.CreatedByUserId,
            CreatedByName = caller is null ? null : _context.FindUser(definition.CreatedByUserId)?.Username,
            CreatedUtc = definition.CreatedUtc,
            UpdatedByUserId = definition.UpdatedByUserId,
            UpdatedUtc = definition.UpdatedUtc,
            State = new SmartSearchAutoCollectionStateDto
            {
                Status = state.Status.ToString(),
                Reason = state.Reason,
                LastRunUtc = state.LastRunUtc,
                LastSuccessUtc = state.LastSuccessUtc,
                LastReason = state.LastReason?.ToString(),
                LastMemberCount = state.LastMemberCount,
                LastCandidateCount = state.LastCandidateCount,
                LastAdded = state.LastAdded,
                LastRemoved = state.LastRemoved,
                LastDeadLinks = state.LastDeadLinks,
                UnprunableDeadLinks = state.UnprunableDeadLinks,
                LastDurationMs = state.LastDurationMs,
                PendingGuard = state.PendingGuard is { } guard ? GuardDto(guard, caller, callerIsAdmin) : null,
                MixedAudience = state.MixedAudience,
                LastError = callerIsAdmin && caller is not null ? state.LastErrorDetail ?? state.LastError : state.LastError
            }
        };
    }

    private static AutoCollectionDefinition NewDefinition(string name, User caller, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        CreatedByUserId = caller.Id,
        CreatedUtc = now,
        UpdatedByUserId = caller.Id,
        UpdatedUtc = now
    };

    // The caller sends back the UpdatedUtc it read (to the tick, as it was given); anything else means someone changed it since.
    private static void RequireUnchanged(AutoCollectionDefinition current, DateTime? ifUpdatedAt)
    {
        if (ifUpdatedAt is not { } expected || current.UpdatedUtc.Ticks != expected.Ticks)
        {
            throw new PluginException(PluginErrorKind.Conflict, "The auto-collection changed since it was read. Read it again, then make the change.") { Code = ChangedCode };
        }
    }

    // The changes a request makes, with who may make them: the creator or an administrator changes the name, the source,
    // the evaluation user and whether it is on (an administrator changing someone else's source only to a copy); any
    // manager changes the look, the safeguards and the manual edits. Parts left out keep their values, and so does a
    // source sent back the same (sourceChanged false): it keeps its copy and when it was taken.
    private static AutoCollectionDefinition Apply(AutoCollectionDefinition definition, SmartSearchAutoCollectionDefinitionDto changes, AutoCollectionDefinition? saved, User caller, bool callerIsAdmin, DateTime now, bool sourceChanged, Func<Guid, IdentitySnapshot?> capture)
    {
        var isCreator = saved is null || saved.CreatedByUserId.Equals(caller.Id);
        var result = definition;
        if (changes.Name is { } name)
        {
            result = result with { Name = name.Trim() };
        }

        if (changes.Enabled is { } enabled)
        {
            result = result with { Enabled = enabled };
        }

        if (changes.HasEvaluationUserId)
        {
            result = result with { EvaluationUserId = changes.EvaluationUserId };
        }

        if (changes.Source is { } source && sourceChanged)
        {
            result = result with { Source = Source(source, now) };
        }

        if (saved is not null && !isCreator)
        {
            if (changes.Source is not null && sourceChanged)
            {
                if (!callerIsAdmin)
                {
                    throw AutoCollectionErrors.NotCreator;
                }

                if (result.Source.Mode.Value != SmartSearchAutoCollectionSourceMode.Snapshot)
                {
                    throw AutoCollectionErrors.SnapshotOnly;
                }
            }

            var creatorOnly = !string.Equals(result.Name, saved.Name, StringComparison.Ordinal)
                || result.Enabled != saved.Enabled
                || !Nullable.Equals(result.EvaluationUserId, saved.EvaluationUserId);
            if (creatorOnly && !callerIsAdmin)
            {
                throw AutoCollectionErrors.CreatorOnly;
            }
        }

        if (changes.Presentation is { } presentation)
        {
            var current = result.Presentation;
            result = result with
            {
                Presentation = current with
                {
                    SortMode = presentation.SortMode is { } sort ? Enum.Parse<SmartSearchSortMode>(sort, true) : current.SortMode,
                    Lock = presentation.Lock ?? current.Lock,
                    OverviewMode = presentation.OverviewMode is { } mode ? Enum.Parse<SmartSearchOverviewMode>(mode, true) : current.OverviewMode,
                    Overview = presentation.Overview ?? current.Overview
                }
            };
        }

        if (changes.ManualEdits is { } edits)
        {
            var current = result.ManualEdits;
            result = result with
            {
                ManualEdits = current with
                {
                    Mode = edits.Mode is { } mode ? Enum.Parse<SmartSearchManualEditsMode>(mode, true) : current.Mode,
                    Pinned = edits.PinnedItemIds is { } pinned ? Entries(pinned, current.Pinned, now, "Definition.ManualEdits.PinnedItemIds", capture) : current.Pinned,
                    Excluded = edits.ExcludedItemIds is { } excluded ? Entries(excluded, current.Excluded, now, "Definition.ManualEdits.ExcludedItemIds", capture) : current.Excluded
                }
            };
        }

        if (changes.Safety is { } safety)
        {
            var current = result.Safety;
            result = result with
            {
                Safety = current with
                {
                    AllowEmpty = safety.AllowEmpty ?? current.AllowEmpty,
                    MaxRemovalPercent = safety.HasMaxRemovalPercent ? safety.MaxRemovalPercent : current.MaxRemovalPercent
                }
            };
        }

        return result;
    }

    private static AutoCollectionSource Source(SmartSearchAutoCollectionSourceDto source, DateTime now)
    {
        if (string.Equals(source.Mode, nameof(SmartSearchAutoCollectionSourceMode.Reference), StringComparison.OrdinalIgnoreCase))
        {
            return new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Reference, SavedQueryId = source.SavedQueryId };
        }

        var read = source.Read ?? throw AutoCollectionErrors.Invalid("Definition.Source.Query", QueryErrorCodes.Required, "A Snapshot source needs a query.");
        if (read.Definition is null)
        {
            throw SmartSearchService.QueryErrors(read.Errors);
        }

        using var document = JsonDocument.Parse(QueryJson.Write(read.Definition));
        return new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Snapshot, Snapshot = document.RootElement.Clone(), SnapshotTakenUtc = now };
    }

    // An entry already there keeps when it was added; a new one records what its item is, to find it again after a move.
    private static List<AutoCollectionItemEntry> Entries(IEnumerable<Guid> ids, IReadOnlyList<AutoCollectionItemEntry>? kept, DateTime now, string path, Func<Guid, IdentitySnapshot?> capture)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count > AutoCollectionManualEdits.MaxEntries)
        {
            var message = "At most " + AutoCollectionManualEdits.MaxEntries + " items can be pinned, and as many excluded.";
            throw new PluginException(PluginErrorKind.Invalid, message)
            {
                Code = TooManyEntriesCode,
                Extensions = new Dictionary<string, object?> { ["errors"] = new List<ProblemError> { new(path, TooManyEntriesCode, message) } }
            };
        }

        var old = (kept ?? []).Where(e => e is not null).GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.First());
        return distinct.Select(i => old.TryGetValue(i, out var entry) ? entry : new AutoCollectionItemEntry { ItemId = i, Identity = capture(i), AddedUtc = now }).ToList();
    }

    private static void Observe(Task task)
        => task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // Checked under the definitions' gate, which a saved query's delete and owner change hold too: the saved query a
    // definition follows is still there and still its creator's, so nothing starts following a query being deleted.
    private void RequireFollowable(AutoCollectionDefinition definition)
    {
        if (definition.Source.Mode.Value != SmartSearchAutoCollectionSourceMode.Reference || definition.Source.SavedQueryId is not { } id)
        {
            return;
        }

        var record = _savedQueries.Find(id) ?? throw AutoCollectionErrors.SourceQueryGone;
        if (!record.OwnerUserId.Equals(definition.CreatedByUserId))
        {
            throw AutoCollectionErrors.NotQueryOwner;
        }
    }

    private void RequireRoom(int count)
    {
        var max = _context.Settings.MaxAutoCollections;
        if (count >= max)
        {
            throw new PluginException(PluginErrorKind.Conflict, "This server has the most auto-collections allowed (" + max + "). Delete one first.") { Code = TooManyAutoCollectionsCode };
        }
    }

    // An auto-collection whose removals are held, as the caller read them (the same DetectedUtc).
    private (AutoCollectionDefinition Definition, AutoCollectionPendingGuard Held) RequireHeld(User caller, bool callerIsAdmin, Guid id, SmartSearchAutoCollectionGuardRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireManager(caller, callerIsAdmin);
        RequireOn();
        RequireWritable();
        var definition = _store.Find(id) ?? throw AutoCollectionErrors.NotFound;
        if (definition.FromNewerVersion() is not null)
        {
            throw new PluginException(PluginErrorKind.Conflict, "This auto-collection was saved by a newer version of Smart Search; it isn't updated here.") { Code = AutoCollectionReasons.NewerDefinition };
        }

        if (_state.Find(id)?.PendingGuard is not { } held)
        {
            throw AutoCollectionErrors.GuardNotPending;
        }

        // Another version of the hold (its removals changed since the caller read them): read and review it again.
        if (held.DetectedUtc != request.DetectedUtc)
        {
            throw AutoCollectionErrors.HoldChanged;
        }

        return (definition, held);
    }

    // The stored state, with what the worker is doing now (Queued, Running) over its status.
    private AutoCollectionState? StateOf(Guid id)
    {
        var state = _state.Find(id);
        return _queue.ActivityOf(id) is { } activity ? (state ?? new AutoCollectionState()) with { Status = activity.Status, Reason = activity.Reason } : state;
    }

    // A create waits for its collection up to the budget, then answers with the state so far; its refusal (a name taken
    // meanwhile) is the request's. The client leaving doesn't stop the run.
    private async Task WaitForCreateAsync(Task<AutoCollectionState> run, CancellationToken cancellationToken)
    {
        Observe(run);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var done = await Task.WhenAny(run, Task.Delay(CreateBudget, _time, budget.Token)).ConfigureAwait(false);
        await budget.CancelAsync().ConfigureAwait(false);
        if (done == run)
        {
            await run.ConfigureAwait(false);
        }
    }

    // Whether a request's source differs from the stored one (the same saved query, or a copy of an equal query, is none).
    private bool ChangesSource(AutoCollectionDefinition? saved, SmartSearchAutoCollectionDefinitionDto? changes, DateTime now)
        => changes?.Source is { } asked && (saved is null || !_checks.SameSource(saved.Source, Source(asked, now)));

    // The saved query a Reference names, when it isn't the creator's own: the one a save copies first.
    private Guid? ForeignReference(AutoCollectionDefinition definition)
        => definition.Source.Mode.Value == SmartSearchAutoCollectionSourceMode.Reference
            && definition.Source.SavedQueryId is { } id
            && _savedQueries.Find(id) is { } record
            && !record.OwnerUserId.Equals(definition.CreatedByUserId)
                ? id
                : null;

    // Someone else's saved query, read as a copy when the caller may read it (theirs, shared, or an administrator).
    private AutoCollectionDefinition ForeignReferenceAsSnapshot(AutoCollectionDefinition definition, User caller, bool callerIsAdmin, DateTime now)
    {
        if (ForeignReference(definition) is not { } id)
        {
            return definition;
        }

        var record = _savedQueries.Find(id)!;
        if (!callerIsAdmin && !record.Shared && !record.OwnerUserId.Equals(caller.Id))
        {
            throw AutoCollectionErrors.NotQueryOwner;
        }

        return definition with { Source = new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Snapshot, Snapshot = record.Query.Clone(), SnapshotTakenUtc = now } };
    }

    private SmartSearchAutoCollectionSourceDto SourceDto(AutoCollectionDefinition definition, User? caller, bool callerIsAdmin)
    {
        var source = definition.Source ?? new AutoCollectionSource();
        var dto = new SmartSearchAutoCollectionSourceDto
        {
            Mode = source.Mode.ToString(),
            SavedQueryId = source.SavedQueryId,
            SnapshotTakenUtc = source.SnapshotTakenUtc,
            QueryVisibleToCaller = false
        };

        // A copy of a query is the creator's: other managers see that it is one, not what it holds (it may be a copy of
        // a private query of the creator's).
        JsonElement? json = caller is not null && (callerIsAdmin || definition.CreatedByUserId.Equals(caller.Id)) ? source.Snapshot : null;
        if (source.Mode.Value == SmartSearchAutoCollectionSourceMode.Reference)
        {
            var record = source.SavedQueryId is { } id ? _savedQueries.Find(id) : null;
            var visible = record is not null && caller is not null && (callerIsAdmin || record.Shared || record.OwnerUserId.Equals(caller.Id));
            dto.QueryName = visible ? record!.Name : null;
            json = visible ? record!.Query : null;
        }

        if (json is { ValueKind: JsonValueKind.Object } query && QueryJson.Read(System.Text.Encoding.UTF8.GetBytes(query.GetRawText())).Definition is { } read)
        {
            dto.Query = read;
            dto.QueryVisibleToCaller = true;
        }

        return dto;
    }

    // Pins and excludes: names only for items the caller can open.
    private List<SmartSearchAutoCollectionItemDto> Items(IReadOnlyList<AutoCollectionItemEntry>? entries, User? caller)
        => (entries ?? []).Where(e => e is not null).Select(e => new SmartSearchAutoCollectionItemDto
        {
            ItemId = e.ItemId,
            Name = caller is null ? null : _access.GetItem<BaseItem>(e.ItemId, caller)?.Name,
            AddedUtc = e.AddedUtc,
            DetachedUtc = e.DetachedUtc
        }).ToList();

    // Held removals: ids only of items the caller can open, the rest counted; library locations for administrators only.
    private SmartSearchAutoCollectionPendingGuardDto GuardDto(AutoCollectionPendingGuard guard, User? caller, bool callerIsAdmin)
    {
        var ids = guard.WouldRemove ?? [];
        var visible = caller is null ? [] : ids.Where(i => _access.GetItem<BaseItem>(i, caller) is not null).ToList();
        return new SmartSearchAutoCollectionPendingGuardDto
        {
            DetectedUtc = guard.DetectedUtc,
            WouldRemoveCount = guard.WouldRemoveCount,
            WouldRemove = visible,
            HiddenCount = guard.WouldRemoveCount - visible.Count,
            Reasons = guard.Reasons ?? [],
            Locations = callerIsAdmin && caller is not null ? guard.Locations : null,
            Approved = guard.ApprovedUtc is not null
        };
    }

    // Whether a run now would hold the removals for a review: the guard's rules with what is known now (a scan running, the
    // libraries' folders, the last run's candidates), as for a refresh. Saving a change from this preview relaxes the
    // large-removal rule for its first run; emptying is held anyway.
    private SmartSearchAutoCollectionGuardPreviewDto PreviewGuard(AutoCollectionDefinition? saved, AutoCollectionDefinition definition, int current, int removed, DesiredMembership desired, string? queryHash)
    {
        if (saved is null)
        {
            return new SmartSearchAutoCollectionGuardPreviewDto();
        }

        var previous = _state.Find(saved.Id) is { } state ? state with { PendingGuard = null, AcceptedRemovals = null } : null;
        var conditions = new AutoCollectionRunConditions(ScanRunning: _scanRunning());
        var verdict = _guard.Check(definition, previous, current, removed, _guard.WithScopeCount(desired), queryHash, _context.Settings, conditions, SmartSearchAutoCollectionRunReason.Manual);
        return new SmartSearchAutoCollectionGuardPreviewDto { WouldTrip = verdict.Tripped, Reasons = verdict.Reasons };
    }

    private void RequireWritable()
    {
        if (_store.IsReadOnly)
        {
            throw AutoCollectionErrors.StoreReadOnly;
        }
    }

    private void RequireOn()
    {
        if (!_context.Settings.Enabled)
        {
            throw AutoCollectionErrors.FeatureOff;
        }
    }

    // A taken name names the collection in the way only to a caller who can open it.
    private async Task RequireNameAsync(AutoCollectionDefinition definition, User caller, CancellationToken cancellationToken)
    {
        var name = await _names.CheckAsync(definition.Name, definition.Id, cancellationToken).ConfigureAwait(false);
        if (name.Error is not null)
        {
            throw AutoCollectionErrors.Invalid("Definition.Name", QueryErrorCodes.InvalidValue, name.Error);
        }

        if (!name.Available && name.Rebind is null)
        {
            throw AutoCollectionErrors.NameTaken(name.ForViewer(CanSee(caller)));
        }
    }

    // A collection the caller can open, when it still carries the definition's mark.
    private BoxSet? VisibleTethered(User caller, Guid boxSetId, Guid definitionId)
        => _access.GetItem<BoxSet>(boxSetId, caller) is { } box && CollectionLocator.HasTether(box, definitionId) ? box : null;

    private Func<Guid, bool> CanSee(User caller) => id => _access.GetItem<BaseItem>(id, caller) is not null;

    // What a pinned or excluded item is, when it can be read now.
    private IdentitySnapshot? Capture(Guid id) => _keeper?.Capture(id);

    // The live links of a saved definition's collection, when it can be written; nothing otherwise.
    private IReadOnlyList<Guid> CurrentMembers(AutoCollectionDefinition definition)
    {
        var location = _locator.Locate(definition);
        return location.Writable ? _links.Resolve(location.Box!).Members : [];
    }
}
