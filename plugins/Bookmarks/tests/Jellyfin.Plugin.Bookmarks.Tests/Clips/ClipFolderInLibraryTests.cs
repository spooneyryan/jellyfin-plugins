using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// Bookmarks 1.2.1: the warning that a clip folder lies in a Movies or TV library (the docs say never to use one), for the
/// settings page's check as the admin types and for Diagnostics' <c>ClipStorage</c>: a library's own folder or one inside it,
/// on whole path segments; the library named only for an admin who may open it; Home Videos and other types left alone.
/// </summary>
public sealed class ClipFolderInLibraryTests : IDisposable
{
    private static readonly Guid MoviesId = Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
    private static readonly Guid ShowsId = Guid.Parse("a3c1d2b4e5f60718293a4b5c6d7e8f90");

    private readonly TempDirectory _directory = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly User _admin = TestData.CreateUser("admin");
    private readonly List<Guid> _open = [MoviesId, ShowsId];
    private readonly ClipFolderInLibrary _check;

    public ClipFolderInLibraryTests()
    {
        _library.Setup(m => m.GetVirtualFolders()).Returns(() =>
        [
            new VirtualFolderInfo { Name = "Films", Locations = [Folder("media", "films")], CollectionType = CollectionTypeOptions.movies, ItemId = MoviesId.ToString("N") },
            new VirtualFolderInfo { Name = "Shows", Locations = [Folder("media", "shows"), Folder("overflow", "shows")], CollectionType = CollectionTypeOptions.tvshows, ItemId = ShowsId.ToString("N") },
            new VirtualFolderInfo { Name = "Clips", Locations = [Folder("media", "clips")], CollectionType = CollectionTypeOptions.homevideos, ItemId = Guid.NewGuid().ToString("N") },
            new VirtualFolderInfo { Name = "Mixed", Locations = [Folder("media", "mixed")], CollectionType = null, ItemId = Guid.NewGuid().ToString("N") }
        ]);
        _users.Setup(m => m.GetUserById(_admin.Id)).Returns(_admin);
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.SetupGet(p => p.DataPath).Returns(_directory.Combine("data"));
        _check = new ClipFolderInLibrary(
            new ClipLibraryLocations(_library.Object, NullLogger.Instance, new ManualTimeProvider(DateTimeOffset.UtcNow)),
            new LibraryAccess(_library.Object, _ => _open.Select(id => new CollectionFolder { Id = id }).ToList()),
            _users.Object,
            new BookmarkPaths(new KitPaths(applicationPaths.Object, BookmarksKit.Options)),
            NullLogger.Instance);
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void AFolderInAMoviesOrTvLibrary_IsWarnedOf_NamingTheLibraryForAnAdminWhoMayOpenIt()
    {
        var films = Assert.Single(Warnings(_check.Check(Folder("media", "films", "clips"), _admin.Id)));
        Assert.Equal(ClipFolderInLibrary.WarningCode, films["Code"]);
        Assert.Equal(
            "This folder is in the Movies library \"Films\": Jellyfin takes clips there for the films they come from and shows them as duplicates. Choose a folder of its own outside every Movies and TV library, or leave the field empty for the default folder.",
            films["Message"]);

        // The library's own folder, and the second folder of a library of two, in another case and with a trailing separator.
        Assert.Single(Warnings(_check.Check(Folder("media", "films"), _admin.Id)));
        var shows = Assert.Single(Warnings(_check.Check(Folder("overflow", "shows").ToUpperInvariant() + System.IO.Path.DirectorySeparatorChar, _admin.Id)));
        Assert.StartsWith("This folder is in the TV library \"Shows\": Jellyfin takes clips there for the episodes", (string)shows["Message"]!, StringComparison.Ordinal);

        // An admin who may not open the library learns its type only.
        _open.Clear();
        Assert.StartsWith("This folder is in a Movies library:", (string)Assert.Single(Warnings(_check.Check(Folder("media", "films", "clips"), _admin.Id)))["Message"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderOfItsOwn_AHomeVideosOrMixedLibrary_TheDefaultFolder_AndAFolderBesideALibrary_AreNot()
    {
        foreach (var folder in new[] { Folder("clips"), Folder("media", "clips"), Folder("media", "mixed", "x"), Folder("media", "films2"), Folder("media"), string.Empty, "  " })
        {
            var check = _check.Check(folder, _admin.Id);
            Assert.True((bool)check["Valid"]!, folder);
            Assert.Empty(Warnings(check));
        }

        Assert.EndsWith(System.IO.Path.Combine("bookmarks", "clips"), (string)_check.Check(null, _admin.Id)["Path"]!, StringComparison.OrdinalIgnoreCase);
        var invalid = _check.Check("clips" + (char)0 + "folder", _admin.Id);
        Assert.Equal((false, null), ((bool)invalid["Valid"]!, invalid["Path"]));
    }

    [Fact]
    public void DiagnosticsClipStorage_GetsTheWarning_WithoutTheLibrarysName()
    {
        var entry = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Root"] = Folder("media", "shows", "clips"), ["Warnings"] = new List<IDictionary<string, object?>>() };

        _check.AddTo(entry);

        var warning = Assert.Single((List<IDictionary<string, object?>>)entry["Warnings"]!);
        Assert.Equal(ClipFolderInLibrary.WarningCode, warning["Code"]);
        Assert.StartsWith("This folder is in a TV library:", (string)warning["Message"]!, StringComparison.Ordinal);

        var elsewhere = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Root"] = Folder("clips"), ["Warnings"] = new List<IDictionary<string, object?>>() };
        _check.AddTo(elsewhere);
        Assert.Empty((List<IDictionary<string, object?>>)elsewhere["Warnings"]!);
    }

    private static IEnumerable<IDictionary<string, object?>> Warnings(IDictionary<string, object?> check)
        => (IEnumerable<IDictionary<string, object?>>)check["Warnings"]!;

    private string Folder(params string[] parts) => _directory.Combine(parts);
}
