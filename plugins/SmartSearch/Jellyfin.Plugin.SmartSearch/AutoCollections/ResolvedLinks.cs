using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>A collection's live links, each resolved to the item it names.</summary>
/// <param name="Members">The items the links name, in link order, each once.</param>
/// <param name="Links">The first link of each member, by member id (its <c>ItemId</c> set to the member).</param>
/// <param name="Dead">The links that name nothing (their item is gone).</param>
/// <param name="Duplicates">The further links to a member already linked.</param>
/// <param name="Hash">The hash of the links as stored (paths and cached ids).</param>
/// <param name="DeadLinks">The dead links themselves, kept by a write whose removals are held.</param>
internal sealed record ResolvedLinks(
    IReadOnlyList<Guid> Members,
    IReadOnlyDictionary<Guid, LinkedChild> Links,
    int Dead,
    int Duplicates,
    string Hash,
    IReadOnlyList<LinkedChild>? DeadLinks = null);
