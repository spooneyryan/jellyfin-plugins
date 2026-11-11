using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Moq;

namespace Jellyfin.Plugin.Kit.TestSupport.Library;

/// <summary>
/// A small in-memory library behind a mocked <see cref="ILibraryManager"/>, for item-identity tests:
/// <c>GetItemById</c>, <c>FindByPath</c> and <c>GetItemList</c> answer from the items added, with the filters the kit's
/// resolver uses (kinds, <c>HasAnyProviderId</c>, <c>AncestorIds</c>, season and episode numbers, name, years, virtual
/// items, limit), and <c>GetItemById&lt;BaseItem&gt;(id, user)</c> hides the items <see cref="Hide"/> hid from a user.
/// Like Jellyfin 10.11.5, <c>GetItemById(Guid.Empty)</c> and <c>FindByPath</c> of an empty path throw, so do both
/// <c>GetItemById</c> overloads for <see cref="PlaceholderId"/> (the server's row of type <c>PLACEHOLDER</c>), <c>FindByPath</c>
/// of a white-space path puts no path filter on its query and answers with the newest item (<c>LibraryManager.cs:938-955</c>,
/// <c>BaseItemRepository.cs:1788</c>), and removing a folder removes its descendants but raises <c>ItemRemoved</c> once,
/// for the folder only (<c>LibraryManager.cs:409-466</c>). For the library-change monitor it raises <c>ItemAdded</c>
/// and <c>ItemUpdated</c> on request and answers <c>IsScanRunning</c> with <see cref="IsScanRunning"/>. For the kit's
/// <c>LibraryAccess</c> and <c>VersionSets</c> its <c>GetItemList</c> also filters <c>ItemIds</c> and <c>TopParentIds</c>
/// (an item's top parent is set with <see cref="SetTopParent"/>; an item without one, like an owned extra, matches no
/// <c>TopParentIds</c>), drops the items <see cref="Hide"/> hid from the query's <c>User</c>, and, like the repository,
/// groups by <c>PresentationUniqueKey</c> when a user is set and <c>GroupByPresentationUniqueKey</c> is left on
/// (<c>BaseItemRepository.cs:1510-1543</c>): one member of each key is kept.
/// <para>A hidden item stands for one that fails core's <c>IsVisible(user)</c> on its own (a parental rating, an unrated
/// item, a blocked tag, a library the user can't open). Like 10.11.5, <c>GetItemList</c> drops only the hidden items
/// themselves (<c>LibraryManager.cs:1408-1431</c>), while <c>GetItemById&lt;BaseItem&gt;(id, user)</c> also refuses an item
/// under a hidden parent, as <c>IsVisibleStandalone</c> does (<c>BaseItem.cs:1330-1351</c>).
/// <see cref="IsVisible"/> is that per-item check, for <c>LibraryAccess</c>'s parent check.</para>
/// </summary>
public sealed class FakeLibrary
{
    private readonly List<BaseItem> _items = [];
    private readonly HashSet<(User User, Guid ItemId)> _hidden = [];
    private readonly Dictionary<Guid, Guid> _topParents = [];
    private readonly Dictionary<Guid, BaseItem> _cached = [];

