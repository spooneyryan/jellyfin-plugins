using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Kit.Tests.Users;

/// <summary>
/// An in-memory <see cref="IUserDataOwner"/> with Bookmarks' policies: <c>KeepAsOrphan</c> and <c>Anonymize</c> remove
/// records without a file and keep the others as tombstones; <c>Delete</c> removes all of them.
/// </summary>
internal sealed class FakeUserDataOwner : IUserDataOwner
{
    public FakeUserDataOwner(string name = "fake")
    {
        Name = name;
    }

    public string Name { get; }

    public List<FakeRecord> Records { get; } = [];

    public bool ReadOnly { get; set; }

    public bool Unreadable { get; set; }

    public Exception? FailWith { get; set; }

    public Exception? CountFailsWith { get; set; }

    public Func<Guid, Guid, int>? Conflicts { get; set; }

    public Func<Guid, DeletedUserPolicy, bool>? WouldChange { get; set; }

    public Action? AfterDeletion { get; set; }

    public List<string> Snapshots { get; } = [];

    public List<(Guid UserId, DeletedUserPolicy Policy)> Deletions { get; } = [];

    public List<IReadOnlyDictionary<Guid, Guid>> Remaps { get; } = [];

    public int Refreshes { get; private set; }

    public bool IsReadOnly => Unreadable ? throw new Jellyfin.Plugin.Kit.Api.PluginException(Jellyfin.Plugin.Kit.Api.PluginErrorKind.Unavailable, "unreadable") : ReadOnly;

    public KnownUsers KnownUsers { get; set; } = KnownUsers.Empty;

    public FakeUserDataOwner Add(Guid userId, int count, bool hasFile = false, string? name = null)
    {
        for (var i = 0; i < count; i++)
        {
            Records.Add(new FakeRecord(userId, hasFile, false));
        }

        if (name is not null)
        {
            KnownUsers = KnownUsers.With(userId, name);
        }

        return this;
    }

    public IReadOnlyDictionary<Guid, int> CountRecordsByUser() => CountFailsWith is not null
        ? throw CountFailsWith
        : Records.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Count());

    public int CountRemapConflicts(Guid oldId, Guid newId) => Conflicts?.Invoke(oldId, newId) ?? 0;

    public Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
    {
        Refreshes++;
        KnownUsers = KnownUsers.Refresh(CountRecordsByUser().Keys, currentNames);
        return Task.CompletedTask;
    }

    public Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken)
    {
        var name = $"{Name}-{operation}-{Snapshots.Count}.json";
        Snapshots.Add(name);
        return Task.FromResult<string?>(name);
    }

    public bool WouldChangeRecords(Guid userId, DeletedUserPolicy policy) => WouldChange?.Invoke(userId, policy) ?? true;

    public Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Deletions.Add((userId, policy));
        var affected = 0;
        for (var i = Records.Count - 1; i >= 0; i--)
        {
            var record = Records[i];
            if (!record.UserId.Equals(userId))
            {
                continue;
            }

            if (policy != DeletedUserPolicy.Delete && record.HasFile)
            {
                if (!record.Tombstone)
                {
                    Records[i] = record with { Tombstone = true };
                    affected++;
                }
            }
            else
            {
                Records.RemoveAt(i);
                affected++;
            }
        }

        if (!Records.Exists(r => r.UserId.Equals(userId)))
        {
            KnownUsers = KnownUsers.Without(userId);
        }

        AfterDeletion?.Invoke();
        return Task.FromResult(affected);
    }

    public Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken)
    {
        Remaps.Add(map);
        var moved = 0;
        for (var i = 0; i < Records.Count; i++)
        {
            if (map.TryGetValue(Records[i].UserId, out var newId))
            {
                Records[i] = Records[i] with { UserId = newId };
                moved++;
            }
        }

        KnownUsers = KnownUsers.Remap(map);
        return Task.FromResult(moved);
    }

    internal sealed record FakeRecord(Guid UserId, bool HasFile, bool Tombstone);
}
