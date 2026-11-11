using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// A fake of core 10.11.5's item queries that reproduces its SQL as it is, quirks included (for the differential
/// test), for the properties the planner can push: <c>CleanName</c>/<c>CleanValue</c> as stored (stale ones too),
/// <c>NameContains</c> unfolded against a case-sensitive <c>CleanName</c> or an ASCII-only <c>lower(OriginalTitle)</c>, and
/// <c>LIKE</c> after <c>Trim('%')</c> with <c>[ ] ^</c> literal; <c>Years</c> as a plain <c>IN</c>; ratings as a widened
/// float; dates as stored UTC instants; <c>StudioIds</c> through the Studio item's stored clean name; the user-data filters
/// over an arbitrary (the first) of the user's rows, no row dropped for both values, <c>IsResumable</c> dropping missing
/// items; <c>Has&lt;Provider&gt;Id</c> as <c>lower(key)</c> with any value; the access filters of <c>SetUser</c>; grouping
/// by <c>PresentationUniqueKey</c> with a user; provider ids loaded only when asked. A query property it doesn't model
/// throws, so a mapper can't push something the fake would silently ignore. It is also the planner's
/// <see cref="IPushdownFacts"/>, computed from the same rows.
/// <para>The library manager's three reads differ as 10.11.5's do (LibraryManager.cs:1402-1428, 1646-1671):
/// <c>GetItemIds</c> and <c>GetItemsResult</c> are SQL only (<c>GetItemIds</c> unordered and paged, as
/// <c>GetItemIdsList</c>'s <c>ApplyQueryFilter</c> is; <c>GetCount</c> counts the same rows unpaged;
/// <c>GetItemsResult</c> ordered, paged, and counted in SQL
/// when it has a <c>Limit</c> or <c>StartIndex</c>, BaseItemRepository.cs:250-283); <c>GetItemList</c> with a user also
/// drops what <c>IsVisible(user)</c> refuses, which <see cref="VisibleInMemory"/> models (the box set rule, tags
/// inherited from parents SQL doesn't read, the rating compared in memory).</para>
/// <para>An item is loaded as <c>DtoOptions</c> asks (BaseItemRepository.cs:428-436): provider ids only with the
/// <c>ProviderIds</c> field, <c>item.UserData</c> (the user-data rows) only with <c>EnableUserData</c> and
/// <c>ImageInfos</c> only with <c>EnableImages</c>; <see cref="UserData"/>'s <c>GetUserData</c> reads <c>item.UserData</c>
/// as core's does (UserDataManager.cs:239-245), so an item loaded without user data reads as never played, and
/// <see cref="Dto"/> builds DTOs from what the item was loaded with, after <c>IsVisible</c> (DtoService.cs:158).</para>
/// </summary>
internal sealed class FakeCore : IPushdownFacts
{
    // SQLite's order of two column values: NULL first, text by code point (BINARY), numbers and dates by value.
    private static readonly Comparer<object?> SqlOrder = Comparer<object?>.Create((x, y)
        => x is string || y is string ? MatchOrder.CompareText((string?)x, (string?)y) : Comparer<object?>.Default.Compare(x, y));

    // The InternalItemsQuery properties the fake implements; any other one set to a non-default value throws.
    private static readonly HashSet<string> Modelled = new(StringComparer.Ordinal)
    {
        "IncludeItemTypes", "TopParentIds", "IsVirtualItem", "ItemIds", "Name", "NameContains", "HasOverview", "Years", "OfficialRatings",
        "Genres", "Tags", "ExcludeTags", "StudioIds", "AncestorIds", "MinCommunityRating", "MinPremiereDate", "MaxPremiereDate",
        "MinDateCreated", "MinWidth", "MaxWidth", "IsLocked", "IsPlayed", "IsFavorite", "IsResumable", "HasTmdbId", "HasImdbId", "HasTvdbId",
        "User", "MaxParentalRating", "BlockUnratedItems", "ExcludeInheritedTags", "IncludeInheritedTags", "GroupByPresentationUniqueKey",
        "DtoOptions", "OrderBy", "Recursive", "EnableTotalRecordCount", "StartIndex", "Limit"
    };

    private static readonly BaseItemKind[] GroupedKinds = [BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.Movie, BaseItemKind.MusicVideo, BaseItemKind.Series, BaseItemKind.Season];

    private readonly Dictionary<(ItemValueType, string), FakeItemValue> _values = [];
    private readonly Dictionary<Guid, (Studio Studio, string? CleanName)> _studios = [];

    // Items that aren't rows of the planner's kinds but that GetItemById returns: seasons, physical folders, the root.
    private readonly Dictionary<Guid, BaseItem> _others = [];
    private readonly Dictionary<Guid, Guid> _seasons = [];
    private CleanFormSnapshot? _probed;
    private readonly Dictionary<Guid, IReadOnlyList<CollectionFolder>> _userLibraries = [];
    private readonly Dictionary<Guid, FakeRow> _byId = [];

