using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The collection side: links resolved the way the server does (never looking up an empty id), the collection found by
/// its id or its mark, the difference always taken against the live links, and the writer's one save and one refresh
/// per changed collection.
/// </summary>
public sealed class CollectionWriterTests
{
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Dictionary<string, BaseItem> _byPath = new(StringComparer.Ordinal);
    private readonly List<BoxSet> _boxes = [];
    private readonly List<Guid> _lookedUp = [];
    private readonly List<InternalItemsQuery> _tetherQueries = [];
    private readonly List<int> _idQueries = [];
    private readonly Mock<ILibraryManager> _library = new();
    private readonly RecordingPersistence _persistence = new();
    private readonly LinkResolver _links;
    private readonly CollectionLocator _locator;
    private readonly DirectReplaceWriter _writer;

    public CollectionWriterTests()
    {
        _library.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User?>())).Returns((Guid id, User? _) =>
        {
            _lookedUp.Add(id);
            return _items.GetValueOrDefault(id) ?? _boxes.FirstOrDefault(b => b.Id.Equals(id));
        });
        _library.Setup(l => l.GetItemById<BoxSet>(It.IsAny<Guid>(), It.IsAny<User?>())).Returns((Guid id, User? _) => _boxes.FirstOrDefault(b => b.Id.Equals(id)));
        _library.Setup(l => l.FindByPath(It.IsAny<string>(), It.IsAny<bool?>())).Returns((string path, bool? _) => _byPath.GetValueOrDefault(path));
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) =>
        {
            if (q.HasAnyProviderId is { Count: > 0 } wanted)
            {
                _tetherQueries.Add(q);
                return _boxes.Where(b => wanted.Any(w => b.ProviderIds.TryGetValue(w.Key, out var v) && v == w.Value)).Cast<BaseItem>().ToList();
            }

            return q.ItemIds.Where(_items.ContainsKey).Select(id => _items[id]).ToList();
        });
        _library.Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) =>
        {
            _idQueries.Add(q.ItemIds.Length);
            return q.ItemIds.Where(id => _items.ContainsKey(id) || _boxes.Any(b => b.Id.Equals(id))).ToList();
        });
        var access = new LibraryAccess(_library.Object, _ => []);
        _links = new LinkResolver(_library.Object, access);
        _locator = new CollectionLocator(_library.Object, access);
        _writer = new DirectReplaceWriter(_library.Object, _persistence, _locator, NullLogger.Instance);
    }

#if !JELLYFIN_12
    // Jellyfin 12's links carry the item id alone.
    [Fact]
    public void ALink_IsFoundByItsCachedId_ThenItsPath_ThenItsLibraryItemId_AndAnEmptyIdIsNeverLookedUp()
    {
        var byId = Movie("By id");
        var byPath = Movie("By path", path: "/media/by-path.mkv");
        var series = Item(new MediaBrowser.Controller.Entities.TV.Series { Name = "A series" }, path: "/media/a-series");
        var byLibraryId = Movie("By library id");
        var box = Box(
            new LinkedChild { ItemId = byId.Id },
            new LinkedChild { ItemId = Guid.Empty, Path = byPath.Path },
            new LinkedChild { Path = series.Path },
            new LinkedChild { LibraryItemId = byLibraryId.Id.ToString("N") },
            new LinkedChild { ItemId = Guid.Empty, LibraryItemId = Guid.Empty.ToString("N") });

        var links = _links.Resolve(box);

        Assert.Equal([byId.Id, byPath.Id, series.Id, byLibraryId.Id], links.Members);
        Assert.Equal(1, links.Dead);
        Assert.DoesNotContain(Guid.Empty, _lookedUp);
    }
