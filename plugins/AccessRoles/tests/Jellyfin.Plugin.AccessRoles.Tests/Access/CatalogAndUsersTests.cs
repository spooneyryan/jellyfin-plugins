using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Access;

/// <summary>
/// The resolver's inputs read from the server: the library catalog from <c>GetVirtualFolders()</c> with the id
/// the core gives each library folder (as observed on 10.11.5), and the users' native access from the user manager's objects.
/// </summary>
public sealed class CatalogAndUsersTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    [Fact]
    public void TheCatalog_IsGetVirtualFolders_WithEachIdFromGetNewItemIdOfTheLibrarysFolder_NotItsItemId()
    {
        var root = _directory.Combine("root", "default");
        Directory.CreateDirectory(Path.Combine(root, "Movies"));
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.DefaultUserViewsPath).Returns(root);
        var movies = Guid.NewGuid();
        var kids = Guid.NewGuid();
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Movies", Locations = [@"C:\Media\Movies"], CollectionType = CollectionTypeOptions.movies, ItemId = Guid.NewGuid().ToString("N"), LibraryOptions = new LibraryOptions { Enabled = true } },
            new VirtualFolderInfo { Name = "Kids", Locations = [@"C:\Media\Kids"], ItemId = null, LibraryOptions = new LibraryOptions { Enabled = false } },
            new VirtualFolderInfo { Name = string.Empty }
        ]);
        library.Setup(l => l.GetNewItemId(Path.Combine(root, "Movies"), typeof(CollectionFolder))).Returns(movies);
        library.Setup(l => l.GetNewItemId(Path.Combine(root, "Kids"), typeof(CollectionFolder))).Returns(kids);

        var catalog = new VirtualFolderCatalogSource(library.Object, paths.Object, NullLogger.Instance).Read();

        Assert.Equal(["Kids", "Movies"], catalog.Libraries.Select(l => l.Name));
        var read = catalog.Find(movies)!;
        Assert.Equal([@"C:\Media\Movies"], read.Locations);
        Assert.Equal((true, "movies"), (read.Enabled, read.CollectionType));
        Assert.NotNull(read.CreatedUtc);
        var disabled = catalog.Find(kids)!;
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.CreatedUtc);
        Assert.Null(disabled.CollectionType);

        // The "folder without item" state (seen on 10.11.5): listed, with its id, but no ItemId yet.
        Assert.True(read.HasItem);
        Assert.False(disabled.HasItem);
        Assert.Equal(kids, new VirtualFolderCatalogSource(library.Object, paths.Object, NullLogger.Instance).IdOf("Kids"));
        Assert.Equal(Guid.Empty, new VirtualFolderCatalogSource(library.Object, paths.Object, NullLogger.Instance).IdOf(string.Empty));
    }

    [Fact]
    public void TheCatalog_ExpandsNativeAccess_AndOrdersIdsByName()
    {
        var catalog = TestSupport.AccessFixtures.Catalog;
        var gone = Guid.NewGuid();

        Assert.Equal(5, catalog.Expand(true, []).Count);
        Assert.Equal([TestSupport.AccessFixtures.Kids.Id], catalog.Expand(false, [TestSupport.AccessFixtures.Kids.Id, gone]));
        Assert.Equal([TestSupport.AccessFixtures.Kids.Id, TestSupport.AccessFixtures.Movies.Id, gone], catalog.Order([gone, TestSupport.AccessFixtures.Movies.Id, TestSupport.AccessFixtures.Kids.Id]));
    }

    [Fact]
    public void ServerUsers_AreReadFromTheUserManagersObjects_ThroughKitUsers()
    {
        var kid = new User("kid", "a", "b");
        kid.SetPermission(PermissionKind.EnableAllFolders, false);
        kid.SetPermission(PermissionKind.EnableLiveTvManagement, true);
        kid.SetPermission(PermissionKind.IsHidden, true);
        var folder = Guid.NewGuid();
        kid.SetPreference(PreferenceKind.EnabledFolders, [folder]);
        kid.SetPreference(PreferenceKind.BlockedMediaFolders, [Guid.NewGuid()]);
        kid.EnableUserPreferenceAccess = false;
        var admin = new User("admin", "a", "b");
        admin.SetPermission(PermissionKind.IsAdministrator, true);
        admin.SetPermission(PermissionKind.EnableAllFolders, true);
        var manager = new Mock<IUserManager>();
        manager.SetupGet(m => m.Users).Returns([kid, admin]);
        manager.Setup(m => m.GetUserById(kid.Id)).Returns(kid);

        var users = new JellyfinServerUsers(manager.Object);
        var all = users.GetAll();
        var found = users.Find(kid.Id)!;

        Assert.Equal(["admin", "kid"], all.Select(u => u.Name));
        Assert.True(all[0].IsAdministrator);
        Assert.True(all[0].Native.EnableAllFolders);
        Assert.False(found.Native.EnableAllFolders);
        Assert.Equal([folder], found.Native.EnabledFolders);
        Assert.False(found.Native.EnableUserPreferenceAccess);
        Assert.True(found.Native.EnableLiveTvManagement);
        Assert.Single(found.Native.BlockedMediaFolders);
        Assert.True(found.IsHidden);
        Assert.Null(users.Find(Guid.NewGuid()));
        Assert.Null(users.Find(Guid.Empty));
        manager.Verify(m => m.UpdateUserAsync(It.IsAny<User>()), Times.Never);
    }

    public void Dispose() => _directory.Dispose();
}
