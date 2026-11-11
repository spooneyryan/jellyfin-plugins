using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.AutoCollections.AutoCollectionTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The name check: the folder and id a new collection would get come from its name, and the server checks neither, so
/// every way two collections could end up on one folder or one id is a conflict, compared ignoring case.
/// </summary>
public sealed class CollectionNameCheckerTests : IDisposable
{
    private readonly TempDirectory _folder = new();
    private readonly List<BaseItem> _items = [];
    private readonly List<VirtualFolderInfo> _libraries = [];
    private readonly Mock<ILibraryManager> _library = new();
    private readonly AutoCollectionStore _store;
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(Created));

    public CollectionNameCheckerTests()
    {
        Directory.CreateDirectory(Root);
        _store = Store(_folder.Combine("data"));
        _library.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns((string key, Type type) => NewId(key, type));
        _library.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.FirstOrDefault(i => i.Id.Equals(id)));
        _library.Setup(l => l.GetVirtualFolders()).Returns(() => _libraries);
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) =>
            _items.Where(i => q.IncludeItemTypes.Contains(i.GetBaseItemKind()))
                .Where(i => q.AncestorIds.Length == 0 || q.AncestorIds.Contains(i.ParentId))
                .Skip(q.StartIndex ?? 0)
                .Take(q.Limit ?? int.MaxValue)
                .ToList());
    }

    private string Root => _folder.Combine("collections");

    public void Dispose()
    {
        _store.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task AFreeName_IsAvailable_WithTheServersFolder()
    {
        var check = await CheckAsync("Horror 80s");

        Assert.True(check.Available);
        Assert.Empty(check.Suggestions);
        Assert.Equal(Path.Combine(Root, "Horror 80s [boxset]"), check.FolderPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::??")]
    public async Task ANameWithNothingUsable_IsAnError(string name)
    {
        var check = await CheckAsync(name);

        Assert.False(check.Available);
        Assert.NotNull(check.Error);
    }

    [Fact]
    public async Task ANameOver100Characters_IsAnError_AndOneOf100IsFine()
    {
        Assert.NotNull((await CheckAsync(new string('a', 101))).Error);
        Assert.True((await CheckAsync(new string('a', 100))).Available);
    }

    [Fact]
    public async Task TheName_IsTrimmed()
    {
        var check = await CheckAsync("  Horror 80s  ");

        Assert.Equal("Horror 80s", check.Name);
    }

    [Fact]
    public async Task ACollectionOfTheSameName_IgnoringCase_IsAConflict()
    {
        var box = AddBoxSet("HORROR 80S", Path.Combine(Root, "elsewhere [boxset]"));

        var check = await CheckAsync("Horror 80s");

        var conflict = Assert.Single(check.Conflicts);
        Assert.Equal(CollectionNameConflictKind.ExistingCollection, conflict.Kind);
        Assert.Equal(box.Id, conflict.Id);
        Assert.Equal(["Horror 80s (2)", "Horror 80s (Auto)"], check.Suggestions);
    }

    [Fact]
    public async Task ACollectionOnTheSameFolder_IgnoringCase_IsAConflict_WhateverItsName()
    {
        // The folder kept its first name after a rename; the server would put a new "horror 80s" on it.
        Directory.CreateDirectory(Path.Combine(Root, "Horror 80s [boxset]"));
        var box = AddBoxSet("Scary 80s", Path.Combine(Root, "Horror 80s [boxset]"));

        var check = await CheckAsync("horror 80s");

        var conflict = Assert.Single(check.Conflicts);
        Assert.Equal(CollectionNameConflictKind.ExistingCollection, conflict.Kind);
        Assert.Equal(box.Id, conflict.Id);
    }

    [Fact]
    public async Task AFolderWithNoCollection_IsAConflict()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Horror 80s [boxset]"));

        var check = await CheckAsync("Horror 80s");

        var conflict = Assert.Single(check.Conflicts);
        Assert.Equal(CollectionNameConflictKind.FolderExists, conflict.Kind);
        Assert.Equal("Horror 80s [boxset]", conflict.Name);
    }

    [Fact]
    public async Task AFileOfTheFoldersName_IsAConflict_AndGetsSuggestions()
    {
        // Not a folder: the server's create would fail on it (a 500 before this check), so the name is taken.
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Combine(Root, "Horror 80s [boxset]"), "not a folder", TestContext.Current.CancellationToken);

        var check = await CheckAsync("Horror 80s");

        Assert.False(check.Available);
        var conflict = Assert.Single(check.Conflicts);
        Assert.Equal(CollectionNameConflictKind.FolderExists, conflict.Kind);
        Assert.Equal("Horror 80s [boxset]", conflict.Name);
        Assert.Contains("Horror 80s (2)", check.Suggestions);
    }

    [Fact]
    public async Task NamesTheFileSystemMapsToOneFolder_Conflict()
    {
        // The stub maps ':' and '?' as the server's file system does: "Alien: Isolation?" and "Alien Isolation" share a folder.
        AddBoxSet("Alien Isolation", Path.Combine(Root, "Alien Isolation [boxset]"));

        var check = await CheckAsync("Alien: Isolation?");

        Assert.Equal(Path.Combine(Root, "Alien Isolation [boxset]"), check.FolderPath);
        Assert.Equal(CollectionNameConflictKind.ExistingCollection, Assert.Single(check.Conflicts).Kind);
    }

    [Fact]
    public async Task AnotherItemOnTheNewCollectionsId_IsAConflict()
    {
        var path = Path.Combine(Root, "Horror 80s [boxset]");
        _items.Add(new Movie { Id = NewId(path, typeof(BoxSet)), Name = "Odd movie" });

        var check = await CheckAsync("Horror 80s");

        var conflict = Assert.Single(check.Conflicts);
        Assert.Equal(CollectionNameConflictKind.ItemIdExists, conflict.Kind);
        Assert.Equal("Odd movie", conflict.Name);
    }

    [Fact]
    public async Task TheDefinitionsOwnCollection_IsARebind_NotAConflict()
    {
        var definition = Guid.NewGuid();
        var path = Path.Combine(Root, "Horror 80s [boxset]");
        Directory.CreateDirectory(path);
        var box = AddBoxSet("Horror 80s", path, AutoCollectionDefinition.FormatTether(definition));

        var mine = await CheckAsync("horror 80s", definition);
        var someoneElses = await CheckAsync("horror 80s", Guid.NewGuid());

        Assert.True(mine.Available);
        Assert.Equal(box.Id, mine.Rebind);
        Assert.False(someoneElses.Available);
        Assert.Null(someoneElses.Rebind);
    }

    [Fact]
    public async Task TheMark_IsMatchedExactly_InNFormat()
    {
        var definition = Guid.NewGuid();
        AddBoxSet("Horror 80s", Path.Combine(Root, "Horror 80s [boxset]"), definition.ToString("D"));

        Assert.False((await CheckAsync("Horror 80s", definition)).Available);
    }

    [Fact]
    public async Task AnotherDefinitionWithNoCollectionYet_HoldsItsName()
    {
        var other = Definition(Guid.NewGuid(), Guid.NewGuid(), "Horror 80s");
        await _store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [other]), CancellationToken.None);

        var check = await CheckAsync("HORROR 80s");
        var itself = await CheckAsync("Horror 80s", other.Id);

        var conflict = Assert.Single(check.Conflicts);
        Assert.Equal(CollectionNameConflictKind.AutoCollection, conflict.Kind);
        Assert.Equal(other.Id, conflict.Id);
        Assert.True(itself.Available);
    }

    [Fact]
    public async Task Suggestions_SkipTakenNames_AndStayWithin100Characters()
    {
        AddBoxSet("Horror 80s", Path.Combine(Root, "Horror 80s [boxset]"));
        AddBoxSet("Horror 80s (2)", Path.Combine(Root, "Horror 80s (2) [boxset]"));
        Directory.CreateDirectory(Path.Combine(Root, "Horror 80s (3) [boxset]"));
        AddBoxSet("horror 80s (auto)", Path.Combine(Root, "x [boxset]"));
        var longName = new string('n', 100);
        AddBoxSet(longName, Path.Combine(Root, longName + " [boxset]"));

        Assert.Equal(["Horror 80s (4)"], (await CheckAsync("Horror 80s")).Suggestions);
        Assert.All((await CheckAsync(longName)).Suggestions, s => Assert.True(s.Length <= CollectionNameChecker.MaxNameLength, s));
    }

    [Fact]
    public async Task TheTmdbNameWarning_ComesOnlyFromLibrariesThatAddToCollections()
    {
        var off = AddLibrary(automaticallyAdd: false);
        _items.Add(new Movie { Id = Guid.NewGuid(), Name = "A", ParentId = off, CollectionName = "Alien Collection" });

        Assert.Empty((await CheckAsync("Alien Collection")).Warnings);

        var on = AddLibrary(automaticallyAdd: true);
        _items.Add(new Movie { Id = Guid.NewGuid(), Name = "B", ParentId = on, CollectionName = "Alien Collection" });
        _time.Advance(TimeSpan.FromMinutes(6));

        var check = await CheckAsync("Alien Collection");
        Assert.True(check.Available);
        Assert.Contains("Alien Collection", Assert.Single(check.Warnings), StringComparison.Ordinal);

        // The server matches the name exactly.
        Assert.Empty((await CheckAsync("alien collection")).Warnings);
    }

    [Fact]
    public async Task WithNoCollectionsFolderYet_TheServersDataFolderIsUsed()
    {
        var check = await CheckAsync("Horror 80s", folder: null);

        Assert.Equal(Path.Combine(_folder.Combine("server-data"), "collections", "Horror 80s [boxset]"), check.FolderPath);
    }

    [Fact]
    public async Task TheService_RefusesNonManagers_AndANameThatIsntOne()
    {
        var viewer = Planning.PlannerWorld.NewUser("viewer");
        var refused = Assert.Throws<Kit.Api.PluginException>(() => AutoCollectionService.RequireManager(viewer, false));
        Assert.Equal(AutoCollectionErrors.ManagerRequiredCode, refused.Code);
        AutoCollectionService.RequireManager(viewer, true);
        viewer.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.EnableCollectionManagement, true);
        AutoCollectionService.RequireManager(viewer, false);

        var error = Assert.Throws<Kit.Api.PluginException>(() => AutoCollectionService.ToDto(new CollectionNameCheck(string.Empty, "empty", [], [], [], null, null)));
        Assert.Equal(Kit.Api.PluginErrorKind.Invalid, error.Kind);

        AddBoxSet("Horror 80s", Path.Combine(Root, "Horror 80s [boxset]"));
        var dto = AutoCollectionService.ToDto(await CheckAsync("Horror 80s"));
        Assert.False(dto.Available);
        Assert.Equal("ExistingCollection", Assert.Single(dto.Conflicts).Kind);
        Assert.Contains("Horror 80s (2)", dto.Suggestions);
    }

    [Theory]
    [InlineData("{\"Name\":\"Horror\",\"Extra\":1}")]
    [InlineData("{\"DefinitionId\":null}")]
    [InlineData("{\"Name\":3}")]
    [InlineData("{\"Name\":\"Horror\",\"DefinitionId\":\"nope\"}")]
    public void TheBody_IsReadStrictly(string body)
    {
        var error = Assert.Throws<Kit.Api.PluginException>(() => Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.ReadCheckName(Encoding.UTF8.GetBytes(body)));

        Assert.Equal(Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.InvalidRequestCode, error.Code);
    }

    [Fact]
    public void TheBody_NamesAndDefinitionId_MatchIgnoringCase()
    {
        var id = Guid.NewGuid();

        var request = Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.ReadCheckName(Encoding.UTF8.GetBytes("{\"name\":\" Horror \",\"definitionid\":\"" + id + "\"}"));

        Assert.Equal(" Horror ", request.Name);
        Assert.Equal(id, request.DefinitionId);
    }

    private static Guid NewId(string key, Type type)
        => new(MD5.HashData(Encoding.Unicode.GetBytes(type.FullName + key)));

    private Task<CollectionNameCheck> CheckAsync(string name, Guid? definitionId = null, string? folder = "")
        => Checker(folder == string.Empty ? Root : folder).CheckAsync(name, definitionId, CancellationToken.None);

    private CollectionNameChecker Checker(string? collectionsFolder)
    {
        var collections = new Mock<ICollectionManager>();
        collections.Setup(c => c.GetCollectionsFolder(false)).ReturnsAsync(collectionsFolder is null ? null : new Folder { Path = collectionsFolder });
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns((string n) => new string(n.Where(c => c is not (':' or '?')).ToArray()).Trim());
        var paths = new Mock<IServerApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_folder.Combine("server-data"));
        return new CollectionNameChecker(_library.Object, collections.Object, fileSystem.Object, paths.Object, _store, _time);
    }

    private BoxSet AddBoxSet(string name, string path, string? tether = null)
    {
        var box = new BoxSet { Id = NewId(path, typeof(BoxSet)), Name = name, Path = path };
        if (tether is not null)
        {
            box.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [AutoCollectionDefinition.TetherKey] = tether };
        }

        _items.Add(box);
        return box;
    }

    private Guid AddLibrary(bool automaticallyAdd)
    {
        var id = Guid.NewGuid();
        _libraries.Add(new VirtualFolderInfo { ItemId = id.ToString("N"), Name = "Movies", LibraryOptions = new LibraryOptions { AutomaticallyAddToCollection = automaticallyAdd } });
        return id;
    }
}
