using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>What the mappers need to know of a plan's scope.</summary>
/// <param name="Kinds">The kinds the query can return: the scope's, narrowed by mandatory <c>ItemType</c> rules. A mapping is
/// Exact only when they all are its field's kinds.</param>
/// <param name="AllowedFolders">The physical folders of the allowed libraries (the requested ones the caller can open).</param>
/// <param name="Membership">The library membership resolver.</param>
/// <param name="User">The user whose access and data apply, or <c>null</c>.</param>
/// <param name="IncludeMissing">Whether missing (virtual) items are in scope.</param>
/// <param name="People">The people role resolver (the <c>People</c> rules' persons and their items).</param>
internal sealed record PlanningScope(IReadOnlySet<BaseItemKind> Kinds, IReadOnlyList<Guid> AllowedFolders, LibraryMembership Membership, User? User, bool IncludeMissing, PeopleRoleResolver People);
