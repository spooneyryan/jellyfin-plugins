using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The user data demo's part of the kit's import: records matched by id (<see cref="ImportMerge"/>), user ids
/// mapped, and <see cref="KnownUsers"/> kept to the records' users with this server's names.
/// </summary>
internal sealed class UserDataDemoImportAdapter : JsonImportAdapter<UserDataDemoDocument>
{
    private static readonly IEqualityComparer<UserDataDemoRecord> SameContent = ImportMerge.JsonContent<UserDataDemoRecord>();

    /// <inheritdoc />
    public override IReadOnlyDictionary<Guid, int> CountRecordsByUser(UserDataDemoDocument document) => UserDataDemoOwner.CountRecordsByUser(document);

    /// <inheritdoc />
    public override ImportApplied<UserDataDemoDocument> Apply(UserDataDemoDocument current, UserDataDemoDocument incoming, ImportMode mode, ImportUserMap users)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(users);

        var mapped = new List<UserDataDemoRecord>();
        foreach (var record in incoming.Records ?? [])
        {
            if (record is not null && users.TryMap(record.UserId, out var userId))
            {
                mapped.Add(record.UserId.Equals(userId) ? record : record with { UserId = userId });
            }
        }

        var merged = mode == ImportMode.Replace
            ? ImportMerge.Replace(current.Records, mapped, r => r.Id, SameContent)
            : ImportMerge.Merge(current.Records, mapped, r => r.Id, r => r.UpdatedAt, SameContent);

        var changed = merged.Counts.Added + merged.Counts.Updated + merged.Counts.Removed > 0;
        var known = (current.KnownUsers ?? KnownUsers.Empty)
            .Refresh(merged.Records.Select(r => r.UserId), users.Mapped.GroupBy(m => m.NewId).ToDictionary(g => g.Key, g => g.First().Name));
        if (!changed && known.Equals(current.KnownUsers ?? KnownUsers.Empty))
        {
            return new ImportApplied<UserDataDemoDocument>(current, merged.Counts, []);
        }

        return new ImportApplied<UserDataDemoDocument>(
            UserDataDemoOwner.WithRecords(current, merged.Records) with { KnownUsers = known },
            merged.Counts,
            []);
    }
}
