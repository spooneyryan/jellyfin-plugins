using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using Jellyfin.Plugin.SmartSearch.Tests.Query;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>Definitions, saved queries and stores for the auto-collection tests.</summary>
internal static class AutoCollectionTestData
{
    public static readonly DateTime Created = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Gets a catalog with the test fields plus an <c>InCollection</c> field (which the registry gains later).</summary>
    public static FieldCatalog Catalog { get; } = new(
        [
            .. TestCatalog.Instance.Fields,
            new FieldDescriptor
            {
                Id = "InCollection", Label = "In collection", Group = "Scope", Type = FieldValueType.Choice,
                Kinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video],
                Operators = QueryOperators.Choice, ElementKind = FieldElementKind.Guid
            }
        ],
        TestCatalog.Instance.SortKeys);

    public static AutoCollectionChecks Checks { get; } = new(new Jellyfin.Plugin.SmartSearch.Query.Validation.QueryValidator(Catalog), new QueryCanonicalizer(Catalog), Catalog);

    public static string Q(string rule = "{\"Field\":\"Year\",\"Op\":\"Ge\",\"Value\":2020}", string kinds = "\"Movie\"", string extra = "")
        => "{\"Scope\":{\"ItemKinds\":[" + kinds + "]},\"Where\":{\"Children\":[" + rule + "]}" + extra + "}";

    public static SavedQueryRecord SavedQuery(Guid owner, string json, Guid? id = null)
    {
        using var document = JsonDocument.Parse(json);
        return new SavedQueryRecord
        {
            Id = id ?? Guid.NewGuid(),
            OwnerUserId = owner,
            Name = "Query",
            Query = document.RootElement.Clone(),
            CreatedUtc = Created,
            UpdatedUtc = Created,
            UpdatedByUserId = owner
        };
    }

    public static AutoCollectionDefinition Definition(Guid creator, Guid? savedQueryId, string name = "Horror 80s", Guid? id = null, Guid? collectionId = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        Source = new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Reference, SavedQueryId = savedQueryId },
        CollectionId = collectionId,
        CreatedByUserId = creator,
        CreatedUtc = Created,
        UpdatedByUserId = creator,
        UpdatedUtc = Created
    };

    public static AutoCollectionDefinition Snapshot(Guid creator, string json, string name = "Snapshot")
    {
        using var document = JsonDocument.Parse(json);
        return Definition(creator, null, name) with
        {
            Source = new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Snapshot, Snapshot = document.RootElement.Clone(), SnapshotTakenUtc = Created }
        };
    }

    public static AutoCollectionCheckContext Context(IEnumerable<SavedQueryRecord> queries, IEnumerable<User> users, IReadOnlySet<Guid>? autoCollections = null, bool readOnly = false, int maxMembers = 5000)
    {
        var byId = users.ToDictionary(u => u.Id);
        return new AutoCollectionCheckContext(
            readOnly,
            new SavedQueriesDocument { Queries = queries.ToList() },
            id => byId.TryGetValue(id, out var user) ? user : null,
            QueryLimits.ItemKinds,
            autoCollections ?? new HashSet<Guid>(),
            maxMembers);
    }

    public static User Admin(string name = "admin")
    {
        var user = PlannerWorld.NewUser(name);
        user.AddDefaultPermissions();
        user.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true);
        return user;
    }

    public static AutoCollectionStore Store(string folder)
        => new(Path.Combine(folder, AutoCollectionStore.FileName), Path.Combine(folder, "snapshots"), NullLogger.Instance);

    public static AutoCollectionStateStore StateStore(string folder)
        => new(Path.Combine(folder, AutoCollectionStateStore.FileName), Path.Combine(folder, "snapshots"), NullLogger.Instance);
}
