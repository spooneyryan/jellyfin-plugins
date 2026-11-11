using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// The administrator's tools for the saved queries (<see cref="ISmartSearchSavedQueriesAdmin"/>), on the kit's
/// store, export envelope, import and user reconciler.
/// </summary>
/// <remarks>
/// <para><b>Bulk changes</b> (an import, a snapshot restore, an owner change) run inside
/// <see cref="ISavedQueryReferenceGuard.BulkChangeAsync{T}"/>: the guard's lock first, then the store's. Each works out the
/// queries it removes and changes and asks <see cref="ISavedQueryReferenceGuard.FindBrokenReferences"/>; a dry run lists
/// what would break (<c>BrokenAutoCollections</c>), a real run refuses with 409 <c>BreaksAutoCollections</c> unless
/// <c>force=true</c>, before it takes its pre-operation snapshot. A restore checks before it writes, which holds because
/// every change of a reference, and every write that could touch a referenced query, needs the guard's lock it holds.</para>
/// <para><b>Remap by name</b> runs the kit's reconciler, which remaps every owner of users' data, so it never runs under
/// the guard's lock or a store's (the lock order is the reconciler, then the auto-collections' store, then this store;
/// <see cref="ISavedQueryReferenceGuard"/>). Its plan is the reconciler's dry run; the plan is checked under the guard's
/// lock, which is released before the reconciler runs, and each store checks its own part again under its own lock. A
/// refusal part-way is answered with the owners already remapped (they stay so); a second run remaps the rest.</para>
/// <para><b>Export and import</b> carry the auto-collection definitions too (<see cref="AutoCollectionTransfer"/>): the
/// envelope's <c>Data</c> is the saved-query document with <c>AutoCollections</c> beside <c>Queries</c>; an export without
/// them (Smart Search 1.0) imports as before. The definitions are read and checked before anything is imported, and
/// imported after the saved queries, outside their locks.</para>
/// <para><b>Owners.</b> An owner change and a remap never give a user who isn't an administrator a query that uses an
/// administrator-only field (409 <c>AdminOnlyField</c>; an import skips such a record), nor take a user past
/// <c>MaxSavedQueriesPerUser</c> (409 <c>QuotaReached</c>; an import skips the record).</para>
/// </remarks>
internal sealed class SavedQueryAdmin : ISmartSearchSavedQueriesAdmin
{
    private const string FileStem = "smartsearch-saved-queries";

    private readonly SavedQueryStore _store;
    private readonly SavedQueryReader _reader;
    private readonly Func<ISavedQueryReferenceGuard> _guard;
    private readonly UserReconciler _reconciler;
    private readonly IUserManager _users;
    private readonly IServerApplicationHost _host;
    private readonly Func<object?> _settings;
    private readonly Func<SavedQuerySettings> _querySettings;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Func<IReadOnlyList<IUserDataOwner>> _owners;
    private readonly Func<AutoCollectionTransfer?> _autoCollections;