#endif

    [Fact]
    public void CachedIds_AreCheckedInBatchesOfIdsOnly_AndOnlyAGoneOneIsLookedUpOnItsOwn()
    {
        var movies = Enumerable.Range(0, 1200).Select(i => Movie("M" + i)).ToList();
        var moved = Movie("Moved", path: "/media/moved.mkv");
        var gone = Guid.NewGuid();
        var box = Box([.. movies.Select(m => m.Id).Select(id => new LinkedChild { ItemId = id }), JellyfinLine.Link(gone, moved.Path)]);

        var links = _links.Resolve(box);

        // A gone id is found again by its path on 10.11; Jellyfin 12's links have none, so it's dead.
        Assert.Equal(JellyfinLine.Is12 ? [.. movies.Select(m => m.Id)] : [.. movies.Select(m => m.Id), moved.Id], links.Members);
        Assert.Equal(JellyfinLine.Is12 ? 1 : 0, links.Dead);
        Assert.Equal([LinkResolver.IdsPerQuery, LinkResolver.IdsPerQuery, 201], _idQueries);
        Assert.Equal([gone], _lookedUp);
    }

    [Fact]
    public void ResolvedLinks_CountDeadAndDuplicateLinks_AndHashWhatTheyName()
    {
        var a = Movie("A");
        var box = Box(new LinkedChild { ItemId = a.Id }, new LinkedChild { ItemId = a.Id }, DeadLink("/media/gone.mkv"));

        var links = _links.Resolve(box);
        var again = _links.Resolve(Box(new LinkedChild { ItemId = a.Id }));

        Assert.Equal([a.Id], links.Members);
        Assert.Equal(1, links.Duplicates);
        Assert.Equal(1, links.Dead);
        Assert.StartsWith("sha256:", links.Hash, StringComparison.Ordinal);
        Assert.NotEqual(links.Hash, again.Hash);
    }

    [Fact]
    public void TheStoredId_FindsAMarkedCollection_AndAnUnmarkedOneIsTetherMissing()
    {
        var definition = Definition();
        var marked = Box(definition.Id);
        var unmarked = Box();

        Assert.Equal(CollectionLocationKind.Found, _locator.Locate(definition with { CollectionId = marked.Id }).Kind);
        var missing = _locator.Locate(definition with { CollectionId = unmarked.Id });
        Assert.Equal(CollectionLocationKind.TetherMissing, missing.Kind);
        Assert.False(missing.Writable);
    }

    [Fact]
    public void WithoutAStoredCollection_TheMarkFindsTheOldest_AndNamesTheOthers()
    {
        var definition = Definition();
        var newer = Box(definition.Id);
        newer.DateCreated = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = Box(definition.Id);
        older.DateCreated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Box(Guid.NewGuid());

        var location = _locator.Locate(definition with { CollectionId = Guid.NewGuid() });

        Assert.Equal(CollectionLocationKind.Rebound, location.Kind);
        Assert.Same(older, location.Box);
        Assert.Equal([newer.Id], location.Duplicates);
        Assert.True(location.Writable);
    }

    [Fact]
    public void NoCollection_IsNotCreated_OrOrphanedWhenOneWasStored()
    {
        var definition = Definition();

        Assert.Equal(CollectionLocationKind.NotCreated, _locator.Locate(definition).Kind);
        Assert.Equal(CollectionLocationKind.Orphaned, _locator.Locate(definition with { CollectionId = Guid.NewGuid() }).Kind);
    }

    [Fact]
    public void TheMark_IsLookedUpExactly_InNFormat()
    {
        var definition = Definition();
        Box(definition.Id).ProviderIds[AutoCollectionDefinition.TetherKey] = definition.Id.ToString("D");
        var upper = Box();
        upper.ProviderIds = new Dictionary<string, string> { [AutoCollectionDefinition.TetherKey] = definition.Tether.ToUpperInvariant() };

        var hits = _locator.FindByTether(definition.Id);

        Assert.Empty(hits);
        var query = Assert.Single(_tetherQueries);
        Assert.NotNull(query.HasAnyProviderId);
        Assert.Equal(definition.Id.ToString("N"), query.HasAnyProviderId[AutoCollectionDefinition.TetherKey]);
        Assert.Equal([BaseItemKind.BoxSet], query.IncludeItemTypes);
    }

    [Fact]
    public void TheDifference_IsTakenAgainstTheLiveLinks()
    {
        var kept = Movie("Kept");
        var gone = Movie("Gone");
        var added = Movie("Added");
        var dropped = Movie("Dropped by the server");
        var definition = Definition();
        var box = Marked(definition, new LinkedChild { ItemId = kept.Id }, new LinkedChild { ItemId = gone.Id }, DeadLink("/media/dead.mkv"));

        var diff = CollectionDiff.Compute(box, _links.Resolve(box), [kept.Id, added.Id, dropped.Id], definition);

        Assert.Equal([added.Id, dropped.Id], diff.Added);
        Assert.Equal([gone.Id], diff.Removed);
        Assert.Equal(1, diff.Dead);
        Assert.Empty(diff.Presentation);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void TheSameMembers_AreNoChange_AndOrderCountsOnlyForTheQueryOrder()
    {
        var a = Movie("A");
        var b = Movie("B");
        var byDate = Definition();
        var byQuery = byDate with { Presentation = new AutoCollectionPresentation { SortMode = SmartSearchSortMode.QueryOrder } };
        var box = Marked(byDate, new LinkedChild { ItemId = a.Id }, new LinkedChild { ItemId = b.Id });

        Assert.True(CollectionDiff.Compute(box, _links.Resolve(box), [b.Id, a.Id], byDate).IsEmpty);
        box.DisplayOrder = "Default";
        var reordered = CollectionDiff.Compute(box, _links.Resolve(box), [b.Id, a.Id], byQuery);
        Assert.True(reordered.OrderChanged);
        Assert.Empty(reordered.Added);
        Assert.True(CollectionDiff.Compute(box, _links.Resolve(box), [a.Id, b.Id], byQuery).IsEmpty);
    }

    [Fact]
    public void APresentationChange_IsAChange_AndAnUnlockedCollectionsOrderIsNeverCompared()
    {
        var a = Movie("A");
        var definition = Definition();
        var box = Marked(definition, new LinkedChild { ItemId = a.Id });
        var links = _links.Resolve(box);

        Assert.Equal([nameof(BoxSet.Name)], CollectionDiff.Compute(box, links, [a.Id], definition with { Name = "Renamed" }).Presentation);
        Assert.Equal([nameof(BoxSet.Overview)], CollectionDiff.Compute(box, links, [a.Id], definition with { Presentation = new AutoCollectionPresentation { OverviewMode = SmartSearchOverviewMode.Managed, Overview = "New" } }).Presentation);

        var unlocked = definition with { Presentation = new AutoCollectionPresentation { Lock = false, SortMode = SmartSearchSortMode.SortName } };
        box.IsLocked = false;
        box.DisplayOrder = "Whatever the refresh left";
        box.LockedFields = [MetadataField.Name];
        Assert.True(CollectionDiff.Compute(box, links, [a.Id], unlocked).IsEmpty);
        Assert.Equal("PremiereDate", CollectionDiff.DisplayOrderOf(unlocked.Presentation));
        Assert.Equal("Default", CollectionDiff.DisplayOrderOf(new AutoCollectionPresentation { SortMode = SmartSearchSortMode.QueryOrder }));
        Assert.Equal("SortName", CollectionDiff.DisplayOrderOf(new AutoCollectionPresentation { SortMode = SmartSearchSortMode.SortName }));
    }

    [Fact]
    public async Task TheWriter_KeepsExistingLinks_PrunesDeadOnes_AndSavesAndRefreshesOnce()
    {
        var kept = Movie("Kept", path: "/media/kept.mkv");
        var added = Movie("Added");
        var definition = Definition() with { Name = "Renamed", Presentation = new AutoCollectionPresentation { SortMode = SmartSearchSortMode.QueryOrder } };
        var keptLink = ByPath(kept);
        var box = Marked(Definition(definition.Id), keptLink, DeadLink("/media/dead.mkv"));

        var written = await _writer.WriteAsync(box, _links.Resolve(box), [added.Id, kept.Id, Guid.NewGuid()], definition, CancellationToken.None);

        Assert.Equal([added.Id, kept.Id], written);
        Assert.Equal(2, box.LinkedChildren.Length);
        Assert.Equal(added.Id, box.LinkedChildren[0].ItemId);
        Assert.Same(keptLink, box.LinkedChildren[1]);
        Assert.Equal(kept.Id, keptLink.ItemId);
        Assert.Equal("Default", box.DisplayOrder);
        Assert.Equal("Renamed", box.Name);
        Assert.True(box.IsLocked);
        Assert.Equal(["Rating", "Save", "Refresh"], _persistence.Calls);
        Assert.True(CollectionDiff.Compute(box, _links.Resolve(box), [added.Id, kept.Id], definition).IsEmpty);
    }

    [Fact]
    public async Task AnUnlockedCollection_GetsTheReleaseDateOrder_AndItsNameLocked()
    {
        var a = Movie("A");
        var definition = Definition() with { Presentation = new AutoCollectionPresentation { Lock = false, SortMode = SmartSearchSortMode.QueryOrder } };
        var box = Marked(definition);

        await _writer.WriteAsync(box, _links.Resolve(box), [a.Id], definition, CancellationToken.None);

        Assert.False(box.IsLocked);
        Assert.Equal("PremiereDate", box.DisplayOrder);
        Assert.Contains(MetadataField.Name, box.LockedFields);
    }

    [Fact]
    public async Task ACreate_MarksTheCollection_SetsItsPresentation_AndSavesOnce()
    {
        var a = Movie("A");
        var definition = Definition() with { Presentation = new AutoCollectionPresentation { SortMode = SmartSearchSortMode.SortName } };

        var box = await _writer.CreateAsync(definition, [a.Id, Guid.NewGuid()], CancellationToken.None);

        Assert.True(CollectionLocator.HasTether(box, definition.Id));
        Assert.Equal("SortName", box.DisplayOrder);
        Assert.Equal([a.Id], box.LinkedChildren.Select(l => l.ItemId!.Value));
        Assert.Equal(["Create", "Save"], _persistence.Calls);
    }

    [Fact]
    public async Task ACreateThatFailedAfterMakingTheCollection_UsesIt_AndOneThatMadeNothingThrows()
    {
        var a = Movie("A");
        var definition = Definition();
        _persistence.CreateThrowsAfterMaking = true;

        var box = await CreateTrackedAsync(definition, [a.Id]);

        Assert.Same(_persistence.Boxes.Single(), box);
        Assert.Equal(["Create", "Save"], _persistence.Calls);

        _persistence.CreateThrowsAfterMaking = false;
        _persistence.CreateThrows = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateTrackedAsync(Definition(), [a.Id]));
    }

    [Fact]
    public async Task ADetach_RemovesOnlyTheMark_AndSavesOnce_AndAnUnmarkedCollectionIsLeftAlone()
    {
        var definition = Definition();
        var box = Box(definition.Id);
        box.ProviderIds["Tmdb"] = "123";
        box.IsLocked = true;

        Assert.True(await _writer.DetachAsync(box, definition.Id, CancellationToken.None));
        Assert.False(await _writer.DetachAsync(box, definition.Id, CancellationToken.None));

        Assert.False(box.ProviderIds.ContainsKey(AutoCollectionDefinition.TetherKey));
        Assert.Equal("123", box.ProviderIds["Tmdb"]);
        Assert.True(box.IsLocked);
        Assert.Equal(["Save"], _persistence.Calls);
    }

    [Fact]
    public async Task AFailedSave_PutsTheCollectionBackAsItWas()
    {
        var kept = Movie("Kept", path: "/media/kept.mkv");
        var added = Movie("Added");
        var definition = Definition() with { Name = "Renamed", Presentation = new AutoCollectionPresentation { SortMode = SmartSearchSortMode.SortName, OverviewMode = SmartSearchOverviewMode.Managed, Overview = "New" } };
        var keptLink = ByPath(kept);
        var box = Marked(Definition(definition.Id), keptLink);
        box.Overview = "Old";
        box.DisplayOrder = "PremiereDate";
        box.OfficialRating = "PG";
        var links = box.LinkedChildren;
        _persistence.SaveFails = _ => true;

        await Assert.ThrowsAsync<System.IO.IOException>(() => _writer.WriteAsync(box, _links.Resolve(box), [added.Id, kept.Id], definition, CancellationToken.None));

        Assert.Same(links, box.LinkedChildren);
        Assert.Equal(JellyfinLine.Is12 ? kept.Id : Guid.Empty, keptLink.ItemId);
        Assert.Equal("Horror", box.Name);
        Assert.Equal("Old", box.Overview);
        Assert.Equal("PremiereDate", box.DisplayOrder);
        Assert.Equal("PG", box.OfficialRating);
        Assert.DoesNotContain("Refresh", _persistence.Calls);

        // So the next run still sees the difference.
        Assert.False(CollectionDiff.Compute(box, _links.Resolve(box), [added.Id, kept.Id], definition).IsEmpty);
    }

    [Fact]
    public async Task ACancelledWrite_ChangesNothing()
    {
        var a = Movie("A");
        var definition = Definition() with { Name = "Renamed" };
        var box = Marked(Definition(definition.Id));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _writer.WriteAsync(box, _links.Resolve(box), [a.Id], definition, cancelled.Token));

        Assert.Empty(box.LinkedChildren);
        Assert.Equal("Horror", box.Name);
        Assert.Empty(_persistence.Calls);
    }

    [Fact]
    public async Task ACreateWhoseSaveFails_PutsTheNewCollectionsValuesBack_AndAFailedDetachKeepsTheMark()
    {
        var a = Movie("A");
        var definition = Definition() with { Presentation = new AutoCollectionPresentation { SortMode = SmartSearchSortMode.SortName } };
        _persistence.SaveFails = _ => true;

        await Assert.ThrowsAsync<System.IO.IOException>(() => CreateTrackedAsync(definition, [a.Id]));

        var made = _persistence.Boxes.Single();
        Assert.NotEqual("SortName", made.DisplayOrder);
        Assert.True(CollectionLocator.HasTether(made, definition.Id));
        await Assert.ThrowsAsync<System.IO.IOException>(() => _writer.DetachAsync(made, definition.Id, CancellationToken.None));
        Assert.True(CollectionLocator.HasTether(made, definition.Id));
    }

    [Fact]
    public async Task ACreate_ChecksAgainJustBeforeTheServerCreates_AndARefusalMakesNothing()
    {
        var a = Movie("A");
        var order = new List<string>();
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds.Length > 0))).Returns((InternalItemsQuery q) =>
        {
            order.Add("Load");
            return q.ItemIds.Where(_items.ContainsKey).Select(id => _items[id]).ToList();
        });

        await _writer.CreateAsync(Definition(), [a.Id], CancellationToken.None, () =>
        {
            order.Add("Check");
            return Task.CompletedTask;
        });
        var refused = await Assert.ThrowsAsync<Jellyfin.Plugin.Kit.Api.PluginException>(() => _writer.CreateAsync(Definition(), [a.Id], CancellationToken.None, () => throw AutoCollectionErrors.NameTaken(new CollectionNameCheck("Horror", null, [], [], [], null, null))));

        Assert.Equal(["Load", "Check", "Load"], order);
        Assert.Equal(AutoCollectionReasons.NameTaken, refused.Code);
        Assert.Equal(1, _persistence.Creates);
    }

    [Fact]
    public void TheRefreshAfterAWrite_SavesWithoutAnyMetadataOrImageProvider()
    {
        var options = CollectionPersistence.RefreshOptions(new Mock<MediaBrowser.Model.IO.IFileSystem>().Object);

        Assert.True(options.ForceSave);
        Assert.Equal(MediaBrowser.Controller.Providers.MetadataRefreshMode.None, options.MetadataRefreshMode);
        Assert.Equal(MediaBrowser.Controller.Providers.MetadataRefreshMode.None, options.ImageRefreshMode);
        Assert.False(options.ReplaceAllMetadata);
        Assert.False(options.ReplaceAllImages);
    }

    private static AutoCollectionDefinition Definition(Guid? id = null) =>AutoCollectionTestData.Definition(Guid.NewGuid(), Guid.NewGuid(), "Horror", id);

    // A create through the writer, with what the persistence made visible to the mark lookup.
    private async Task<BoxSet> CreateTrackedAsync(AutoCollectionDefinition definition, IReadOnlyList<Guid> ids)
    {
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.HasAnyProviderId != null && q.HasAnyProviderId.Count > 0))).Returns((InternalItemsQuery q) =>
            _persistence.Boxes.Where(b => CollectionLocator.HasTether(b, definition.Id)).Cast<BaseItem>().ToList());
        return await _writer.CreateAsync(definition, ids, CancellationToken.None);
    }

    // A link to no item: by a path no item has on 10.11, by an id no item has on 12 (whose links carry the id alone).
    private static LinkedChild DeadLink(string path) => JellyfinLine.Is12 ? JellyfinLine.Link(Guid.NewGuid()) : JellyfinLine.Link(null, path);

    // A link a scan wrote: by path with no cached id on 10.11, by id on 12.
    private static LinkedChild ByPath(BaseItem item) => JellyfinLine.Is12 ? JellyfinLine.Link(item.Id) : JellyfinLine.Link(Guid.Empty, item.Path);

    private Movie Movie(string name, string? path = null) => Item(new Movie { Name = name }, path);

    private T Item<T>(T item, string? path = null)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        item.Path = path;
        _items[item.Id] = item;
        if (path is not null)
        {
            _byPath[path] = item;
        }

        return item;
    }

    private BoxSet Box(params LinkedChild[] links)
    {
        var box = new BoxSet { Id = Guid.NewGuid(), Name = "Horror", LinkedChildren = links, ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
        _boxes.Add(box);
        return box;
    }

    private BoxSet Box(Guid definitionId)
    {
        var box = Box();
        box.ProviderIds[AutoCollectionDefinition.TetherKey] = AutoCollectionDefinition.FormatTether(definitionId);
        return box;
    }

    // A marked collection already showing the definition's presentation.
    private BoxSet Marked(AutoCollectionDefinition definition, params LinkedChild[] links)
    {
        var box = Box(definition.Id);
        box.LinkedChildren = links;
        box.Name = definition.Name;
        box.IsLocked = definition.Presentation.Lock;
        box.DisplayOrder = CollectionDiff.DisplayOrderOf(definition.Presentation);
        return box;
    }
}
