using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="FreshUserData"/>: a cached instance whose user-data snapshot core set to null (as
/// <c>LibraryManager.cs:2169</c> does to a parent after a child is updated) still yields the stored favourite, played,
/// play count and resume position; the read has no user and asks for user data; the cached instance gets the rows
/// before any save; a save changes only what the caller changed; nothing is written from an unknown state; the
/// repository path is the fallback.
/// </summary>
public sealed class FreshUserDataTests
{
    private const long TenMinutes = 6_000_000_000;

    private readonly User _viewer = NewUser("viewer");
    private readonly User _other = NewUser("other");
    private readonly Series _cached = new() { Id = Guid.NewGuid(), Name = "Test Series" };
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IItemRepository> _repository = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly List<InternalItemsQuery> _queries = [];
    private readonly List<(BaseItem Item, UserItemData Data, UserDataSaveReason Reason)> _saves = [];
    private readonly ListLogger _logger = new();
    private Series? _fresh;

    public FreshUserDataTests()
    {
        _library.Setup(l => l.GetItemById(_cached.Id)).Returns(_cached);
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) =>
        {
            _queries.Add(q);
            return _fresh is null ? [] : [_fresh];
        });

        // IUserDataManager.GetUserData on 10.11.5: the snapshot only, a blank record without one (UserDataManager.cs:239-245).
        _userData.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((User user, BaseItem item)
            => item.UserData?.Where(e => e.UserId.Equals(user.Id)).Select(Map).FirstOrDefault() ?? new UserItemData { Key = "blank" });
        _userData.Setup(u => u.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Callback((User _, BaseItem item, UserItemData data, UserDataSaveReason reason, CancellationToken _) => _saves.Add((item, Copy(data), reason)));

        // After a scan or refresh touched a child: core's cached Series has no snapshot any more.
        _cached.UserData = null;
    }

    [Fact]
    public void TheNaiveRead_IsBlank_WhichIsWhyTheFreshReadExists()
    {
        var naive = _userData.Object.GetUserData(_viewer, _cached)!;

        Assert.False(naive.IsFavorite);
        Assert.False(naive.Played);
        Assert.Equal(0, naive.PlayCount);
    }

    [Fact]
    public void Read_YieldsTheStoredFavouritePlayedPlayCountAndPosition_FromACachedInstanceWithoutUserData()
    {
        _fresh = FreshCopy(Row(_viewer, favourite: true, played: true, playCount: 3, position: 1_200_000_000), Row(_other, favourite: false, played: false, playCount: 0, position: 0));

        var result = Fresh().Read(_cached.Id, _viewer)!;

        Assert.True(result.Data.IsFavorite);
        Assert.True(result.Data.Played);
        Assert.Equal(3, result.Data.PlayCount);
        Assert.Equal(1_200_000_000, result.Data.PlaybackPositionTicks);
        Assert.False(result.HadSnapshot);
        Assert.Equal(2, result.Rows);
        Assert.Equal(FreshUserDataReadPath.Query, result.Source);

        // Core's cached instance was repaired: its naive read is right again, for every user.
        Assert.Same(_cached, result.Item);
        Assert.Same(_fresh.UserData, _cached.UserData);
        Assert.True(_userData.Object.GetUserData(_viewer, _cached)!.IsFavorite);
    }

    [Fact]
    public void TheQuery_HasNoUser_AsksForUserData_AndOnlyTheItem()
    {
        _fresh = FreshCopy(Row(_viewer, favourite: true, played: false, playCount: 0, position: 0));

        Fresh().Read(_cached.Id, _viewer);

        var query = Assert.Single(_queries);
        Assert.Null(query.User);
        Assert.Equal(new[] { _cached.Id }, query.ItemIds);
        Assert.True(query.DtoOptions.EnableUserData);
        Assert.False(query.DtoOptions.EnableImages);
    }

    [Fact]
    public void Update_KeepsTheOtherFields_ChangesOnlyTheRating_AndSavesThroughTheCachedInstance()
    {
        _fresh = FreshCopy(Row(_viewer, favourite: true, played: true, playCount: 3, position: 1_200_000_000));

        var result = Fresh().Update(_cached.Id, _viewer, data =>
        {
            data.Rating = 7;
            return true;
        }, UserDataSaveReason.UpdateUserData, CancellationToken.None);

        Assert.NotNull(result);
        var save = Assert.Single(_saves);
        Assert.Same(_cached, save.Item);
        Assert.Equal(UserDataSaveReason.UpdateUserData, save.Reason);
        Assert.True(save.Data.IsFavorite);
        Assert.True(save.Data.Played);
        Assert.Equal(3, save.Data.PlayCount);
        Assert.Equal(1_200_000_000, save.Data.PlaybackPositionTicks);
        Assert.Equal(7, save.Data.Rating);

        // The snapshot was replaced before the save, not after it.
        Assert.Same(_fresh.UserData, _cached.UserData);
    }

    [Fact]
    public void Update_ThatChangesNothing_WritesNothing()
    {
        _fresh = FreshCopy(Row(_viewer, favourite: true, played: true, playCount: 3, position: 0));

        Assert.NotNull(Fresh().Update(_cached.Id, _viewer, _ => false, UserDataSaveReason.UpdateUserData, CancellationToken.None));
        Assert.Empty(_saves);
    }

    [Fact]
    public void AnItemThatCantBeLoaded_IsUnavailable_AndNothingIsWritten()
    {
        _fresh = null;
        var fresh = new FreshUserData(_library.Object, _userData.Object);

        Assert.Null(fresh.Read(_cached.Id, _viewer));
        Assert.Null(fresh.Update(_cached.Id, _viewer, _ => true, UserDataSaveReason.UpdateUserData, CancellationToken.None));
        Assert.Null(fresh.Read(Guid.NewGuid(), _viewer));
        Assert.Null(fresh.Read(Guid.Empty, _viewer));
        Assert.Empty(_saves);
        Assert.Null(_cached.UserData);
        _library.Verify(l => l.GetItemById(Guid.Empty), Times.Never);
        Assert.Equal(4L, fresh.Describe()["Unavailable"]);
    }

    [Fact]
    public void TheQueryWithoutUserData_FallsBackToTheRepository_AndSaysSoOnce()
    {
        // The case the fresh read guards against: GetItemList returns the item but not its rows.
        _fresh = new Series { Id = _cached.Id, UserData = null };
        var fromRepository = FreshCopy(Row(_viewer, favourite: true, played: true, playCount: 2, position: TenMinutes));
        _repository.Setup(r => r.RetrieveItem(_cached.Id)).Returns(fromRepository);
        var fresh = Fresh();

        var first = fresh.Read(_cached.Id, _viewer)!;
        var second = fresh.Read(_cached.Id, _viewer)!;

        Assert.Equal(FreshUserDataReadPath.Repository, first.Source);
        Assert.True(first.Data.IsFavorite);
        Assert.Equal(TenMinutes, second.Data.PlaybackPositionTicks);
        Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Equal(2L, fresh.Describe()["Fallbacks"]);
    }

    [Fact]
    public void TheRepositoryPath_CanBeChosen()
    {
        _repository.Setup(r => r.RetrieveItem(_cached.Id)).Returns(FreshCopy(Row(_viewer, favourite: true, played: false, playCount: 1, position: 0)));
        var fresh = new FreshUserData(_library.Object, _userData.Object, _repository.Object, _logger, FreshUserDataReadPath.Repository);

        var result = fresh.Read(_cached.Id, _viewer)!;

        Assert.Equal(FreshUserDataReadPath.Repository, result.Source);
        Assert.True(result.Data.IsFavorite);
        Assert.Empty(_queries);
    }

    [Fact]
    public void AUserWithoutARow_GetsATrueBlank()
    {
        _fresh = FreshCopy(Row(_other, favourite: true, played: true, playCount: 5, position: 0));

        var result = Fresh().Read(_cached.Id, _viewer)!;

        Assert.False(result.Data.IsFavorite);
        Assert.Equal(0, result.Data.PlayCount);
        Assert.Equal(1, result.Rows);
    }

    [Fact]
    public void ACachedInstanceWithItsSnapshot_IsRefreshedToo_AndSaysItHadOne()
    {
        _cached.UserData = [Row(_viewer, favourite: false, played: false, playCount: 0, position: 0)];
        _fresh = FreshCopy(Row(_viewer, favourite: true, played: false, playCount: 0, position: 0));

        var result = Fresh().Read(_cached.Id, _viewer)!;

        Assert.True(result.HadSnapshot);
        Assert.True(result.Data.IsFavorite);
    }

    private static User NewUser(string name)
        => new(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider") { Id = Guid.NewGuid() };

    private static UserItemData Map(UserData row) => new()
    {
        Key = row.CustomDataKey,
        IsFavorite = row.IsFavorite,
        Played = row.Played,
        PlayCount = row.PlayCount,
        PlaybackPositionTicks = row.PlaybackPositionTicks,
        Rating = row.Rating,
        LastPlayedDate = row.LastPlayedDate
    };

    private static UserItemData Copy(UserItemData data) => new()
    {
        Key = data.Key,
        IsFavorite = data.IsFavorite,
        Played = data.Played,
        PlayCount = data.PlayCount,
        PlaybackPositionTicks = data.PlaybackPositionTicks,
        Rating = data.Rating
    };

    private FreshUserData Fresh() => new(_library.Object, _userData.Object, _repository.Object, _logger);

    private Series FreshCopy(params UserData[] rows) => new() { Id = _cached.Id, Name = _cached.Name, UserData = rows.ToList() };

    private UserData Row(User user, bool favourite, bool played, int playCount, long position) => new()
    {
        ItemId = _cached.Id,
        Item = null,
        UserId = user.Id,
        User = null,
        CustomDataKey = _cached.Id.ToString("N"),
        IsFavorite = favourite,
        Played = played,
        PlayCount = playCount,
        PlaybackPositionTicks = position
    };
}
