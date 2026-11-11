using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// An in-memory store of item-linked records as the kit sees one: <see cref="IAttachedRecordOwner"/> for the verify
/// sweep, <see cref="IDetachedRecordOwner"/> for re-resolution, retention, the archive and purge. It follows the rules the
/// interfaces ask of a real store (only records still as the kit saw them change; the archive is written before the
/// records leave), and records what it was asked.
/// </summary>
internal class FakeRecordOwner : IDetachedRecordOwner
{
    private readonly object _lock = new();

    public string Name { get; init; } = "records";

    public bool IsReadOnly { get; set; }

    public RelinkPolicy RelinkPolicy { get; set; } = RelinkPolicy.Default;

    public int DetachedRetentionDays { get; set; }

    public List<FakeRecord> Records { get; } = [];

    /// <summary>Gets the item-id sets the sweep asked to detach, in order.</summary>
    public List<IReadOnlyCollection<Guid>> DetachCalls { get; } = [];

    /// <summary>Gets or sets a gate every <see cref="DetachAsync"/> waits for (to hold a sweep while it runs).</summary>
    public TaskCompletionSource? DetachGate { get; set; }

    /// <summary>Gets or sets a value indicating whether the store's write after the archive's fails.</summary>
    public bool FailStoreWrite { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="ArchiveAsync"/> throws after the store's write succeeded.</summary>
    public bool FailAfterStoreWrite { get; set; }

    /// <summary>Gets or sets what runs right after the archive was written, before the store's write.</summary>
    public Action? AfterAppend { get; set; }

    public List<string> Snapshots { get; } = [];

    public FakeRecord Add(IdentitySnapshot identity, Guid? userId = null, DateTime? detachedUtc = null, DateTime? attachedUtc = null)
    {
        var record = new FakeRecord { Id = Guid.NewGuid().ToString("N"), Identity = identity, UserId = userId, DetachedUtc = detachedUtc, AttachedUtc = attachedUtc };
        lock (_lock)
        {
            Records.Add(record);
        }

        return record;
    }

    public FakeRecord? Find(string id)
    {
        lock (_lock)
        {
            return Records.FirstOrDefault(r => r.Id == id);
        }
    }

    public IReadOnlyCollection<Guid> GetAttachedItemIds()
    {
        lock (_lock)
        {
            return Records.Where(r => r.DetachedUtc is null).Select(r => r.Identity.ItemId).Distinct().ToList();
        }
    }

    public async Task<int> DetachAsync(IReadOnlyCollection<Guid> itemIds, DateTime detachedUtc, CancellationToken cancellationToken)
    {
        DetachCalls.Add(itemIds.ToList());
        if (DetachGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            var detached = 0;
            foreach (var record in Records.Where(r => r.DetachedUtc is null && itemIds.Contains(r.Identity.ItemId)))
            {
                record.DetachedUtc = detachedUtc;
                record.DetachedBy ??= "Sweep";
                detached++;
            }

            return detached;
        }
    }

    public IReadOnlyList<DetachedRecord> GetDetached()
    {
        lock (_lock)
        {
            return Records.Where(r => r.DetachedUtc is not null)
                .Select(r => new DetachedRecord { Id = r.Id, UserId = r.UserId, Identity = r.Identity, AttachedUtc = r.AttachedUtc, DetachedUtc = r.DetachedUtc!.Value })
                .ToList();
        }
    }

    public bool ContainsRecord(string id) => Find(id) is not null;

    /// <summary>Gets the archived record ids whose purge is pending (<see cref="IDetachedRecordOwner.IsPurgePending"/>).</summary>
    public HashSet<string> PurgePending { get; } = new(StringComparer.Ordinal);

    public bool IsPurgePending(ArchivedRecord record) => PurgePending.Contains(record.Id);

    public Task<int> ApplyResolutionsAsync(IReadOnlyList<DetachedResolution> resolutions, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var relinked = 0;
            foreach (var resolution in resolutions)
            {
                var record = Records.FirstOrDefault(r => r.Id == resolution.Record.Id);
                if (record is null || record.DetachedUtc != resolution.Record.DetachedUtc)
                {
                    continue;
                }

                record.LastOutcome = resolution.Result.Outcome;
                if (resolution.Result.ShouldRelink)
                {
                    Relink(record, resolution.Result);
                    relinked++;
                }
            }

            return Task.FromResult(relinked);
        }
    }

