using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// The services over the real <c>userratings.db</c> (<see cref="StoreFixture"/>) and the kit's in-memory library
/// (<see cref="FakeLibrary"/>: <c>GetItemById&lt;BaseItem&gt;(id, user)</c> hides what <c>Hide</c> hid, and
/// <c>GetItemList</c> filters <c>ItemIds</c>, <c>TopParentIds</c> and the user's hidden items, as the kit's
/// <c>LibraryAccess.EligibleIds</c> needs): the libraries Movies and Movies 2, the users admin, viewer and viewer2 (who can't
/// open Movies 2), and the settings as a mutable configuration read per request.
/// </summary>
internal sealed class ServicesFixture : IDisposable
{
    private readonly Dictionary<Guid, CollectionFolder> _topParents = [];
    private readonly Dictionary<Guid, Guid> _topParentOf = [];

    public ServicesFixture()
    {
        Movies = AddLibrary("Movies");
        Movies2 = AddLibrary("Movies 2");
        Admin = NewUser("admin");
        Admin.SetPermission(PermissionKind.IsAdministrator, true);
        Viewer = NewUser("viewer");
        Viewer2 = NewUser("viewer2");
        Users = [Admin, Viewer, Viewer2];

        UserManager.SetupGet(m => m.Users).Returns(() => Users);
        UserData.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((User user, BaseItem item)
            => item.UserData?.Where(e => e.UserId.Equals(user.Id)).Select(r => new UserItemData { Key = r.CustomDataKey, Played = r.Played, IsFavorite = r.IsFavorite, PlayCount = r.PlayCount }).FirstOrDefault()
                ?? new UserItemData { Key = "blank" });
        Library.Mock.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns((BaseItem item) =>
            _topParentOf.TryGetValue(item.Id, out var top) && _topParents.TryGetValue(top, out var folder) ? [folder] : new List<Folder>());

        // A save (the Settings tab's) replaces the configuration the services read, as Plugin.UpdateConfiguration does.
        ConfigurationProvider.Setup(c => c.GetConfiguration()).Returns(() => Configuration);
        ConfigurationProvider.Setup(c => c.Save(It.IsAny<PluginConfiguration>())).Callback((PluginConfiguration saved) => Configuration = saved);
        Settings = new RatingsSettingsReader(ConfigurationProvider.Object);
        Access = new LibraryAccess(Library.Object, user => user == Viewer2 ? [Movies] : [Movies, Movies2], Library.IsVisible);
        ItemAccess = new ItemAccess(Access, Library.Object);
        Played = new PlayedCheck(Library.Object, new FreshUserData(Library.Object, UserData.Object));
        Cache = new SummaryCache(Store.Store);
        Siblings = new CopySiblings(Store.Store, ItemAccess, Cache, Library.Object);

        // The ratings' archive too (empty until a test writes to it), for the lines a removed review takes along.
        Archive = new DetachedArchive(Store.Directory.Combine("archive"), "userratings", Store.Logger, Store.Clock);
        ArchivedLines = new ArchivedLinePurges(Store.Store, Store.Logger, () => Archive);
        Service = new RatingsService(Store.Store, Cache, ItemAccess, Siblings, Played, Settings, UserManager.Object, Library.Object, Store.Clock, archivedLines: ArchivedLines);
    }

    public StoreFixture Store { get; } = new();

    public FakeLibrary Library { get; } = new();

    public Mock<IUserManager> UserManager { get; } = new();

    public Mock<IUserDataManager> UserData { get; } = new();

    public PluginConfiguration Configuration { get; set; } = new();

    public Mock<IPluginConfigurationProvider> ConfigurationProvider { get; } = new();

    public CollectionFolder Movies { get; }

    public CollectionFolder Movies2 { get; }

    public User Admin { get; }

    public User Viewer { get; }

    public User Viewer2 { get; }

    public List<User> Users { get; }

    public RatingsSettingsReader Settings { get; }

    public LibraryAccess Access { get; }

    public ItemAccess ItemAccess { get; }

    public PlayedCheck Played { get; }

    public SummaryCache Cache { get; }

    public CopySiblings Siblings { get; }

    public RatingsService Service { get; }

    public DetachedArchive Archive { get; }

    public ArchivedLinePurges ArchivedLines { get; }

    public static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public static User NewUser(string name)
        => new(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider") { Id = Guid.NewGuid() };

    public Movie AddMovie(CollectionFolder library, string name, string? tmdb = null)
    {
        var movie = tmdb is null
            ? Library.AddMovie(name, 1999, TimeSpan.FromMinutes(136))
            : Library.AddMovie(name, 1999, TimeSpan.FromMinutes(136), null, ("Tmdb", tmdb));
        PlaceIn(movie, library);
        return movie;
    }

    public T Add<T>(CollectionFolder library, T item)
        where T : BaseItem
    {
        Library.Add(item);
        PlaceIn(item, library);
        return item;
    }

    public void Advance(TimeSpan by) => Store.Clock.Advance(by);

    // A service over the store as it is now (after StoreFixture.Reopen, say), with its own cache.
    public RatingsService NewService()
    {
        var cache = new SummaryCache(Store.Store);
        return new RatingsService(Store.Store, cache, ItemAccess, new CopySiblings(Store.Store, ItemAccess, cache, Library.Object), Played, Settings, UserManager.Object, Library.Object, Store.Clock, archivedLines: ArchivedLines);
    }

    public void Dispose()
    {
        Cache.Dispose();
        Store.Dispose();
    }

    private CollectionFolder AddLibrary(string name)
    {
        var folder = Library.Add(new CollectionFolder { Name = name, CollectionType = CollectionType.movies, PhysicalFolderIds = [Guid.NewGuid()] });
        _topParents[folder.PhysicalFolderIds[0]] = folder;
        return folder;
    }

    private void PlaceIn(BaseItem item, CollectionFolder library)
    {
        Library.SetTopParent(item, library.PhysicalFolderIds[0]);
        _topParentOf[item.Id] = library.PhysicalFolderIds[0];

        // Like core: an item in a library the user can't open isn't visible to them.
        if (library == Movies2)
        {
            Library.Hide(Viewer2, item);
        }
    }
}
