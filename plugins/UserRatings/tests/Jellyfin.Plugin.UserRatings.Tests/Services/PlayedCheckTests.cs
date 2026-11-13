using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.UserRatings.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// <see cref="PlayedCheck"/>, User Ratings' wiring of the kit's <see cref="FreshUserData"/>: a leaf item's
/// played state comes from the database even when core's cached instance has no user-data snapshot (as
/// after <c>LibraryManager.cs:2169</c> blanked it), through a query with no user that asks for user data; a Series, Season or
/// MusicAlbum asks a count of played children with the user's filter, never the cached flag; an item that can't be read
/// fresh is "unknown", never a guess.
/// </summary>
public sealed class PlayedCheckTests
{
    private readonly User _viewer = ServicesFixture.NewUser("viewer");
    private readonly User _other = ServicesFixture.NewUser("other");
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly List<InternalItemsQuery> _queries = [];
    private readonly List<InternalItemsQuery> _counts = [];
    private BaseItem? _fresh;
    private int _playedChildren;

    public PlayedCheckTests()
    {
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) =>
        {
            _queries.Add(q);
            return _fresh is null ? [] : [_fresh];
        });
        _library.Setup(l => l.GetCount(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) =>
        {
            _counts.Add(q);
            return _playedChildren;
        });

        // 10.11.5's GetUserData: the snapshot only, a blank record without one (UserDataManager.cs:239-245).
        _userData.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((User user, BaseItem item)
            => item.UserData?.Where(e => e.UserId.Equals(user.Id)).Select(r => new UserItemData { Key = r.CustomDataKey, Played = r.Played }).FirstOrDefault()
                ?? new UserItemData { Key = "blank" });
    }

    [Fact]
    public void ALeafWithABlankCachedSnapshot_IsPlayedWhenTheDatabaseSaysSo()
    {
        var cached = Cached(new Movie { Name = "The Matrix" });
        _fresh = FreshCopy(cached, (_viewer, true), (_other, false));

        Assert.True(Check().HasPlayed(cached, _viewer));

        // The query: no user (no access filter; access was checked before), user data asked for, only the item.
        var query = Assert.Single(_queries);
        Assert.Null(query.User);
        Assert.Equal(new[] { cached.Id }, query.ItemIds);
        Assert.True(query.DtoOptions.EnableUserData);

        // And core's cached instance was repaired on the way.
        Assert.Same(_fresh.UserData, cached.UserData);
        Assert.Empty(_counts);
    }

    [Fact]
    public void ALeafTheDatabaseSaysIsUnplayed_IsUnplayed_AndOneThatCantBeRead_IsUnknown()
    {
        var cached = Cached(new Episode { Name = "Pilot" });
        _fresh = FreshCopy(cached, (_viewer, false));
        Assert.False(Check().HasPlayed(cached, _viewer));

        _fresh = null;
        Assert.Null(Check().HasPlayed(cached, _viewer));
    }

    [Fact]
    public void AnUnplayedMovie_WhoseOtherVersionWasPlayed_IsPlayedOnJellyfin12Only()
    {
        var cached = Cached(new Movie { Name = "Heat" });
        _fresh = FreshCopy(cached, (_viewer, false));
        _playedChildren = 1;

        Assert.Equal(JellyfinLine.Is12, Check().HasPlayed(cached, _viewer));

        if (JellyfinLine.Is12)
        {
            var query = Assert.Single(_counts);
            Assert.Same(_viewer, query.User);
            Assert.Equal(new[] { cached.Id }, query.ItemIds);
            Assert.True(query.IsPlayed);
        }
        else
        {
            Assert.Empty(_counts);
        }
    }

    [Theory]
    [InlineData(BaseItemKind.Series, BaseItemKind.Episode)]
    [InlineData(BaseItemKind.Season, BaseItemKind.Episode)]
    [InlineData(BaseItemKind.MusicAlbum, BaseItemKind.Audio)]
    public void AContainer_IsPlayedWhenAnyChildIs_ByTheUsersCountQuery_NotItsCachedFlag(BaseItemKind kind, BaseItemKind child)
    {
        BaseItem container = kind switch
        {
            BaseItemKind.Series => new Series { Name = "Show" },
            BaseItemKind.Season => new Season { Name = "Season 1" },
            _ => new MusicAlbum { Name = "Album" }
        };
        Cached(container);

        // The cached snapshot says played (or anything): it is never read.
        _fresh = FreshCopy(container, (_viewer, true));
        _playedChildren = 0;
        Assert.False(Check().HasPlayed(container, _viewer));
        _playedChildren = 1;
        Assert.True(Check().HasPlayed(container, _viewer));

        var query = _counts[^1];
        Assert.Same(_viewer, query.User);
        Assert.Equal(new[] { container.Id }, query.AncestorIds);
        Assert.True(query.IsPlayed);
        Assert.True(query.Recursive);
        Assert.Equal(new[] { child }, query.IncludeItemTypes);
        Assert.Equal(1, query.Limit);
        Assert.Empty(_queries);
    }

    private PlayedCheck Check() => new(_library.Object, new FreshUserData(_library.Object, _userData.Object));

    // Core's cached instance of an item, without its user-data snapshot.
    private T Cached<T>(T item)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        item.UserData = null;
        _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        return item;
    }

    private static BaseItem FreshCopy(BaseItem cached, params (User User, bool Played)[] rows)
    {
        var copy = (BaseItem)Activator.CreateInstance(cached.GetType())!;
        copy.Id = cached.Id;
        copy.Name = cached.Name;
        copy.UserData = rows.Select(r => new UserData
        {
            ItemId = cached.Id,
            Item = null!,
            UserId = r.User.Id,
            User = null!,
            CustomDataKey = cached.Id.ToString("N"),
            Played = r.Played
        }).ToList();
        return copy;
    }
}
