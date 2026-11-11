using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer library demo's records: a store of item-linked records that the kit's
/// lifecycle works on as it does on bookmarks. It is an
/// <see cref="IDetachedRecordOwner"/> (the verify sweep, re-resolution, the archive after
/// <c>DetachedRetentionDays</c>, the reattach from it, purge) and an <see cref="ILibraryChangeConsumer"/> that handles
/// every batch in the kit's order: records whose item or container was removed detach, a batch with removed folders
/// awaits the verify sweep, and added or updated items refresh the snapshots of records on them and re-resolve the
/// detached records with Bookmarks' relink policy (runtime within 2 %, at most 60 s).
/// </summary>
/// <remarks>
/// While the developer setting <c>EnableLibraryDemo</c> is off it holds nothing as far as the kit can tell: its filter takes
/// no change, it never reads or creates its file, and it says it is read-only, so every kit step leaves it alone (as the
/// kit leaves read-only data).
/// </remarks>
internal sealed class LibraryDemoRecords : IDetachedRecordOwner, ILibraryChangeConsumer, IDisposable
{
    /// <summary>The store's name, and the archive's (<c>archive/library-demo-archived.jsonl</c>).</summary>
    public const string StoreName = "library-demo";

    /// <summary>The consumer's name in logs and Diagnostics.</summary>
    public const string ConsumerName = "LibraryDemo";

    /// <summary>The demo's quiet period, short so a timer batch shows within seconds in a sandbox.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>The demo's maximum wait.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    private readonly IPluginConfigurationProvider _configuration;
    private readonly ILibraryManager _library;
    private readonly Func<AttachedRecordVerifier> _verifier;
    private readonly Func<DetachedRecords> _detached;
    private readonly string _snapshotDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryDemoRecords"/> class.
    /// </summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="verifier">The kit's verify sweep, resolved when first needed.</param>
    /// <param name="detached">The kit's detached records, resolved when first needed.</param>
    /// <param name="file">The store file (<c>BookmarkPaths.LibraryDemoFile</c>).</param>
    /// <param name="snapshotDirectory">The snapshot folder, for the pre-purge snapshot.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The lifecycle's clock (the kit's <c>Clock</c>).</param>
    public LibraryDemoRecords(
        IPluginConfigurationProvider configuration,
        ILibraryManager library,
        Func<AttachedRecordVerifier> verifier,
        Func<DetachedRecords> detached,
        string file,
        string snapshotDirectory,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(detached);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _configuration = configuration;
        _library = library;
        _verifier = verifier;
        _detached = detached;
        _snapshotDirectory = snapshotDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        Store = CreateStore(file, logger, _timeProvider);
        Filter = new LibraryChangeFilter
        {
            QuietPeriod = QuietPeriod,
            MaxWait = MaxWait,

            // Read in the handler: nothing is collected while the demo is off.
            Ignore = (_, _) => !IsEnabled,
        };
    }

    /// <summary>
    /// Gets the demo's store.
    /// </summary>
    public JsonDocumentStore<LibraryDemoDocument> Store { get; }

    /// <summary>
    /// Gets a value indicating whether the demo is on.
    /// </summary>
    public bool IsEnabled => _configuration.GetConfiguration().EnableLibraryDemo;

    /// <inheritdoc />
    public string Name => StoreName;

    /// <inheritdoc />
    public LibraryChangeFilter Filter { get; }

    /// <inheritdoc />
    public bool IsReadOnly => !IsEnabled || Store.IsReadOnly;

    /// <inheritdoc />
    public RelinkPolicy RelinkPolicy { get; } = RelinkPolicy.RuntimeWithin();

    /// <inheritdoc />
    public int DetachedRetentionDays => Math.Max(0, _configuration.GetConfiguration().DetachedRetentionDays);

    /// <inheritdoc />
    string ILibraryChangeConsumer.Name => ConsumerName;

