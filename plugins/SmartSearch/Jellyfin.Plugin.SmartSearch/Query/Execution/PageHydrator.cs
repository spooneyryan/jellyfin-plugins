using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// Turns a page of ids into DTOs (every path, every page): the ids (≤ 200) are loaded again with
/// <c>GetItemList</c>, the user's access (<see cref="QueryPlanner.CreateAccessQuery"/>) and the libraries of the result
/// the user can open now, with <see cref="PageOptions"/>, put back in the page's order and given to
/// <c>IDtoService.GetBaseItemDtos</c>. Candidates are never used for DTOs: they were loaded without images and, unless a
/// rule needed it, without user data, and <c>GetUserData</c> then answers an empty record, so page 1 and later
/// pages come out the same whichever path produced the ids.
/// </summary>
/// <remarks>
/// <c>/Items</c> checks every item it returns with <c>IsVisible(user)</c> (<c>DtoService.GetBaseItemDtos</c>,
/// <c>DtoService.cs:158</c>), and so does this hydration (through <c>GetItemList</c> with the user, and the DTO service
/// again): a Fast page, whose ids SQL chose, can come back shorter than asked. An id that no longer loads, an owned item
/// (the safety net) and one <c>IsVisible</c> refuses are all left out and counted in <see cref="HydratedPage.Dropped"/>.
/// </remarks>
internal sealed class PageHydrator
{
    private readonly ILibraryManager _library;
    private readonly IDtoService _dto;
    private readonly LibraryAccess _access;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageHydrator"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="dto">The DTO service.</param>
    /// <param name="access">The kit's library access.</param>
    public PageHydrator(ILibraryManager library, IDtoService dto, LibraryAccess access)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(access);
        _library = library;
        _dto = dto;
        _access = access;
    }

    /// <summary>
    /// Creates the page's <c>DtoOptions</c>: images (one of each of Primary, Backdrop and Thumb), user data,
    /// and the fields the result list shows. <c>new DtoOptions(false)</c> defaults both flags to true already; they are set
    /// anyway, so a change of that default can't drop them.
    /// </summary>
    /// <returns>The options.</returns>
    public static DtoOptions PageOptions() => new(false)
    {
        EnableImages = true,
        EnableUserData = true,
        ImageTypeLimit = 1,
        ImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Thumb],
        Fields = [ItemFields.PrimaryImageAspectRatio, ItemFields.DateCreated, ItemFields.Genres]
    };

    /// <summary>Hydrates a page.</summary>
    /// <param name="ids">The page's ids, in order.</param>
    /// <param name="user">The searching user.</param>
    /// <param name="libraryIds">The libraries the result was evaluated over (its plan's).</param>
    /// <param name="cancellationToken">The search's cancellation token.</param>
    /// <returns>The DTOs, in the page's order, and what was left out.</returns>
    public HydratedPage Hydrate(IReadOnlyList<Guid> ids, User user, IReadOnlyCollection<Guid> libraryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(libraryIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Count == 0)
        {
            return new HydratedPage([], []);
        }

        // The libraries the user can open now (an empty request would read as "every library").
        var folders = libraryIds.Count == 0 ? [] : LibraryAccess.TopParentIds(_access.ResolveLibraries(libraryIds, user));
        var loaded = new Dictionary<Guid, BaseItem>();
        if (folders.Length > 0)
        {
            var query = QueryPlanner.CreateAccessQuery(user, PlanPurpose.Search);
            query.ItemIds = ids.Distinct().ToArray();
            query.TopParentIds = folders;
            query.GroupByPresentationUniqueKey = false;
            query.DtoOptions = PageOptions();
            foreach (var item in _library.GetItemList(query))
            {
                loaded.TryAdd(item.Id, item);
            }
        }

        var items = new List<BaseItem>(ids.Count);
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (seen.Add(id) && loaded.TryGetValue(id, out var item) && !LibraryAccess.IsOwned(item))
            {
                items.Add(item);
            }
        }

        var dtos = items.Count == 0 ? [] : _dto.GetBaseItemDtos(items, PageOptions(), user);
        var shown = dtos.Select(d => d.Id).ToHashSet();
        return new HydratedPage(dtos, ids.Where(id => !shown.Contains(id)).Distinct().ToList());
    }
}
