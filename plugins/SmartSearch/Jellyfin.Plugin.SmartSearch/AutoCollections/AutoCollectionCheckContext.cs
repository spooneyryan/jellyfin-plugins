using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Storage;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>What <see cref="AutoCollectionChecks"/> reads besides the definition.</summary>
/// <param name="StoreReadOnly">Whether the definitions' store is read-only (a newer file, a failed upgrade).</param>
/// <param name="SavedQueries">The saved queries.</param>
/// <param name="FindUser">Finds a user by id (<c>null</c> when deleted).</param>
/// <param name="AllowedItemKinds">The setting <c>AllowedItemKinds</c>.</param>
/// <param name="AutoCollectionIds">Every auto-collection's collection id.</param>
/// <param name="MaxMembersPerCollection">The setting <c>MaxMembersPerCollection</c>.</param>
internal sealed record AutoCollectionCheckContext(
    bool StoreReadOnly,
    SavedQueriesDocument SavedQueries,
    Func<Guid, User?> FindUser,
    IReadOnlyCollection<BaseItemKind> AllowedItemKinds,
    IReadOnlySet<Guid> AutoCollectionIds,
    int MaxMembersPerCollection);