    /// <summary>
    /// Creates the demo's store: schema version 1, no migrations, the kit's format.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <returns>The store.</returns>
    public static JsonDocumentStore<LibraryDemoDocument> CreateStore(string file, ILogger logger, TimeProvider? timeProvider = null)
        => new(file, LibraryDemoDocument.CurrentVersion, LibraryDemoDocument.CreateEmpty, [], logger, JsonStoreFormat.Options, timeProvider);

    /// <summary>
    /// Returns the document with the given records, ordered by creation time and id.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="records">The records.</param>
    /// <returns>The new document.</returns>
    public static LibraryDemoDocument WithRecords(LibraryDemoDocument document, IEnumerable<LibraryDemoRecord> records)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Records = records.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList() };
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetAttachedItemIds()
        => IsEnabled && !Store.IsReadOnly
            ? Store.Current.Records.Where(r => r.DetachedUtc is null && r.ItemId != Guid.Empty).Select(r => r.ItemId).Distinct().ToList()
            : [];

    /// <inheritdoc />
    public async Task<int> DetachAsync(IReadOnlyCollection<Guid> itemIds, DateTime detachedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.ToHashSet();
        var detached = 0;
        await Store.UpdateAsync(
            document =>
            {
                detached = 0;
                var records = document.Records.Select(r =>
                {
                    if (r.DetachedUtc is not null || !ids.Contains(r.ItemId))
                    {
                        return r;
                    }

                    detached++;
                    return r with { DetachedUtc = detachedUtc, DetachedBy = "Sweep", LastOutcome = null };
                }).ToList();
                return detached == 0 ? document : document with { Records = records };
            },
            cancellationToken).ConfigureAwait(false);
        return detached;
    }

    /// <inheritdoc />
    public IReadOnlyList<DetachedRecord> GetDetached()
        => IsEnabled && !Store.IsReadOnly
            ? Store.Current.Records.Where(r => r.DetachedUtc is not null).Select(ToDetached).ToList()
            : [];

    /// <inheritdoc />
    public bool ContainsRecord(string id)
        => IsEnabled && !Store.IsReadOnly && Guid.TryParse(id, out var guid) && Store.Current.Records.Any(r => r.Id == guid);

    /// <inheritdoc />
    public async Task<int> ApplyResolutionsAsync(IReadOnlyList<DetachedResolution> resolutions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        var byId = resolutions.GroupBy(r => r.Record.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var relinked = 0;
        await Store.UpdateAsync(
            document =>
            {
                relinked = 0;
                var changed = false;
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var records = document.Records.Select(r =>
                {
                    if (!byId.TryGetValue(r.Key, out var resolution) || r.DetachedUtc != resolution.Record.DetachedUtc)
                    {
                        return r;
                    }

                    if (resolution.Result.ShouldRelink)
                    {
                        relinked++;
                        changed = true;
                        return Relink(r, resolution.Result, now);
                    }

                    var outcome = Outcome(resolution.Result);
                    changed |= !string.Equals(outcome, r.LastOutcome, StringComparison.Ordinal);
                    return r with { LastOutcome = outcome };
                }).ToList();
                return changed ? document with { Records = records } : document;
            },
            cancellationToken).ConfigureAwait(false);
        return relinked;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArchivedRecord>> ArchiveAsync(DateTime detachedBefore, Action<IReadOnlyList<ArchivedRecord>> append, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(append);
        IReadOnlyList<ArchivedRecord> archived = [];
        await Store.UpdateAsync(
            document =>
            {
                var due = document.Records.Where(r => r.DetachedUtc is { } d && d <= detachedBefore).ToList();
                if (due.Count == 0)
                {
                    archived = [];
                    return document;
                }

                archived = due.Select(r => new ArchivedRecord
                {
                    Id = r.Key,
                    UserId = r.UserId,
                    Identity = r.Identity with { ItemId = r.ItemId },
                    AttachedUtc = r.AttachedUtc,
                    DetachedUtc = r.DetachedUtc!.Value,
                    Record = JsonSerializer.SerializeToNode(r, JsonStoreFormat.Options)!.AsObject()
                }).ToList();

                // The archive is written and flushed first; if that throws, nothing leaves the store.
                append(archived);
                var gone = due.Select(r => r.Id).ToHashSet();
                return document with { Records = document.Records.Where(r => !gone.Contains(r.Id)).ToList() };
            },
            cancellationToken).ConfigureAwait(false);
        return archived;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> RestoreFromArchiveAsync(IReadOnlyList<DetachedResolution> matches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var restored = new List<string>();
        await Store.UpdateAsync(
            document =>
            {
                restored.Clear();
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var live = document.Records.Select(r => r.Id).ToHashSet();
                var added = new List<LibraryDemoRecord>();
                foreach (var match in matches)
                {
                    if (match.Record is not ArchivedRecord archived || !match.Result.ShouldRelink
                        || archived.Record.Deserialize<LibraryDemoRecord>(JsonStoreFormat.Options) is not { } record || !live.Add(record.Id))
                    {
                        continue;
                    }

                    added.Add(Relink(record, match.Result, now));
                    restored.Add(record.Key);
                }

                return added.Count == 0 ? document : WithRecords(document, document.Records.Concat(added));
            },
            cancellationToken).ConfigureAwait(false);
        return restored;
    }

    /// <inheritdoc />
    public async Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken)
        => await Store.PreOpSnapshotAsync(_snapshotDirectory, operation, cancellationToken).ConfigureAwait(false) is { } path
            ? System.IO.Path.GetFileName(path)
            : null;

    /// <inheritdoc />
    public async Task<int> PurgeDetachedAsync(DateTime? detachedBefore, CancellationToken cancellationToken)
    {
        var purged = 0;
        await Store.UpdateAsync(
            document =>
            {
                var kept = document.Records.Where(r => r.DetachedUtc is not { } d || (detachedBefore is { } cutoff && d > cutoff)).ToList();
                purged = document.Records.Count - kept.Count;
                return purged == 0 ? document : document with { Records = kept };
            },
            cancellationToken).ConfigureAwait(false);
        return purged;
    }

    /// <inheritdoc />
    public async Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (IsReadOnly)
        {
            return;
        }

        // 1. Removed: the records of a removed item, or of an item inside a removed series, season or album (one event
        // for the folder).
        var removed = batch.Removed.Concat(batch.RemovedFolders).Select(c => c.ItemId).Where(id => id != Guid.Empty).ToHashSet();
        if (removed.Count > 0)
        {
            await DetachRemovedAsync(removed, cancellationToken).ConfigureAwait(false);
        }

        // 2. The verify sweep, for what a removed folder took with it silently.
        if (batch.RemovedFolders.Count > 0)
        {
            await _verifier().SweepAsync(VerifySweepReason.RemovedFolders, cancellationToken).ConfigureAwait(false);
        }

        // 3. Added and updated items: fresh snapshots for records on them, then the detached records re-resolved.
        if (batch.Updated.Count > 0)
        {
            await RefreshSnapshotsAsync(batch.Updated.Select(c => c.ItemId).ToHashSet(), cancellationToken).ConfigureAwait(false);
        }

        if ((batch.Added.Count > 0 || batch.Updated.Count > 0) && Store.Current.Records.Any(r => r.DetachedUtc is not null))
        {
            await _detached().ResolveDetachedAsync(this, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Store.Dispose();

    private static DetachedRecord ToDetached(LibraryDemoRecord record) => new()
    {
        Id = record.Key,
        UserId = record.UserId,
        Identity = record.Identity with { ItemId = record.ItemId },
        AttachedUtc = record.AttachedUtc,
        DetachedUtc = record.DetachedUtc ?? DateTime.MinValue
    };

    private static string Outcome(ResolveResult result)
        => result.Outcome == ResolveOutcome.Ambiguous ? "Ambiguous: " + result.Reason : result.Outcome.ToString();

    private async Task DetachRemovedAsync(HashSet<Guid> removed, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var byItem = 0;
        var byContainer = 0;
        await Store.UpdateAsync(
            document =>
            {
                byItem = 0;
                byContainer = 0;
                var records = document.Records.Select(r =>
                {
                    if (r.DetachedUtc is not null)
                    {
                        return r;
                    }

                    var identity = r.Identity with { ItemId = r.ItemId };
                    string by;
                    if (removed.Contains(r.ItemId))
                    {
                        by = "Removed";
                        byItem++;
                    }
                    else if (removed.Any(id => ItemIdentity.IsRemovedBy(identity, id)))
                    {
                        by = "Container";
                        byContainer++;
                    }
                    else
                    {
                        return r;
                    }

                    return r with { DetachedUtc = now, DetachedBy = by, LastOutcome = null };
                }).ToList();
                return byItem + byContainer > 0 ? document with { Records = records } : document;
            },
            cancellationToken).ConfigureAwait(false);
        if (byItem + byContainer > 0)
        {
            _logger.LogInformation("Library demo: {ByItem} records detached whose item was removed, {ByContainer} whose series, season or album was", byItem, byContainer);
        }
    }

    private async Task RefreshSnapshotsAsync(HashSet<Guid> updated, CancellationToken cancellationToken)
    {
        if (!Store.Current.Records.Any(r => r.DetachedUtc is null && updated.Contains(r.ItemId)))
        {
            return;
        }

        await Store.UpdateAsync(
            document =>
            {
                var changed = false;
                var records = document.Records.Select(r =>
                {
                    if (r.DetachedUtc is not null || !updated.Contains(r.ItemId) || _library.GetItemById(r.ItemId) is not { } item)
                    {
                        return r;
                    }

                    var identity = ItemIdentity.Capture(item, _library);
                    if (identity.Equals(r.Identity))
                    {
                        return r;
                    }

                    changed = true;
                    return r with { Identity = identity };
                }).ToList();
                return changed ? document with { Records = records } : document;
            },
            cancellationToken).ConfigureAwait(false);
    }

    // The new item id and a fresh snapshot (or, for a pending relink, the stored one moved to the predicted id),
    // attached since now (or the journal line's time); an Exact relink keeps the media source (rewritten when it was the old
    // item's id), any other clears it and sets the "relinked, check positions" mark.
    private LibraryDemoRecord Relink(LibraryDemoRecord record, ResolveResult result, DateTime now)
    {
        var newId = result.ItemId!.Value;
        var identity = result.Item is { } item ? ItemIdentity.Capture(item, _library) : record.Identity with { ItemId = newId };
        var attached = result.Source == MatchSource.Journal && result.Chain?.Steps is { Count: > 0 } steps ? steps[^1].Utc : now;
        var oldSource = record.ItemId.ToString("N", CultureInfo.InvariantCulture);
        var exact = result.Strength == MatchStrength.Exact;
        _logger.LogDebug("Library demo: record {Record} relinked ({Strength}, {Source})", record.Id, result.Strength, result.Source);
        return record with
        {
            ItemId = newId,
            Identity = identity,
            AttachedUtc = attached,
            DetachedUtc = null,
            DetachedBy = null,
            LastOutcome = null,
            MediaSourceId = exact
                ? (string.Equals(record.MediaSourceId, oldSource, StringComparison.OrdinalIgnoreCase) ? newId.ToString("N", CultureInfo.InvariantCulture) : record.MediaSourceId)
                : null,
            RelinkedUtc = exact ? record.RelinkedUtc : now,
            RelinkStrength = exact ? record.RelinkStrength : result.Strength
        };
    }
}
