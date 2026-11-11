using System;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;
using Moq;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

internal static class TestData
{
    public static readonly long OneSecond = TimeSpan.TicksPerSecond;

    /// <summary>
    /// The registrations of the kit's library services that the API tests copy from the registrator next to the plugin's
    /// own: the library-change monitor with its consumers (the demos and the identity service), the verify sweep and the
    /// detached records with the library demo's records, which the monitor and the sweep resolve when they start, and the
    /// identity service, a hosted service like the worker. The record owners' registrations
    /// (the kit's <c>KitComponent</c>s) are left out: the tests' mocked library knows no item, so a verify sweep would detach
    /// every bookmark.
    /// </summary>
    public static readonly Type[] LibraryServiceTypes =
    [
        typeof(LibraryChangeMonitor), typeof(KitComponent<ILibraryChangeConsumer>), typeof(Jellyfin.Plugin.Bookmarks.Services.BookmarkLibraryChangeDemo),
        typeof(AttachedRecordVerifier), typeof(MoveJournalReader), typeof(IdentityResolver),
        typeof(DetachedRecords), typeof(Jellyfin.Plugin.Bookmarks.Services.LibraryDemoRecords), typeof(Jellyfin.Plugin.Bookmarks.Services.BookmarkIdentityService)
    ];

    static TestData()
    {
        // Video.CanDownload() asks the static media source manager for the path protocol.
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(m => m.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        BaseItem.MediaSourceManager = mediaSourceManager.Object;
    }

    public static User CreateUser(string name = "alice", bool canDownload = true)
    {
        var user = new User(name, "DefaultAuthenticationProvider", "DefaultPasswordResetProvider");
        user.AddDefaultPermissions();
        user.SetPermission(PermissionKind.EnableContentDownloading, canDownload);
        return user;
    }

    public static MediaBrowser.Controller.Entities.Movies.Movie CreateMovie(string name = "Heat", int? year = 1995, long runtimeSeconds = 10_000)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProductionYear = year,
            RunTimeTicks = runtimeSeconds * OneSecond,
            Path = @"C:\media\" + name + ".mkv"
        };

    public static Bookmark CreateBookmark(Guid userId, long startSeconds = 60, long? endSeconds = 90, Guid? itemId = null)
    {
        var item = itemId ?? Guid.NewGuid();
        var now = DateTime.UtcNow;
        return new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ItemId = item,
            StartTicks = startSeconds * OneSecond,
            EndTicks = endSeconds * OneSecond,
            ItemName = "Heat (1995)",
            Identity = new IdentitySnapshot { ItemId = item, Kind = "Movie", Name = "Heat", Year = 1995 },
            AttachedUtc = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static IPluginConfigurationProvider ConfigurationProvider(PluginConfiguration? configuration = null)
    {
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(configuration ?? new PluginConfiguration());
        return provider.Object;
    }
}