    public Task<IReadOnlyList<ArchivedRecord>> ArchiveAsync(DateTime detachedBefore, Action<IReadOnlyList<ArchivedRecord>> append, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var due = Records.Where(r => r.DetachedUtc is { } d && d <= detachedBefore).ToList();
            var archived = due.Select(r => new ArchivedRecord
            {
                Id = r.Id,
                UserId = r.UserId,
                Identity = r.Identity,
                AttachedUtc = r.AttachedUtc,
                DetachedUtc = r.DetachedUtc!.Value,
                Record = JsonSerializer.SerializeToNode(r, JsonStoreFormat.Options)!.AsObject()
            }).ToList();
            if (archived.Count > 0)
            {
                append(archived);
                AfterAppend?.Invoke();
            }

            if (FailStoreWrite)
            {
                throw new System.IO.IOException("The store couldn't be written.");
            }

            Records.RemoveAll(due.Contains);
            if (FailAfterStoreWrite)
            {
                // Something after the write (a log, a cache, a second write) fails: the records have left the store.
                throw new InvalidOperationException("Something after the store's write failed.");
            }

            return Task.FromResult<IReadOnlyList<ArchivedRecord>>(archived);
        }
    }

    public Task<IReadOnlyList<string>> RestoreFromArchiveAsync(IReadOnlyList<DetachedResolution> matches, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var restored = new List<string>();
            foreach (var match in matches)
            {
                if (match.Record is not ArchivedRecord archived || Records.Any(r => r.Id == archived.Id))
                {
                    continue;
                }

                var record = archived.Record.Deserialize<FakeRecord>(JsonStoreFormat.Options)!;
                Relink(record, match.Result);
                Records.Add(record);
                restored.Add(record.Id);
            }

            return Task.FromResult<IReadOnlyList<string>>(restored);
        }
    }

    public Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken)
    {
        var name = $"{Name}-{operation}-{Snapshots.Count}.json";
        Snapshots.Add(name);
        return Task.FromResult<string?>(name);
    }

    public Task<int> PurgeDetachedAsync(DateTime? detachedBefore, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Records.RemoveAll(r => r.DetachedUtc is { } d && (detachedBefore is null || d <= detachedBefore)));
        }
    }

    private static void Relink(FakeRecord record, ResolveResult result)
    {
        record.Identity = record.Identity with { ItemId = result.ItemId!.Value };
        record.DetachedUtc = null;
        record.DetachedBy = null;
        record.RelinkStrength = result.Strength;
        if (result.Strength != MatchStrength.Exact)
        {
            record.MediaSourceId = null;
        }
    }
}

/// <summary>
/// A <see cref="FakeRecordOwner"/> that chooses the relink policy per record (<see cref="IDetachedRecordOwner.GetRelinkPolicy"/>,
/// as Bookmarks does): <see cref="Lenient"/> for the ids in <see cref="LenientIds"/>, its own
/// <see cref="FakeRecordOwner.RelinkPolicy"/> otherwise, and it records every record it was asked about. The plain
/// <see cref="FakeRecordOwner"/> keeps the interface's default.
/// </summary>
internal sealed class PerRecordPolicyOwner : FakeRecordOwner, IDetachedRecordOwner
{
    public RelinkPolicy Lenient { get; init; } = RelinkPolicy.RuntimeWithin();

    public HashSet<string> LenientIds { get; } = new(StringComparer.Ordinal);

    public List<DetachedRecord> PolicyRequests { get; } = [];

    public RelinkPolicy GetRelinkPolicy(DetachedRecord record)
    {
        PolicyRequests.Add(record);
        return LenientIds.Contains(record.Id) ? Lenient : RelinkPolicy;
    }
}

/// <summary>
/// A <see cref="FakeRecordOwner"/> that adds records of its own to a full re-resolution
/// (<see cref="IDetachedRecordOwner.GetResolvable"/>, as User Ratings' mass-detach guard does), as detached since
/// <see cref="HeldSinceUtc"/>. The plain <see cref="FakeRecordOwner"/> keeps the interface's default.
/// </summary>
internal sealed class HeldRecordsOwner : FakeRecordOwner, IDetachedRecordOwner
{
    public DateTime HeldSinceUtc { get; init; }

    /// <summary>Gets the ids of the attached records the owner holds.</summary>
    public HashSet<string> HeldIds { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<DetachedRecord> GetResolvable()
        => [.. GetDetached(), .. Records.Where(r => r.DetachedUtc is null && HeldIds.Contains(r.Id))
            .Select(r => new DetachedRecord { Id = r.Id, UserId = r.UserId, Identity = r.Identity, AttachedUtc = r.AttachedUtc, DetachedUtc = HeldSinceUtc })];
}

/// <summary>A record of <see cref="FakeRecordOwner"/>.</summary>
internal sealed class FakeRecord
{
    public string Id { get; set; } = string.Empty;

    public Guid? UserId { get; set; }

    public IdentitySnapshot Identity { get; set; } = new();

    public DateTime? AttachedUtc { get; set; }

    public DateTime? DetachedUtc { get; set; }

    public string? DetachedBy { get; set; }

    public string? Note { get; set; }

    public string? MediaSourceId { get; set; }

    public MatchStrength? RelinkStrength { get; set; }

    public ResolveOutcome? LastOutcome { get; set; }
}