    public FakeCore()
    {
        Library = new Mock<ILibraryManager>(MockBehavior.Strict);
        Library.Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Page(Run(q), q).Select(r => r.Id).ToList());
        Library.Setup(l => l.GetCount(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Run(q).Count);
        // GetItemList with a user post-filters IsVisible(user) in memory (LibraryManager.cs:1428); GetItemIds doesn't.
        Library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Page(Order(Run(q), q), q)
            .Where(r => q.User is null || IsVisibleTo(r, q.User))
            .Select(r => Loaded(r, q))
            .ToList());
        Library.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => ItemsResult(q));
        Library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => Find(id));
        Library.Setup(l => l.GetStudioId(It.IsAny<string>())).Returns((string name) => StudioIds?.Invoke(name) ?? StudioId(name));
        // No fake item has a path: a version link resolves by its cached id only.
        Library.Setup(l => l.FindByPath(It.IsAny<string>(), It.IsAny<bool?>())).Returns((BaseItem?)null);
        _others[RootId] = new AggregateFolder { Id = RootId, Name = "root" };

        UserData = new Mock<IUserDataManager>(MockBehavior.Strict);
        UserData.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((User user, BaseItem item) => GetUserData(user, item));
        Dto = new Mock<IDtoService>(MockBehavior.Strict);
        Dto.Setup(d => d.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Returns((IReadOnlyList<BaseItem> items, DtoOptions options, User? user, BaseItem? owner) => items
                .Where(i => user is null || RowById(i.Id) is not { } row || IsVisibleTo(row, user))
                .Select(i => ToDto(i, options, user))
                .ToList());
    }

    /// <summary>Gets core's user data manager, as far as the engine reads it (<c>GetUserData</c>).</summary>
    public Mock<IUserDataManager> UserData { get; }

    /// <summary>Gets core's DTO service, as far as the page hydrator calls it (<c>GetBaseItemDtos</c>).</summary>
    public Mock<IDtoService> Dto { get; }

    /// <summary>Gets or sets an action run on every query the fake answers (a test advances its clock or cancels there).</summary>
    public Action<InternalItemsQuery>? OnQuery { get; set; }

    /// <summary>Gets or sets an action run on every <c>GetItemsResult</c> with the rows its count counted (all of them, before paging).</summary>
    public Action<InternalItemsQuery, IReadOnlyList<FakeRow>>? OnItemsResult { get; set; }

    /// <summary>Gets the root folder's id (an <c>AggregateFolder</c>), an ancestor of every item in a library.</summary>
    public Guid RootId { get; } = Guid.NewGuid();

    public Mock<ILibraryManager> Library { get; }

    public List<FakeRow> Rows { get; } = [];

    public List<CollectionFolder> Libraries { get; } = [];

    public List<InternalItemsQuery> Queries { get; } = [];

    /// <summary>
    /// Gets or sets <c>BaseItem.IsVisible(user)</c> as <c>GetItemList</c> and <c>DtoService.GetBaseItemDtos</c> apply it
    /// after SQL (a recorded server's answers); <c>null</c> for <see cref="VisibleInMemory"/>, the fake's model of it
    /// (core's own check needs the static <c>LocalizationManager</c>, which a unit test hasn't got).
    /// </summary>
    public Func<FakeRow, User, bool>? IsVisible { get; set; }

    /// <summary>Gets or sets a value indicating whether the clean-form probe has a snapshot.</summary>
    public bool ProbeRan { get; set; } = true;

    public bool PipesReadable { get; set; } = true;

    public bool EpisodesReadable { get; set; } = true;

    public int PipeReads { get; private set; }

    /// <summary>
    /// Gets the clean-form probe's snapshot: <c>null</c> when the probe hasn't run; the one <see cref="RunProbe"/> took, which
    /// rows added later aren't in (the real probe runs at startup and after a scan, not after a metadata edit); otherwise
    /// the rows as they are now, as if the probe had just run.
    /// </summary>
    public CleanFormSnapshot? CleanForms => !ProbeRan ? null : _probed ?? Probe();

    /// <summary>Takes the probe's snapshot now, as the real probe does at startup and after each scan.</summary>
    public void RunProbe() => _probed = Probe();

    private CleanFormSnapshot Probe() => new(
        DateTime.UnixEpoch,
        Rows.Count,
        _values.Count,
        Rows.Where(r => CleanFormRisk.NameDiffers(r.Item.Name, r.CleanName)).Select(r => new StaleName(r.Id, r.Item.Name, r.CleanName))
            .Concat(_studios.Values.Where(s => CleanFormRisk.NameDiffers(s.Studio.Name, s.CleanName)).Select(s => new StaleName(s.Studio.Id, s.Studio.Name, s.CleanName)))
            .ToList(),
        _values.Values.Where(v => CleanFormRisk.Differs(v.Value, v.CleanValue)).Select(v => new ItemValueRow(v.Type, v.Value, v.CleanValue)).ToList());

    public LibraryAccess Access() => new(Library.Object, user => user is not null && _userLibraries.TryGetValue(user.Id, out var libraries) ? libraries : Libraries);

    /// <summary>The libraries a user can open (every library unless restricted).</summary>
    public IReadOnlyList<CollectionFolder> VisibleLibraries(User? user) => user is not null && _userLibraries.TryGetValue(user.Id, out var libraries) ? libraries : Libraries;

    public void Restrict(User user, params CollectionFolder[] visible) => _userLibraries[user.Id] = visible;

    /// <summary>
    /// Gets or sets core's <c>GetStudioId</c> for the names it knows (a recorded server's: the ids depend on its paths);
    /// <c>null</c> or a <c>null</c> answer is the fake's own id.
    /// </summary>
    public Func<string, Guid?>? StudioIds { get; set; }

    public CollectionFolder AddLibrary(string name, CollectionType? type, params Guid[] folders) => AddLibrary(Guid.NewGuid(), name, type, folders);

    /// <summary>Registers an item that isn't a row of the planner's kinds (a recorded season or folder) for <c>GetItemById</c>.</summary>
    /// <param name="id">The id.</param>
    /// <param name="kind">Its kind: Season, AggregateFolder, UserRootFolder, or anything else for a plain folder.</param>
    public void AddOther(Guid id, string kind) => _others.TryAdd(id, kind switch
    {
        "Season" => new Season { Id = id, Name = "Season" },
        "AggregateFolder" => new AggregateFolder { Id = id, Name = "root" },
        "UserRootFolder" => new UserRootFolder { Id = id, Name = "user root" },
        _ => new Folder { Id = id, Name = "folder" }
    });

    public CollectionFolder AddLibrary(Guid id, string name, CollectionType? type, params Guid[] folders)
    {
        var library = new CollectionFolder { Id = id, Name = name, CollectionType = type, PhysicalFolderIds = folders.Length == 0 ? [Guid.NewGuid()] : folders };
        Libraries.Add(library);
        foreach (var folder in library.PhysicalFolderIds)
        {
            _others.TryAdd(folder, new Folder { Id = folder, Name = name + " folder" });
        }

        return library;
    }

    /// <summary>Gets the id of a series' season (a <c>Season</c> item that isn't a row), made on first use.</summary>
    /// <param name="seriesId">The series.</param>
    /// <returns>The season's id.</returns>
    public Guid SeasonOf(Guid seriesId)
    {
        if (!_seasons.TryGetValue(seriesId, out var season))
        {
            season = Guid.NewGuid();
            _seasons[seriesId] = season;
            _others[season] = new Season { Id = season, Name = "Season 1", SeriesId = seriesId };
        }

        return season;
    }

    /// <summary>
    /// Gets an item's <c>AncestorIds</c> rows as 10.11.5 saves them (<c>BaseItem.GetAncestorIds</c>): its parents (an
    /// episode's season and series, then the physical folder, then the root) and every library whose locations hold that
    /// folder (<c>LibraryManager.GetCollectionFolders</c>), so a folder two libraries share has both libraries' ids.
    /// </summary>
    /// <param name="seasonId">The episode's season, or <c>null</c>.</param>
    /// <param name="seriesId">The episode's series, or <c>null</c>.</param>
    /// <param name="topParentId">The physical folder, or <c>null</c> (an owned item).</param>
    /// <returns>The ids.</returns>
    public List<Guid> AncestorsOf(Guid? seasonId, Guid? seriesId, Guid? topParentId)
    {
        var ancestors = new List<Guid>();
        if (seasonId is { } season)
        {
            ancestors.Add(season);
        }

        if (seriesId is { } series && series != Guid.Empty)
        {
            ancestors.Add(series);
        }

        if (topParentId is { } top)
        {
            ancestors.Add(top);
            ancestors.Add(RootId);
            ancestors.AddRange(Libraries.Where(l => l.PhysicalFolderIds.Contains(top)).Select(l => l.Id));
        }

        return ancestors;
    }

    public Studio AddStudio(string name, string? storedCleanName = null) => AddStudio(StudioId(name), name, storedCleanName);

    public Studio AddStudio(Guid id, string name, string? storedCleanName)
    {
        var studio = new Studio { Id = id, Name = name };
        _studios[studio.Id] = (studio, storedCleanName ?? Clean(name));
        return studio;
    }

    /// <summary>Seeds an <c>ItemValues</c> row with its stored clean value (an older version's, for the clean-form probe).</summary>
    public void SeedValue(ItemValueType type, string value, string cleanValue) => _values[(type, value)] = new FakeItemValue(type, value, cleanValue);

    public FakeRow Add(RowSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var id = spec.Id ?? Guid.NewGuid();
        FakeRow? created = null;
        BaseItem Create(bool withProviders)
        {
            BaseItem item = spec.Kind switch
            {
                BaseItemKind.Movie => new Movie(),
                BaseItemKind.Series => new Series(),
                BaseItemKind.Episode => new Episode { SeriesId = spec.Series?.Id ?? spec.SeriesId ?? Guid.Empty, SeriesName = spec.SeriesName ?? spec.Series?.Item.Name },
                BaseItemKind.MusicVideo => new MusicVideo(),
                BaseItemKind.Video => new Video(),
                BaseItemKind.BoxSet => new BoxSet(),
                _ => throw new ArgumentException("Unsupported kind " + spec.Kind)
            };
            item.Id = id;
            item.Name = spec.Name;
            item.OriginalTitle = spec.OriginalTitle;
            item.Overview = spec.Overview;
            item.ProductionYear = spec.Year;
            // Dates come back from the database as the stored instant with Kind = Utc (a local time was stored as
            // ToUniversalTime() on the server's zone).
            item.PremiereDate = spec.PremiereStoredUtc ?? StoredUtc(spec.PremiereDate, spec.Zone);
            item.DateCreated = StoredUtc(spec.DateCreated ?? new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc), spec.Zone)!.Value;
            item.Genres = Loaded(spec.Genres, removeEmpty: false);
            item.Tags = Loaded(spec.Tags, removeEmpty: false);
            item.Studios = Loaded(spec.Studios, removeEmpty: false);
            item.OfficialRating = spec.OfficialRating;
            item.InheritedParentalRatingValue = spec.InheritedRating;
            item.CommunityRating = spec.CommunityRating;
            item.CriticRating = spec.CriticRating;
            item.RunTimeTicks = spec.RunTimeTicks;
            item.Width = spec.Width;
            item.Container = spec.Container;
            item.IsLocked = spec.IsLocked;
            item.OwnerId = spec.Owner?.Id ?? spec.OwnerId ?? Guid.Empty;
            item.IsVirtualItem = spec.IsVirtual;
            item.ProviderIds = withProviders ? spec.Providers.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // The columns the repository maps onto every item (BaseItemRepository.cs:796-846): its sort name (a getter that
            // would otherwise need the server's configuration), its presentation key and a secondary's primary.
            item.SortName = spec.SortName ?? spec.CleanName ?? Clean(spec.Name);
            item.PresentationUniqueKey = created?.PresentationUniqueKey ?? spec.PresentationUniqueKey ?? id.ToString("N");
            if (item is Video video && created is not null)
            {
                video.PrimaryVersionId = created.PrimaryVersionId;
                video.LinkedAlternateVersions = created.LinkedVersions.Select(v => new LinkedChild { ItemId = v, Type = LinkedChildType.Manual }).ToArray();
            }

            spec.Configure?.Invoke(item);
            return item;
        }

        var full = Create(true);
        var row = new FakeRow
        {
            Item = full,
            ItemWithoutProviderIds = Create(false),
            Create = Create,
            CleanName = spec.CleanName ?? Clean(spec.Name),
            TopParentId = spec.TopParentId ?? (spec.Owner is not null || spec.OwnerId is not null ? null : spec.Library?.PhysicalFolderIds[0]),
            IsVirtual = spec.IsVirtual,
            InheritedRating = spec.InheritedRating,
            InheritedSubRating = spec.InheritedSubRating,
            MemoryScore = spec.InheritedRating,
            // A Video's GetBlockUnratedType reads SourceType, which needs the live TV manager: the base type's Other.
            UnratedType = spec.UnratedType ?? (spec.Kind == BaseItemKind.Video ? UnratedItem.Other : full.GetBlockUnratedType()).ToString(),
            PresentationUniqueKey = spec.PresentationUniqueKey ?? id.ToString("N"),
            SeriesId = spec.Kind == BaseItemKind.Episode ? spec.Series?.Id ?? spec.SeriesId : null,
            SeriesName = spec.Kind == BaseItemKind.Episode ? spec.SeriesName ?? spec.Series?.Item.Name : null,
            CommunityRating = spec.CommunityRating,
            PremiereDateUtc = full.PremiereDate,
            DateCreatedUtc = full.DateCreated,
            Width = spec.Width
        };
        if (spec.Ancestors is { } ancestors)
        {
            row.Ancestors.AddRange(ancestors);
        }
        else
        {
            var series = spec.Kind == BaseItemKind.Episode && row.SeriesId is { } s && s != Guid.Empty ? s : (Guid?)null;
            row.Ancestors.AddRange(AncestorsOf(series is { } of ? spec.SeasonId ?? SeasonOf(of) : spec.SeasonId, series, row.TopParentId));
        }

        row.Providers.AddRange(spec.Providers);
        if (spec.StoredValues is { } stored)
        {
            // The ItemValuesMap rows as stored (seeded with SeedValue), whatever the item's columns say.
            row.Values.AddRange(stored.Select(v => _values[v]));
        }
        else
        {
            AddValues(row, ItemValueType.Genre, spec.Genres);
            AddValues(row, ItemValueType.Studios, spec.Studios);
            AddValues(row, ItemValueType.Tags, spec.Tags);
        }

        // An empty key stands for the item's own key (its id).
        row.UserData.AddRange(spec.UserData.Select(u => u with { Key = u.Key.Length == 0 ? id.ToString("N") : u.Key }));
        row.Images.AddRange(spec.Images);
        created = row;
        row.Index = Rows.Count;
        Rows.Add(row);
        _byId[row.Id] = row;
        return row;
    }

    /// <summary>Deletes a row (an item removed from the library).</summary>
    /// <param name="row">The row.</param>
    public void Remove(FakeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Rows.Remove(row);
        _byId.Remove(row.Id);
    }

    /// <summary>Puts a deleted row back (an item found again by a scan).</summary>
    /// <param name="row">The row.</param>
    public void Restore(FakeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.Index = Rows.Count;
        Rows.Add(row);
        _byId[row.Id] = row;
    }

    /// <summary>
    /// Merges a secondary into a primary's version set as core's "Merge versions" does (VideosController.MergeVersions):
    /// the secondary's <c>PrimaryVersionId</c> is the primary's id ("N"), so its presentation key is the primary's too
    /// (<c>Video.CreatePresentationUniqueKey</c>), and the primary lists it in <c>LinkedAlternateVersions</c>.
    /// </summary>
    /// <param name="primary">The primary.</param>
    /// <param name="secondary">The secondary.</param>
    public void Merge(FakeRow primary, FakeRow secondary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        secondary.PrimaryVersionId = primary.Id.ToString("N");
        secondary.PresentationUniqueKey = primary.PresentationUniqueKey;
        primary.LinkedVersions.Add(secondary.Id);
        SyncVersions(primary);
        SyncVersions(secondary);
    }

    /// <summary>
    /// core's "Merge versions" as it is (VideosController.MergeVersions), the first row as the primary: each other row
    /// points at it, and its presentation key becomes the primary's id (<c>Video.SetPrimaryVersionId</c>); the primary lists
    /// it and the rows it listed, whose own pointers stay where they were, and its list is cleared. Merging into a set whose
    /// secondary already had versions of its own leaves a chain: those still point at the old primary and keep its id as
    /// their key, so <c>/Items</c> shows them as a row of their own.
    /// </summary>
    /// <param name="primary">The primary.</param>
    /// <param name="others">The rows merged into it.</param>
    public void MergeVersions(FakeRow primary, params FakeRow[] others)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(others);
        foreach (var row in others.Where(r => r.Id != primary.Id))
        {
            row.PrimaryVersionId = primary.Id.ToString("N");
            row.PresentationUniqueKey = row.PrimaryVersionId;
            foreach (var listed in row.LinkedVersions.Prepend(row.Id).Where(id => !primary.LinkedVersions.Contains(id)).ToList())
            {
                primary.LinkedVersions.Add(listed);
            }

            row.LinkedVersions.Clear();
            SyncVersions(row);
        }

        SyncVersions(primary);
    }

    /// <summary>A primary that lists a row whose own pointer is empty: the row joins the set and keeps its own key.</summary>
    /// <param name="primary">The primary.</param>
    /// <param name="listed">The listed row.</param>
    public void ListVersion(FakeRow primary, FakeRow listed)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(listed);
        primary.LinkedVersions.Add(listed.Id);
        SyncVersions(primary);
    }

    private static void SyncVersions(FakeRow row)
    {
        foreach (var item in new[] { row.Item, row.ItemWithoutProviderIds }.OfType<Video>())
        {
            item.PrimaryVersionId = row.PrimaryVersionId;
            item.PresentationUniqueKey = row.PresentationUniqueKey;
            item.LinkedAlternateVersions = row.LinkedVersions.Select(v => new LinkedChild { ItemId = v, Type = LinkedChildType.Manual }).ToArray();
        }
    }

    /// <summary>
    /// core's <c>GetUserData(user, item)</c> (UserDataManager.cs:239-245): the first of the user's rows the item was loaded
    /// with (<c>item.UserData</c>, only with <c>DtoOptions.EnableUserData</c>), else an empty record.
    /// </summary>
    public static UserItemData GetUserData(User user, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);
        return item.UserData?.Where(u => u.UserId.Equals(user.Id)).Select(u => new UserItemData
        {
            Key = u.CustomDataKey,
            Played = u.Played,
            IsFavorite = u.IsFavorite,
            PlaybackPositionTicks = u.PlaybackPositionTicks,
            PlayCount = u.PlayCount,
            LastPlayedDate = u.LastPlayedDate
        }).FirstOrDefault() ?? new UserItemData { Key = item.Id.ToString("N") };
    }

    // What DtoService.GetBaseItemDto fills that the engine's tests read: the id and name, the user data (only with
    // EnableUserData, from what the item was loaded with) and the image tags (only with EnableImages, from ImageInfos).
    private static BaseItemDto ToDto(BaseItem item, DtoOptions options, User? user)
    {
        var dto = new BaseItemDto { Id = item.Id, Name = item.Name };
        if (options.EnableUserData && user is not null)
        {
            var data = GetUserData(user, item);
            dto.UserData = new UserItemDataDto { Key = data.Key, ItemId = item.Id, Played = data.Played, IsFavorite = data.IsFavorite, PlaybackPositionTicks = data.PlaybackPositionTicks, PlayCount = data.PlayCount, LastPlayedDate = data.LastPlayedDate };
        }

        if (options.EnableImages)
        {
            dto.ImageTags = item.ImageInfos.Where(i => options.ImageTypes.Contains(i.Type)).GroupBy(i => i.Type).ToDictionary(g => g.Key, g => g.First().Path);
        }

        return dto;
    }

    /// <summary>core's <c>GetCleanValue</c>: whitespace as is, else <c>RemoveDiacritics().ToLowerInvariant()</c>.</summary>
    public static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? value : value.RemoveDiacritics().ToLowerInvariant();

    /// <summary>
    /// What <c>GetUserData(user, item)</c> returns on 10.11.5 (UserDataManager.cs:239): one of the user's rows the item
    /// was loaded with (<c>item.UserData</c>, filled only when the query's <c>DtoOptions.EnableUserData</c>; keys are not
    /// read), or none. Core takes the first in EF's include order, which isn't the order of the SQL filters' subquery: the
    /// fake takes the last, so a user whose rows of one item disagree gets one answer in SQL and another in memory.
    /// </summary>
    public UserItemData? MemoryUserData(User user, FakeRow row)
    {
        var data = row.UserData.LastOrDefault(u => u.UserId == user.Id);
        return data is null ? null : new UserItemData
        {
            Key = data.Key,
            Played = data.Played,
            IsFavorite = data.IsFavorite,
            PlaybackPositionTicks = data.PlaybackPositionTicks,
            PlayCount = data.PlayCount,
            LastPlayedDate = data.LastPlayedDate
        };
    }

    /// <summary>The SQL access filters of <c>SetUser</c> plus <c>BlockUnratedItems</c>, as a query carries them.</summary>
    public bool PassesAccess(FakeRow r, InternalItemsQuery q)
    {
        if (q.MaxParentalRating is { } max
            && !(r.InheritedRating is null || r.InheritedRating < max.Score || (r.InheritedRating == max.Score && (r.InheritedSubRating ?? 0) <= (max.SubScore ?? 0))))
        {
            return false;
        }

        if (q.BlockUnratedItems.Length > 0 && r.InheritedRating is null && q.BlockUnratedItems.Select(b => b.ToString()).Contains(r.UnratedType))
        {
            return false;
        }

        if (q.ExcludeInheritedTags.Length > 0
            && (TagCleans(r).Any(q.ExcludeInheritedTags.Contains)
                || (r.Kind == BaseItemKind.Episode && r.SeriesId is { } s && RowById(s) is { } series && TagCleans(series).Any(q.ExcludeInheritedTags.Contains))))
        {
            return false;
        }

        if (q.IncludeInheritedTags.Length > 0)
        {
            var own = TagCleans(r).Any(q.IncludeInheritedTags.Contains);
            var viaSeries = q.IncludeItemTypes.Any(t => t is BaseItemKind.Episode or BaseItemKind.Season)
                && r.SeriesId is { } parent && RowById(parent) is { } show && TagCleans(show).Any(q.IncludeInheritedTags.Contains);
            if (!own && !viaSeries)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 10.11.5's <c>BaseItem.IsVisible(user)</c> for the planner's kinds (BaseItem.cs:1584-1735, BoxSet.cs:156-182): the
    /// user's blocked tags against the item's own, its series' and the <see cref="FakeRow.MemoryTags"/> its season or
    /// library gives it, ignoring case (and the allowed tags, when the user has some); the rating it compares
    /// (<see cref="FakeRow.MemoryScore"/>) against the user's limit, or for an unrated item whether the user blocks its
    /// unrated kind; and for a box set with children, whether the user can open a library one of them is in.
    /// </summary>
    /// <param name="r">The row.</param>
    /// <param name="user">The user.</param>
    /// <returns>Whether core shows the item to the user.</returns>
    public bool VisibleInMemory(FakeRow r, User user)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(user);
        var tags = r.Item.Tags.Concat(r.MemoryTags).ToList();
        if (r.Kind == BaseItemKind.Episode && r.SeriesId is { } seriesId && RowById(seriesId) is { } series)
        {
            tags.AddRange(series.Item.Tags);
        }

        if (user.GetPreference(PreferenceKind.BlockedTags).Any(b => tags.Contains(b, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        var allowed = user.GetPreference(PreferenceKind.AllowedTags);
        if (allowed.Length > 0 && !allowed.Any(a => tags.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (r.MemoryScore is not { } score)
        {
            // GetBlockUnratedValue: the kind's unrated type (BoxSet's Movie, Series' Series), as SQL's UnratedType has it.
            if (user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Any(b => b.ToString() == r.UnratedType))
            {
                return false;
            }
        }
        else if (user.MaxParentalRatingScore is { } max
            && (score != max ? score > max : user.MaxParentalRatingSubScore is { } maxSub && (r.InheritedSubRating ?? 0) > maxSub))
        {
            return false;
        }

        if (r.Kind == BaseItemKind.BoxSet && r.Children.Count > 0)
        {
            var theirs = r.Children.Select(RowById).OfType<FakeRow>()
                .SelectMany(c => Libraries.Where(l => c.TopParentId is { } top && l.PhysicalFolderIds.Contains(top)))
                .Select(l => l.Id)
                .ToHashSet();
            return theirs.Count == 0 || VisibleLibraries(user).Any(l => theirs.Contains(l.Id));
        }

        return true;
    }

    /// <summary>Whether core shows the row to the user: the recorded answer (<see cref="IsVisible"/>) or the model.</summary>
    public bool IsVisibleTo(FakeRow r, User user) => IsVisible is { } recorded ? recorded(r, user) : VisibleInMemory(r, user);

    /// <summary>
    /// <c>GetItemsResult</c>: SQL only, ordered, paged, and counted (after grouping, before paging) when the query has a
    /// <c>Limit</c> or a <c>StartIndex</c> and <c>EnableTotalRecordCount</c>; otherwise the count is the list's.
    /// </summary>
    public QueryResult<BaseItem> ItemsResult(InternalItemsQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var rows = Order(Run(q), q);
        var page = Page(rows, q).Select(r => Loaded(r, q)).ToList();
        var counted = q.EnableTotalRecordCount && (q.Limit is not null || (q.StartIndex ?? 0) > 0);
        if (counted)
        {
            OnItemsResult?.Invoke(q, rows);
        }

        return new QueryResult<BaseItem>(q.StartIndex, counted ? rows.Count : page.Count, page);
    }

    /// <summary>The rows a query returns (one statement, no paging), grouped by presentation key when core would.</summary>
    public IReadOnlyList<FakeRow> Run(InternalItemsQuery q)
    {
        ArgumentNullException.ThrowIfNull(q);
        lock (Queries)
        {
            Queries.Add(q);
        }

        OnQuery?.Invoke(q);
        ThrowOnUnmodelled(q);
        if (q.User is null && q.OrderBy.Any(o => o.OrderBy is ItemSortBy.DatePlayed or ItemSortBy.PlayCount or ItemSortBy.IsFavoriteOrLiked or ItemSortBy.IsPlayed or ItemSortBy.IsUnplayed))
        {
            // OrderMapper.cs:33-38 reads query.User!.Id: EF fails to evaluate the parameter.
            throw new InvalidOperationException("An exception was thrown while attempting to evaluate a LINQ query parameter expression.");
        }

        // By ItemIds, the rows of those ids in table order (a hydration batch needn't scan every row); else every row.
        var rows = (q.ItemIds.Length > 0
                ? q.ItemIds.Distinct().Select(RowById).OfType<FakeRow>().OrderBy(r => r.Index)
                : (IEnumerable<FakeRow>)Rows)
            .Where(r => Sql(r, q))
            .ToList();
        if (q.User is not null && q.GroupByPresentationUniqueKey && (q.IncludeItemTypes.Length == 0 || q.IncludeItemTypes.Any(GroupedKinds.Contains)))
        {
            rows = rows.GroupBy(r => r.PresentationUniqueKey, StringComparer.Ordinal).Select(g => g.First()).ToList();
        }

        return rows;
    }

    /// <inheritdoc />
    public IReadOnlyList<ItemValueRow>? PipeValues(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PipeReads++;
        return PipesReadable ? _values.Values.Where(v => v.Value.Contains('|', StringComparison.Ordinal)).Select(v => new ItemValueRow(v.Type, v.Value, v.CleanValue)).ToList() : null;
    }

    /// <inheritdoc />
    public bool UserDataAgrees(Guid userId, UserDataFlag flag, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested ? Rows.All(r =>
        {
            var mine = r.UserData.Where(u => u.UserId == userId).Select(u => flag switch
            {
                UserDataFlag.Played => u.Played,
                UserDataFlag.Favorite => u.IsFavorite,
                _ => u.PlaybackPositionTicks > 0
            }).Distinct().Count();
            return mine <= 1;
        }) : throw new OperationCanceledException(cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<EpisodeSeries>? EpisodeSeries(IReadOnlyCollection<Guid> topParentIds, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested ? throw new OperationCanceledException(cancellationToken) : !EpisodesReadable ? null : Rows
            .Where(r => r.Kind == BaseItemKind.Episode && r.TopParentId is { } t && topParentIds.Contains(t))
            .Select(r => new EpisodeSeries(r.SeriesId ?? Guid.Empty, r.SeriesName))
            .Distinct()
            .ToList();

    public FakeRow? RowById(Guid id) => _byId.TryGetValue(id, out var row) ? row : Rows.FirstOrDefault(r => r.Id == id);

    private static string[] Loaded(IReadOnlyList<string> values, bool removeEmpty)
    {
        // The item's column joins its values with '|' and is split again on load (BaseItemRepository.cs:837-871).
        var column = string.Join('|', values);
        return string.IsNullOrWhiteSpace(column) ? [] : column.Split('|', removeEmpty ? StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None);
    }

    private static DateTime? StoredUtc(DateTime? value, TimeZoneInfo zone) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } d => d,
        { } d => DateTime.SpecifyKind(d - zone.GetUtcOffset(d), DateTimeKind.Utc)
    };

    private static Guid StudioId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("studio:" + name.ToLowerInvariant())));

    private static bool SameAsDefault(object? value, object? defaultValue)
    {
        if (value is null || defaultValue is null)
        {
            return value is null && defaultValue is null;
        }

        if (value is System.Collections.IEnumerable list && value is not string)
        {
            return !list.Cast<object>().Any() && !((System.Collections.IEnumerable)defaultValue).Cast<object>().Any();
        }

        return value.Equals(defaultValue);
    }

    // SQLite's LIKE without ESCAPE: % any run, _ any one character, ASCII letters ignore case, everything else literal.
    private static bool Like(string? text, string pattern)
    {
        if (text is null)
        {
            return false;
        }

        static bool Eq(char a, char b) => a == b || (a < 128 && b < 128 && char.ToLowerInvariant(a) == char.ToLowerInvariant(b));
        var memo = new Dictionary<(int, int), bool>();
        bool Match(int t, int p)
        {
            if (memo.TryGetValue((t, p), out var known))
            {
                return known;
            }

            bool result;
            if (p == pattern.Length)
            {
                result = t == text.Length;
            }
            else if (pattern[p] == '%')
            {
                result = Match(t, p + 1) || (t < text.Length && Match(t + 1, p));
            }
            else
            {
                result = t < text.Length && (pattern[p] == '_' || Eq(text[t], pattern[p])) && Match(t + 1, p + 1);
            }

            memo[(t, p)] = result;
            return result;
        }

        return Match(0, 0);
    }

    private static string? AsciiLower(string? text) => text is null ? null : new string(text.Select(c => c < 128 ? char.ToLowerInvariant(c) : c).ToArray());

    private static void ThrowOnUnmodelled(InternalItemsQuery q)
    {
        var defaults = new InternalItemsQuery();
        foreach (var property in typeof(InternalItemsQuery).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is not { IsPublic: true } || property.GetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0 || Modelled.Contains(property.Name))
            {
                continue;
            }

            if (!SameAsDefault(property.GetValue(q), property.GetValue(defaults)))
            {
                throw new NotSupportedException("The fake doesn't model InternalItemsQuery." + property.Name + "; the planner must not set it.");
            }
        }
    }

    private static IEnumerable<FakeRow> Page(IReadOnlyList<FakeRow> rows, InternalItemsQuery q)
        => rows.Skip(q.StartIndex ?? 0).Take(q.Limit ?? int.MaxValue);

    // The item as the query's DtoOptions load it: without user data or images, one of the row's two items; with them, a
    // fresh one carrying the user-data rows (in reverse, so that core's FirstOrDefault reads the row MemoryUserData reads)
    // or the images.
    private static BaseItem Loaded(FakeRow r, InternalItemsQuery q)
    {
        var providers = q.DtoOptions.ContainsField(ItemFields.ProviderIds);
        if (!q.DtoOptions.EnableUserData && !q.DtoOptions.EnableImages)
        {
            return providers ? r.Item : r.ItemWithoutProviderIds;
        }

        var item = r.Create(providers);
        if (q.DtoOptions.EnableUserData)
        {
            item.UserData = r.UserData.AsEnumerable().Reverse().Select(u => new UserData
            {
                CustomDataKey = u.Key,
                ItemId = r.Id,
                Item = null!,
                UserId = u.UserId,
                User = null!,
                Played = u.Played,
                IsFavorite = u.IsFavorite,
                PlaybackPositionTicks = u.PlaybackPositionTicks,
                PlayCount = u.PlayCount,
                LastPlayedDate = u.LastPlayedDate
            }).ToList();
        }

        if (q.DtoOptions.EnableImages)
        {
            item.ImageInfos = [.. r.Images];
        }

        return item;
    }

    // ApplyOrder (BaseItemRepository.cs:1545): SortName when nothing is asked, then each key; a first SortName key is
    // followed by Name. Text compares as SQLite's BINARY collation (code points), NULL first. Core adds no tiebreak of its
    // own, so the order among ties isn't defined; the fake keeps its insertion order.
    private static List<FakeRow> Order(IReadOnlyList<FakeRow> rows, InternalItemsQuery q)
    {
        var order = q.OrderBy.Where(o => o.OrderBy != ItemSortBy.Default).ToList();
        if (order.Count == 0)
        {
            order.Add((ItemSortBy.SortName, SortOrder.Ascending));
        }

        if (order[0].OrderBy == ItemSortBy.SortName)
        {
            order.Insert(1, (ItemSortBy.Name, order[0].SortOrder));
        }

        FakeUserData? Data(FakeRow r) => q.User is { } user ? r.UserData.FirstOrDefault(d => d.UserId == user.Id) : null;
        IOrderedEnumerable<FakeRow>? sorted = null;
        foreach (var (by, direction) in order)
        {
            Func<FakeRow, object?> key = by switch
            {
                ItemSortBy.SortName => r => r.Item.SortName,
                ItemSortBy.Name => r => r.Item.Name,
                ItemSortBy.ProductionYear => r => r.Item.ProductionYear,
                // OrderMapper: the date, else DateTime.MinValue.AddYears(year - 1), else NULL.
                ItemSortBy.PremiereDate => r => r.PremiereDateUtc ?? (r.Item.ProductionYear is { } y && y is >= 1 and <= 9999 ? DateTime.MinValue.AddYears(y - 1) : null),
                ItemSortBy.DateCreated => r => r.DateCreatedUtc,
                ItemSortBy.CommunityRating => r => r.CommunityRating,
                ItemSortBy.CriticRating => r => r.Item.CriticRating,
                ItemSortBy.Runtime => r => r.Item.RunTimeTicks,
                ItemSortBy.OfficialRating => r => r.InheritedRating,
                ItemSortBy.VideoBitRate => r => r.Item.TotalBitrate,
                ItemSortBy.DatePlayed => r => Data(r)?.LastPlayedDate,
                ItemSortBy.PlayCount => r => Data(r)?.PlayCount,
                _ => throw new NotSupportedException("The fake doesn't order by " + by)
            };
            var ascending = direction == SortOrder.Ascending;
            sorted = sorted is null
                ? ascending ? rows.OrderBy(key, SqlOrder) : rows.OrderByDescending(key, SqlOrder)
                : ascending ? sorted.ThenBy(key, SqlOrder) : sorted.ThenByDescending(key, SqlOrder);
        }

        return sorted!.ToList();
    }

    private BaseItem? Find(Guid id)
        => RowById(id)?.Item ?? (BaseItem?)(_studios.TryGetValue(id, out var studio) ? studio.Studio : null) ?? Libraries.FirstOrDefault(l => l.Id == id) ?? _others.GetValueOrDefault(id);

    private IEnumerable<string> TagCleans(FakeRow r) => r.Values.Where(v => v.Type == ItemValueType.Tags).Select(v => v.CleanValue);

    private void AddValues(FakeRow row, ItemValueType type, IReadOnlyList<string> values)
    {
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal))
        {
            if (!_values.TryGetValue((type, value), out var stored))
            {
                stored = new FakeItemValue(type, value, Clean(value));
                _values[(type, value)] = stored;
            }

            row.Values.Add(stored);
        }
    }

    private bool Sql(FakeRow r, InternalItemsQuery q)
    {
        static bool Has<T>(IReadOnlyCollection<T> set) => set.Count > 0;
        var userId = q.User?.Id;
        var userRow = userId is { } u ? r.UserData.FirstOrDefault(d => d.UserId == u) : null;

        return (!Has(q.IncludeItemTypes) || q.IncludeItemTypes.Contains(r.Kind))
            && (!Has(q.TopParentIds) || (r.TopParentId is { } top && q.TopParentIds.Contains(top)))
            && (q.IsVirtualItem is not { } isVirtual || r.IsVirtual == isVirtual)
            && (q.IsResumable != true || !r.IsVirtual)
            && (string.IsNullOrWhiteSpace(q.Name) || string.Equals(r.CleanName, Clean(q.Name), StringComparison.Ordinal))
            && NameContains(r, q.NameContains)
            && (q.HasOverview is not { } hasOverview || (hasOverview ? !string.IsNullOrEmpty(r.Item.Overview) : string.IsNullOrEmpty(r.Item.Overview)))
            && (!Has(q.Years) || (r.Item.ProductionYear is { } year && q.Years.Contains(year)))
            && (!Has(q.OfficialRatings) || (r.Item.OfficialRating is { } rating && q.OfficialRatings.Contains(rating, StringComparer.Ordinal)))
            && (!Has(q.Genres) || r.Values.Any(v => v.Type == ItemValueType.Genre && q.Genres.Select(Clean).Contains(v.CleanValue, StringComparer.Ordinal)))
            && (!Has(q.Tags) || r.Values.Any(v => v.Type == ItemValueType.Tags && q.Tags.Select(Clean).Contains(v.CleanValue, StringComparer.Ordinal)))
            && (!Has(q.ExcludeTags) || !r.Values.Any(v => v.Type == ItemValueType.Tags && q.ExcludeTags.Select(Clean).Contains(v.CleanValue, StringComparer.Ordinal)))
            && (!Has(q.StudioIds) || r.Values.Any(v => v.Type == ItemValueType.Studios && q.StudioIds.Any(id => _studios.TryGetValue(id, out var s) && string.Equals(s.CleanName, v.CleanValue, StringComparison.Ordinal))))
            && (!Has(q.AncestorIds) || r.Ancestors.Any(q.AncestorIds.Contains))
            && (q.MinCommunityRating is not { } minRating || (r.CommunityRating is { } f && (double)f >= minRating))
            && (q.MinPremiereDate is not { } minPremiere || (r.PremiereDateUtc is { } p1 && p1 >= minPremiere))
            && (q.MaxPremiereDate is not { } maxPremiere || (r.PremiereDateUtc is { } p2 && p2 <= maxPremiere))
            && (q.MinDateCreated is not { } minCreated || (r.DateCreatedUtc is { } c && c >= minCreated))
            && (q.MinWidth is not { } minWidth || (r.Width is { } w1 && w1 >= minWidth))
            && (q.MaxWidth is not { } maxWidth || (r.Width is { } w2 && w2 <= maxWidth))
            && (q.IsLocked is not { } locked || r.Item.IsLocked == locked)
            && (q.IsPlayed is not { } played || PlayedSql(r, q, userRow) == played)
            && (q.IsFavorite is not { } favorite || (userRow is not null && userRow.IsFavorite == favorite))
            && (q.IsResumable is not { } resumable || (userRow is not null && (resumable ? userRow.PlaybackPositionTicks > 0 : userRow.PlaybackPositionTicks == 0)))
            && (q.HasTmdbId is not { } tmdb || HasKey(r, "tmdb") == tmdb)
            && (q.HasImdbId is not { } imdb || HasKey(r, "imdb") == imdb)
            && (q.HasTvdbId is not { } tvdb || HasKey(r, "tvdb") == tvdb)
            && PassesAccess(r, q);
    }

    // Has<Provider>Id: a provider row whose lower(key) is the name, whatever its value (blank too).
    private static bool HasKey(FakeRow r, string key) => r.Providers.Any(p => string.Equals(AsciiLower(p.Key), key, StringComparison.Ordinal));

    private static bool PlayedSql(FakeRow r, InternalItemsQuery q, FakeUserData? userRow)
    {
        if (q.IncludeItemTypes is [BaseItemKind.Series])
        {
            throw new NotSupportedException("IsPlayed on Series alone is core's \"any episode played\" rule, which the fake doesn't model.");
        }

        return userRow?.Played ?? false;
    }

    private static bool NameContains(FakeRow r, string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        if (term.IndexOfAny(['%', '_', '[', ']', '^']) >= 0)
        {
            var pattern = "%" + term.Trim('%') + "%";
            return Like(r.CleanName, pattern) || Like(r.Item.OriginalTitle, pattern);
        }

        return (r.CleanName ?? string.Empty).Contains(term, StringComparison.Ordinal)
            || (AsciiLower(r.Item.OriginalTitle) ?? string.Empty).Contains(term, StringComparison.Ordinal);
    }
}