    /// <summary>
    /// Initializes a new instance of the <see cref="SavedQueryAdmin"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="reader">Reads stored queries.</param>
    /// <param name="guard">The reference guard.</param>
    /// <param name="reconciler">The kit's user reconciler.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="host">The server (its id in exports).</param>
    /// <param name="settings">The plugin settings, for exports.</param>
    /// <param name="querySettings">The saved queries' settings (the quota), read on each request.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="owners">The reconciler's owners of users' data, asked after a remap refused part-way which of them it
    /// had already remapped; the store alone by default.</param>
    /// <param name="autoCollections">The auto-collections' part of the export and import; none by default.</param>
    public SavedQueryAdmin(
        SavedQueryStore store,
        SavedQueryReader reader,
        Func<ISavedQueryReferenceGuard> guard,
        UserReconciler reconciler,
        IUserManager users,
        IServerApplicationHost host,
        Func<object?> settings,
        Func<SavedQuerySettings> querySettings,
        TimeProvider clock,
        ILogger logger,
        Func<IReadOnlyList<IUserDataOwner>>? owners = null,
        Func<AutoCollectionTransfer?>? autoCollections = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(querySettings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _reader = reader;
        _guard = guard;
        _reconciler = reconciler;
        _users = users;
        _host = host;
        _settings = settings;
        _querySettings = querySettings;
        _clock = clock;
        _logger = logger;
        _owners = owners ?? (() => [store]);
        _autoCollections = autoCollections ?? (() => null);
    }

    /// <inheritdoc />
    public SmartSearchSavedQueryListDto List(Guid? userId, int startIndex, int limit)
    {
        if (startIndex < 0)
        {
            throw SavedQueryErrors.Invalid("startIndex", QueryErrorCodes.OutOfRange, "startIndex must be 0 or more.");
        }

        if (limit is < 1 or > SavedQueryService.MaxListLimit)
        {
            throw SavedQueryErrors.Invalid("limit", QueryErrorCodes.OutOfRange, "limit must be from 1 to 500.");
        }

        var owners = new SavedQueryService.OwnerNames(_users);
        var listed = _store.Current.Queries
            .Where(q => userId is null || q.OwnerUserId.Equals(userId.Value))
            .OrderBy(q => owners.Find(q.OwnerUserId)?.Username ?? SavedQueryService.DeletedUserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(q => q.OwnerUserId)
            .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(q => q.Id)
            .ToList();
        return new SmartSearchSavedQueryListDto
        {
            Items = listed.Skip(startIndex).Take(limit).Select(q =>
            {
                var facts = _reader.Read(q);
                var owner = owners.Find(q.OwnerUserId);
                return new SmartSearchSavedQuerySummaryDto
                {
                    Id = q.Id,
                    Name = q.Name,
                    Description = q.Description,
                    OwnerUserId = q.OwnerUserId,
                    OwnerName = owner?.Username ?? SavedQueryService.DeletedUserName,
                    OwnerDeleted = owner is null,
                    Shared = q.Shared,
                    UpdatedUtc = q.UpdatedUtc,
                    Supported = facts.Supported,
                    Valid = facts.Valid,
                    AutoCollectionCount = _guard().CountReferences(q.Id)
                };
            }).ToList(),
            TotalRecordCount = listed.Count,
            StartIndex = startIndex
        };
    }

    /// <inheritdoc />
    public async Task SetOwnerAsync(Guid id, SmartSearchSavedQueryOwnerDto request, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store.Find(id) is null)
        {
            throw SavedQueryErrors.NotFound;
        }

        var user = KitUsers.Find(_users, request.UserId)
            ?? throw SavedQueryErrors.Invalid(nameof(request.UserId), SavedQueryErrors.UnknownUserCode, "No user of this server has this id.");
        var now = _clock.GetUtcNow().UtcDateTime;
        var guard = _guard();
        var followed = 0;
        await guard.BulkChangeAsync(
            async ct => await _store.UpdateAsync(
                document =>
                {
                    var stored = document.Queries.FirstOrDefault(q => q.Id.Equals(id)) ?? throw SavedQueryErrors.NotFound;
                    if (stored.OwnerUserId.Equals(user.Id))
                    {
                        return document;
                    }

                    // A referenced query moves with what references it (the auto-collections' own reassignment), alone
                    // only when forced; checked under the guard's lock, so no reference comes between the check and the
                    // write.
                    followed = guard.CountReferences(id);
                    if (!force && followed > 0)
                    {
                        throw SavedQueryErrors.BreaksReferences(
                            guard.FindBrokenReferences(new SavedQueryBulkChange([], [id])),
                            "Auto-collections use this query: reassign the auto-collection, which moves its query with it. force=true moves the query alone, and the auto-collections then stop until they follow another query.");
                    }

                    var taken = document.Queries.Where(q => q.OwnerUserId.Equals(user.Id)).Select(q => q.Name).ToHashSet(SavedQueryNames.Comparer);
                    var moved = stored with
                    {
                        OwnerUserId = user.Id,
                        Name = SavedQueryNames.Free(stored.Name, taken),
                        UpdatedUtc = new DateTime(Math.Max(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), UtcDateTimeConverter.Utc(stored.UpdatedUtc).Ticks + TimeSpan.TicksPerMillisecond), DateTimeKind.Utc)
                    };
                    var queries = document.Queries.Select(q => q.Id.Equals(id) ? moved : q).ToList();
                    var known = (document.KnownUsers ?? KnownUsers.Empty).With(user);
                    if (!queries.Exists(q => q.OwnerUserId.Equals(stored.OwnerUserId)))
                    {
                        known = known.Without(stored.OwnerUserId);
                    }

                    var after = SavedQueryStore.WithQueries(document, queries) with { KnownUsers = known };
                    if (CheckMoves(document, after, force) is { } refusal)
                    {
                        throw refusal;
                    }

                    return after;
                },
                ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (force && followed > 0)
        {
            _logger.LogWarning("Saved query {QueryId} was given to another owner over the auto-collections that use it", id);
        }

        _logger.LogInformation("Saved query {QueryId} given to another owner", id);
    }

    /// <inheritdoc />
    public IActionResult Export(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The data first: it refuses (503) a store whose upgrade failed, and passes on a newer version's file as it is.
        var data = _store.Documents.ExportData();
        var document = _store.Current;
        var serverUsers = ServerUsers();
        IEnumerable<ExportUser> users = ExportUser.For(SavedQueryStore.CountRecordsByUser(document).Keys, document.KnownUsers ?? KnownUsers.Empty, serverUsers, _reconciler.DeletedUserIds);
        var definitions = 0;
        if (_autoCollections() is { } transfer)
        {
            var part = transfer.Export(serverUsers, _reconciler.DeletedUserIds);
            data = data with { Data = AutoCollectionTransfer.Combine(data.Data, part.Data) };
            users = users.Concat(part.Users).GroupBy(u => u.Id).Select(g => g.First());
            definitions = (part.Data["Definitions"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
        }

        var envelope = ExportEnvelope.Create(
            SmartSearchKit.Options,
            data,
            users,
            _settings(),
            archive: null,
            _host.SystemId,
            _clock.GetUtcNow().UtcDateTime);
        _logger.LogInformation("The saved queries were exported ({Queries} queries, {AutoCollections} auto-collections)", document.Queries.Count, definitions);
        return envelope.ToFileResult(context, FileStem);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ImportAsync(HttpRequest request, string? mode, bool? dryRun, string? mapUsersBy, bool? force)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parsed = ImportRequest.Parse(mode, dryRun, mapUsersBy);
        var cancellationToken = request.HttpContext.RequestAborted;
        var envelope = await ExportEnvelope.ReadAsync(request, cancellationToken).ConfigureAwait(false);

        // The definitions are read (and refused) before anything is imported, then imported after the queries.
        var transfer = _autoCollections();
        var definitions = transfer?.Read(envelope, parsed, ServerUsers(), _reconciler.DeletedUserIds);
        envelope = AutoCollectionTransfer.WithoutDefinitions(envelope);
        var guard = _guard();
        var adapter = new SavedQueryImportAdapter(_reader, guard, IsAdministrator, _querySettings().MaxPerUser, refuseBreaking: !parsed.DryRun && force != true, realRun: !parsed.DryRun);
        Task<ImportReport> Run(CancellationToken ct) => JsonStoreImport.RunAsync(
            _store.Documents,
            envelope,
            parsed,
            adapter,
            SmartSearchKit.Options,
            ServerUsers(),
            _reconciler.DeletedUserIds,
            _store.SnapshotDirectory,
            _logger,
            ct);
        var before = _store.Current;
        var report = parsed.DryRun
            ? await Run(cancellationToken).ConfigureAwait(false)
            : await guard.BulkChangeAsync(Run, cancellationToken).ConfigureAwait(false);
        if (!parsed.DryRun)
        {
            // Outside the locks: whoever follows the queries hears which ones the import replaced, removed or brought
            // back (the auto-collections run those again: a removed query stops them as missing, a changed or returning
            // one is checked again).
            _store.NotifyChanged(before, _store.Current);
            if (adapter.Broken.Count > 0)
            {
                _logger.LogWarning("An import was forced over {Count} auto-collection references it breaks", adapter.Broken.Count);
            }

            await _reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = report.ToDictionary();
        result["BrokenAutoCollections"] = adapter.Broken;
        if (transfer is not null && definitions is not null)
        {
            // The queries are in by now: a failure of the definitions' part is reported beside them, not instead of them.
            try
            {
                result["AutoCollections"] = await transfer.ImportAsync(definitions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PluginException or System.IO.IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "The auto-collections of an import couldn't be imported; its saved queries were");
                result["AutoCollections"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Failed"] = true,
                    ["Error"] = ex is PluginException plugin ? plugin.Message : "The auto-collections couldn't be written.",
                    ["Notes"] = new List<string> { "The saved queries were imported, the auto-collections weren't. Import the file again once the problem is solved: the queries already imported count as unchanged." }
                };
            }
        }

        return result;
    }

    /// <inheritdoc />
    public IDictionary<string, object?> GetUsers() => _reconciler.Describe();

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> RemapUsersAsync(string? by, bool? dryRun, bool? force, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(by) && !string.Equals(by.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
        {
            throw SavedQueryErrors.Invalid("by", QueryErrorCodes.InvalidValue, "by must be Name.");
        }

        // The plan is the reconciler's dry run, taken with no lock of ours held: the reconciler's lock comes first in the
        // lock order (the reconciler, then the auto-collections' store, then the saved-query store).
        var guard = _guard();
        var plan = await _reconciler.RemapByNameAsync(true, cancellationToken).ConfigureAwait(false);
        var map = plan.Mapped.ToDictionary(m => m.OldId, m => m.NewId);
        if (dryRun ?? true)
        {
            var before = _store.Current;
            var after = SavedQueryStore.Remap(before, map, out _);
            var dry = plan.ToDictionary();
            dry["BrokenAutoCollections"] = guard.FindBrokenReferences(SavedQueryBulkChange.Between(before, after, map));
            dry["Refusal"] = CheckMoves(before, after, force: true)?.Message;
            return dry;
        }

        // The plan is checked under the guard's lock, which is then released: the reconciler (and every owner it remaps)
        // never runs under the guard's lock or a store's, or a store that is also an owner would wait on itself. A refused
        // plan writes nothing and takes no snapshot.
        await guard.BulkChangeAsync(
            ct =>
            {
                var before = _store.Current;
                if (CheckMoves(before, SavedQueryStore.Remap(before, map, out _), force == true, map) is { } refusal)
                {
                    throw refusal;
                }

                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);

        // Each owner checks its own part again under its own lock as the reconciler remaps it; this store through the scope
        // below (references by the lock-free FindBrokenReferences, administrator-only fields, the quota).
        var holding = _owners().Where(o => !o.IsReadOnly && o.CountRecordsByUser().Keys.Any(map.ContainsKey)).ToList();
        IReadOnlyList<SmartSearchAutoCollectionRefDto> broken = [];
        UserRemapReport report;
        try
        {
            using var scope = SavedQueryStore.CheckRemaps((before, after) =>
            {
                broken = guard.FindBrokenReferences(SavedQueryBulkChange.Between(before, after));
                return CheckMoves(before, after, force == true);
            });
            report = await _reconciler.RemapByNameAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginException ex) when (ex.Kind == PluginErrorKind.Conflict)
        {
            throw PartWay(ex, holding.Where(o => !o.CountRecordsByUser().Keys.Any(map.ContainsKey)).Select(o => o.Name).ToList());
        }

        var result = report.ToDictionary();
        result["BrokenAutoCollections"] = broken;
        return result;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ApplyPolicyAsync(SmartSearchApplyPolicyDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await _reconciler.ApplyPolicyAsync(request.UserIds.ToList(), cancellationToken).ConfigureAwait(false)).ToDictionary();
    }

    /// <inheritdoc />
    public IDictionary<string, object?> GetSnapshots() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Store"] = KitDiagnostics.DescribeStore(_store.Documents),
        ["KeepSnapshotsBeforeUtc"] = _store.Documents.KeepSnapshotsBeforeUtc,
        ["Snapshots"] = _store.Documents.ListSnapshots(_store.SnapshotDirectory).Select(s => s.ToDictionary()).ToList()
    };

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> RestoreSnapshotAsync(string? name, bool? dryRun, bool? force, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw SavedQueryErrors.Invalid("name", QueryErrorCodes.Required, "name is required.");
        }

        var guard = _guard();
        if (dryRun == true)
        {
            var snapshot = await _store.Documents.ReadSnapshotAsync(_store.SnapshotDirectory, name, cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["DryRun"] = true,
                ["RestoredFrom"] = name.Trim(),
                ["Queries"] = snapshot.Queries.Count,
                ["BrokenAutoCollections"] = guard.FindBrokenReferences(SavedQueryBulkChange.Between(_store.Current, snapshot))
            };
        }

        _store.Documents.ThrowIfReadOnlyForAdmin();
        IReadOnlyList<SmartSearchAutoCollectionRefDto> broken = [];
        SavedQueriesDocument? replaced = null;
        var restored = await guard.BulkChangeAsync(
            async ct =>
            {
                replaced = _store.Current;
                // Checked before the write: under the guard's lock no reference changes, and a query added meanwhile
                // (which the restore removes) can't be referenced yet.
                var snapshot = await _store.Documents.ReadSnapshotAsync(_store.SnapshotDirectory, name, ct).ConfigureAwait(false);
                var change = SavedQueryBulkChange.Between(_store.Current, snapshot);
                if (guard.ReferencesUnknown && !change.IsEmpty)
                {
                    throw SavedQueryErrors.ReferencesUnknown;
                }

                broken = guard.FindBrokenReferences(change);
                if (broken.Count > 0 && force != true)
                {
                    throw SavedQueryErrors.BreaksReferences(broken);
                }

                return await _store.Documents.RestoreSnapshotAsync(_store.SnapshotDirectory, name, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        _store.NotifyChanged(replaced!, _store.Current);
        await _reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        var result = restored.ToDictionary();
        result["DryRun"] = false;
        result["Queries"] = _store.Current.Queries.Count;
        result["BrokenAutoCollections"] = broken;
        return result;
    }

    // A remap refused part-way: the owners remapped before the refusal keep their new owners (each wrote on its own), and
    // the refusal says which. Running it again is safe: an owner with nothing left to remap is skipped.
    private static PluginException PartWay(PluginException refusal, IReadOnlyList<string> remapped)
    {
        if (remapped.Count == 0)
        {
            return refusal;
        }

        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in refusal.Extensions ?? new Dictionary<string, object?>())
        {
            extensions[key] = value;
        }

        extensions["remappedOwners"] = remapped;
        return new PluginException(refusal.Kind, refusal.Message + " The records of " + string.Join(", ", remapped) + " were already remapped and stay so; run the remap again once this is solved, and it remaps the rest.", refusal)
        {
            Code = refusal.Code,
            NonAdminMessage = refusal.NonAdminMessage,
            Extensions = extensions
        };
    }

    // What makes an owner change or a remap refuse: references that can't be checked (forced or not), references it would
    // break (unless forced), a query with an administrator-only field given to a user who isn't an administrator, or a
    // user taken past the quota.
    private PluginException? CheckMoves(SavedQueriesDocument before, SavedQueriesDocument after, bool force, IReadOnlyDictionary<Guid, Guid>? map = null)
    {
        if (_guard().ReferencesUnknown && !SavedQueryBulkChange.Between(before, after).IsEmpty)
        {
            return SavedQueryErrors.ReferencesUnknown;
        }

        if (!force && _guard().FindBrokenReferences(SavedQueryBulkChange.Between(before, after, map)) is { Count: > 0 } broken)
        {
            return SavedQueryErrors.BreaksReferences(broken);
        }

        var owners = before.Queries.ToDictionary(q => q.Id, q => q.OwnerUserId);
        var moved = after.Queries.Where(q => owners.TryGetValue(q.Id, out var was) && !was.Equals(q.OwnerUserId)).ToList();
        if (moved.Exists(q => _reader.Read(q).UsesAdminOnlyFields && !IsAdministrator(q.OwnerUserId)))
        {
            return SavedQueryErrors.AdminOnlyForUser();
        }

        var max = _querySettings().MaxPerUser;
        var counts = SavedQueryStore.CountRecordsByUser(before);
        foreach (var (owner, count) in SavedQueryStore.CountRecordsByUser(after))
        {
            if (count > max && count > counts.GetValueOrDefault(owner) && moved.Exists(q => q.OwnerUserId.Equals(owner)))
            {
                return SavedQueryErrors.QuotaFor(max);
            }
        }

        return null;
    }

    // The stored administrator flag of a user of this server: the new owner of a query, who isn't the caller.
    private bool IsAdministrator(Guid userId) => KitUsers.Find(_users, userId) is { } user && CurrentUser.IsAdmin(user);

    private Dictionary<Guid, string> ServerUsers()
        => KitUsers.GetAll(_users).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().Username);
}