    public FakeLibrary()
    {
        // Video.SourceType asks Jellyfin's static recordings manager whether the file is being recorded; one that records
        // nothing, as on a server without Live TV (the library-change filter reads SourceType).
        Video.RecordingsManager ??= new Mock<IRecordingsManager>().Object;

        Mock = new Mock<ILibraryManager>();
        Mock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => GetItemById(id));
        Mock.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User?>())).Returns((Guid id, User? user) => GetItemById(id, user));
        Mock.Setup(l => l.FindByPath(It.IsAny<string>(), It.IsAny<bool?>())).Returns((string path, bool? isFolder) => FindByPath(path, isFolder));
        Mock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => GetItemList(query));
        Mock.SetupGet(l => l.IsScanRunning).Returns(() => IsScanRunning);
    }

    /// <summary>
    /// Gets Jellyfin 10.11's placeholder item id, <c>00000000-0000-0000-0000-000000000001</c>: the row its user data
    /// migration keeps for detached user data (<c>BaseItemRepository.PlaceholderId</c>, type <c>PLACEHOLDER</c>). Every
    /// server has it, and 10.11.5's <c>GetItemById</c> throws <see cref="InvalidOperationException"/> ("Cannot deserialize
    /// unknown type.") for it instead of answering null; so does this fake.
    /// </summary>
    public static Guid PlaceholderId { get; } = new("00000000-0000-0000-0000-000000000001");

    public Mock<ILibraryManager> Mock { get; }

    public ILibraryManager Object => Mock.Object;

    /// <summary>Gets every <c>GetItemList</c> query made, in order.</summary>
    public List<InternalItemsQuery> Queries { get; } = [];

    /// <summary>Gets every id <c>GetItemById</c> was asked for, in order.</summary>
    public List<Guid> IdLookups { get; } = [];

    /// <summary>Gets every path <c>FindByPath</c> was asked for, in order.</summary>
    public List<string> PathLookups { get; } = [];

    /// <summary>Gets every id <c>GetItemById&lt;BaseItem&gt;(id, user)</c> was asked for, in order.</summary>
    public List<Guid> UserLookups { get; } = [];

    public IReadOnlyList<BaseItem> Items => _items;

    /// <summary>Gets or sets what <c>ILibraryManager.IsScanRunning</c> answers.</summary>
    public bool IsScanRunning { get; set; }

    public T Add<T>(T item, BaseItem? parent = null)
        where T : BaseItem
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Id == Guid.Empty)
        {
            item.Id = Guid.NewGuid();
        }

        if (parent is not null)
        {
            item.ParentId = parent.Id;
        }

        _cached.Remove(item.Id);
        _items.Add(item);
        return item;
    }

    public Movie AddMovie(string name, int? year, TimeSpan runtime, string? path = null, params (string Provider, string Value)[] providerIds)
    {
        var movie = new Movie
        {
            Name = name,
            SortName = name,
            ProductionYear = year,
            RunTimeTicks = runtime.Ticks,
            Path = path ?? $@"C:\media\Movies\{name} ({year})\{name} ({year}).mkv"
        };
        foreach (var (provider, value) in providerIds)
        {
            movie.ProviderIds[provider] = value;
        }

        return Add(movie);
    }

    public Series AddSeries(string name, string? path = null, params (string Provider, string Value)[] providerIds)
    {
        var series = new Series { Name = name, Path = path ?? $@"C:\media\Shows\{name}" };
        foreach (var (provider, value) in providerIds)
        {
            series.ProviderIds[provider] = value;
        }

        return Add(series);
    }

    public Season AddSeason(Series series, int number)
    {
        ArgumentNullException.ThrowIfNull(series);
        return Add(
            new Season
            {
                Name = $"Season {number}",
                IndexNumber = number,
                SeriesId = series.Id,
                SeriesName = series.Name,
                Path = $@"{series.Path}\Season {number:D2}"
            },
            series);
    }

    public Episode AddEpisode(Series series, Season season, int number, TimeSpan? runtime = null, bool isVirtual = false)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(season);
        return Add(
            new Episode
            {
                Name = $"Episode {number}",
                IndexNumber = number,
                ParentIndexNumber = season.IndexNumber,
                SeriesId = series.Id,
                SeasonId = season.Id,
                SeriesName = series.Name,
                RunTimeTicks = (runtime ?? TimeSpan.FromSeconds(30)).Ticks,
                IsVirtualItem = isVirtual,
                Path = isVirtual ? null : $@"{season.Path}\{series.Name} S{season.IndexNumber:D2}E{number:D2}.mkv"
            },
            season);
    }

    /// <summary>
    /// Hides items from a user: <c>GetItemById&lt;BaseItem&gt;(id, user)</c> answers null for them and for everything under
    /// them, and <c>GetItemList</c> leaves them (only them) out, as for an item rated above the user's limit.
    /// </summary>
    public void Hide(User user, params BaseItem[] items)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
        {
            _hidden.Add((user, item.Id));
        }
    }

    /// <summary>
    /// Gets whether one item, on its own, is visible to a user (core's <c>IsVisible(user, true)</c>): not hidden.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="user">The user.</param>
    /// <returns>Whether it is visible.</returns>
    public bool IsVisible(BaseItem item, User user)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(user);
        return !_hidden.Contains((user, item.Id));
    }

    /// <summary>
    /// Sets the top parent Jellyfin would store for an item (<c>TopParentId</c>: the library path's folder, one of a
    /// library's <c>PhysicalFolderIds</c>), which <c>InternalItemsQuery.TopParentIds</c> filters on.
    /// </summary>
    public void SetTopParent(BaseItem item, Guid topParentId)
    {
        ArgumentNullException.ThrowIfNull(item);
        _topParents[item.Id] = topParentId;
    }

    /// <summary>
    /// Removes an item and all its descendants, then raises <c>ItemRemoved</c> once, for the item only, as
    /// <c>LibraryManager.DeleteItem</c> does: with the parent it had, after cutting the item's own parent link
    /// (<c>item.SetParent(null)</c>, <c>v10.11.5:Emby.Server.Implementations/Library/LibraryManager.cs:451, 466</c>), so a
    /// removed music artist folder says <c>IsFolder == false</c> in the handler, as on the server.
    /// <para>With <paramref name="cacheKeepsDescendants"/>, <c>GetItemById</c> still finds the descendants afterwards, with
    /// their parent links, while every query leaves them out: 10.11.5's item cache after a music artist folder was deleted
    /// (its albums and tracks; seen in a User Ratings sandbox run). Adding an item with the same id again replaces the
    /// cached one.</para>
    /// </summary>
    public void Remove(BaseItem item, bool raiseEvent = true, bool cacheKeepsDescendants = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        var descendants = Descendants(item.Id).ToList();
        var gone = descendants.Append(item).ToHashSet();
        var parent = Find(item.ParentId);
        _items.RemoveAll(gone.Contains);
        _cached.Remove(item.Id);
        foreach (var descendant in descendants)
        {
            if (cacheKeepsDescendants)
            {
                _cached[descendant.Id] = descendant;
            }
            else
            {
                _cached.Remove(descendant.Id);
            }
        }

        item.ParentId = Guid.Empty;
        if (raiseEvent)
        {
            // ItemChangeEventArgs isn't an EventArgs, so Moq needs the sender too.
            Mock.Raise(l => l.ItemRemoved += null, Object, new ItemChangeEventArgs { Item = item, Parent = parent });
        }
    }

    /// <summary>
    /// Raises <c>ItemAdded</c> for an item already added, with its parent, as <c>LibraryManager.CreateItems</c> does.
    /// </summary>
    public void RaiseAdded(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Mock.Raise(l => l.ItemAdded += null, Object, new ItemChangeEventArgs { Item = item, Parent = Find(item.ParentId) });
    }

    /// <summary>
    /// Raises <c>ItemUpdated</c> for an item, as <c>LibraryManager.UpdateItemsAsync</c> does.
    /// </summary>
    public void RaiseUpdated(BaseItem item, ItemUpdateType reason = ItemUpdateType.MetadataImport)
    {
        ArgumentNullException.ThrowIfNull(item);
        Mock.Raise(l => l.ItemUpdated += null, Object, new ItemChangeEventArgs { Item = item, Parent = Find(item.ParentId), UpdateReason = reason });
    }

    public IEnumerable<BaseItem> Descendants(Guid id)
    {
        var children = _items.Where(i => i.ParentId == id && i.ParentId != Guid.Empty).ToList();
        return children.Concat(children.SelectMany(c => Descendants(c.Id)));
    }

    private BaseItem? GetItemById(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Guid can't be empty", nameof(id));
        }

        IdLookups.Add(id);
        ThrowForPlaceholder(id);
        return Find(id) ?? _cached.GetValueOrDefault(id);
    }

    private BaseItem? GetItemById(Guid id, User? user)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Guid can't be empty", nameof(id));
        }

        UserLookups.Add(id);
        ThrowForPlaceholder(id);

        // IsVisibleStandalone: the item itself, and every parent (as core's GetParents walks them).
        return (Find(id) ?? _cached.GetValueOrDefault(id)) is { } item && (user is null || (IsVisible(item, user) && Ancestors(item).All(a => Find(a) is not { } parent || IsVisible(parent, user)))) ? item : null;
    }

    // 10.11.5: RetrieveItem finds the placeholder's row, and DeserializeBaseItem can't make a PLACEHOLDER.
    private static void ThrowForPlaceholder(Guid id)
    {
        if (id == PlaceholderId)
        {
            throw new InvalidOperationException("Cannot deserialize unknown type.");
        }
    }

    private BaseItem? Find(Guid id) => id == Guid.Empty ? null : _items.FirstOrDefault(i => i.Id == id);

    private BaseItem? FindByPath(string path, bool? isFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PathLookups.Add(path);

        // 10.11.5 filters by path only when it isn't white space; without the filter the newest item of any kind comes back.
        var anyPath = string.IsNullOrWhiteSpace(path);
        return _items.LastOrDefault(i => (anyPath || string.Equals(i.Path, path, StringComparison.Ordinal)) && (isFolder is null || i.IsFolder == isFolder));
    }

    private List<BaseItem> GetItemList(InternalItemsQuery query)
    {
        Queries.Add(query);
        IEnumerable<BaseItem> items = _items;
        if (query.IncludeItemTypes.Length > 0)
        {
            items = items.Where(i => query.IncludeItemTypes.Contains(i.GetBaseItemKind()));
        }

        if (query.HasAnyProviderId is { Count: > 0 } any)
        {
            // Like the repository: "key:value" compared exactly, an empty value meaning "has the provider".
            items = items.Where(i => any.Any(e => i.ProviderIds.Any(p => string.Equals(p.Key, e.Key, StringComparison.Ordinal)
                && (string.IsNullOrEmpty(e.Value) || string.Equals(p.Value, e.Value, StringComparison.Ordinal)))));
        }

        if (query.AncestorIds.Length > 0)
        {
            items = items.Where(i => Ancestors(i).Any(a => query.AncestorIds.Contains(a)));
        }

        if (query.ParentIndexNumber is { } season)
        {
            items = items.Where(i => i.ParentIndexNumber == season);
        }

        if (query.IndexNumber is { } index)
        {
            items = items.Where(i => i.IndexNumber == index);
        }

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            items = items.Where(i => string.Equals(i.Name, query.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Years.Length > 0)
        {
            items = items.Where(i => i.ProductionYear is { } year && query.Years.Contains(year));
        }

        if (query.IsVirtualItem is { } isVirtual)
        {
            items = items.Where(i => i.IsVirtualItem == isVirtual);
        }

        if (query.ItemIds.Length > 0)
        {
            items = items.Where(i => query.ItemIds.Contains(i.Id));
        }

        if (query.TopParentIds.Length > 0)
        {
            items = items.Where(i => _topParents.TryGetValue(i.Id, out var top) && query.TopParentIds.Contains(top));
        }

        if (query.User is { } user)
        {
            items = items.Where(i => !_hidden.Contains((user, i.Id)));
            if (query.GroupByPresentationUniqueKey)
            {
                items = items.GroupBy(i => i.PresentationUniqueKey ?? i.Id.ToString("N")).Select(g => g.Last());
            }
        }

        if (query.Limit is { } limit)
        {
            items = items.Take(limit);
        }

        return items.ToList();
    }

    private IEnumerable<Guid> Ancestors(BaseItem item)
    {
        var parentId = item.ParentId;
        for (var depth = 0; depth < 16 && parentId != Guid.Empty; depth++)
        {
            yield return parentId;
            parentId = Find(parentId)?.ParentId ?? Guid.Empty;
        }
    }
}
