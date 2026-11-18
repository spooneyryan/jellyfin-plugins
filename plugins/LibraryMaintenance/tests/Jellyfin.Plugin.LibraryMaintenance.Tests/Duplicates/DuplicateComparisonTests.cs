using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Compare;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// <see cref="DuplicateComparison"/>: the comparison's copies built from the library's items, their stored streams
/// (embedded and external subtitles, commentaries), their extras (kinds and places), missing data, an offline file, the
/// best of each detail, the play counts, and that it reads only (no save on the library, user data or files).
/// </summary>
public sealed class DuplicateComparisonTests
{
    private const string Folder = @"C:\media\Movies\Heat (1995)";
    private const string OtherFolder = @"D:\media\More Movies\Heat (1995)";

    private readonly FakeLibrary _library = new();
    private readonly Mock<IMediaSourceManager> _sources = new(MockBehavior.Strict);
    private readonly Mock<IUserDataManager> _userData = new(MockBehavior.Strict);
    private readonly Mock<IUserManager> _users = new(MockBehavior.Strict);
    private readonly Mock<IDuplicateGroups> _groups = new(MockBehavior.Strict);
    private readonly Dictionary<Guid, List<MediaStream>> _streams = [];
    private readonly List<User> _userList = [];
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _data = [];
    private readonly List<string> _checked = [];

    public DuplicateComparisonTests()
    {
        _sources.Setup(s => s.GetMediaStreams(It.IsAny<Guid>())).Returns((Guid id) => _streams.TryGetValue(id, out var list) ? list : []);
        _userData.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((User user, BaseItem item) => _data.GetValueOrDefault((user.Id, item.Id)));
#if JELLYFIN_12
        _users.Setup(u => u.GetUsers()).Returns(() => _userList);
        _library.Mock.Setup(l => l.GetLocalAlternateVersionIds(It.IsAny<Video>())).Returns([]);
#else
        _users.SetupGet(u => u.Users).Returns(() => _userList);
#endif
    }

    [Fact]
    public async Task TwoCopies_AreComparedTrackByTrack_TheBestOfEachDetailMarked()
    {
        var uhd = Movie(Folder, "Heat (1995) - 2160p.mkv", size: 60_000_000_000, bitrate: 48_000_000);
        var hd = Movie(OtherFolder, "Heat (1995).mp4", size: 9_000_000_000, bitrate: 12_000_000);
        hd.Container = "mp4";
        _streams[uhd.Id] =
        [
            new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Profile = "Main 10", Width = 3840, Height = 1600, BitDepth = 10, BitRate = 45_000_000, AverageFrameRate = 23.976f, ColorTransfer = "smpte2084", ColorPrimaries = "bt2020" },
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "truehd", Language = "eng", Channels = 8, ChannelLayout = "7.1", IsDefault = true },
            new MediaStream { Type = MediaStreamType.Audio, Index = 2, Codec = "ac3", Language = "eng", Channels = 2, Title = "Director's Commentary" },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 3, Codec = "PGSSUB", Language = "eng", IsForced = true },
            new MediaStream { Type = MediaStreamType.Subtitle, Index = 4, Codec = "subrip", Language = "fre", IsExternal = true, Path = Folder + @"\Heat (1995) - 2160p.fr.srt" },
        ];
        _streams[hd.Id] =
        [
            new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Profile = "High", Width = 1920, Height = 800, BitDepth = 8, BitRate = 10_000_000, AverageFrameRate = 23.976f, ColorTransfer = "bt709", ColorPrimaries = "bt709" },
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Language = "eng", Channels = 6, IsDefault = true },
        ];
        var compare = await Compare(Group(uhd, hd));

        Assert.Equal(["Main", "Main"], compare.Copies.Select(c => c.Role));
        var first = compare.Copies[0];
        Assert.Equal((uhd.Id, uhd.Id, "Heat (1995) - 2160p.mkv", Folder), (first.ItemId, first.EntryItemId, first.FileName, first.Folder));
        Assert.Equal(("Present", 60_000_000_000L, "mkv", 48_000_000), (first.FileState, first.SizeBytes!.Value, first.Container, first.TotalBitrate!.Value));
        var video = first.Video!;
        Assert.Equal((3840, 1600, "2160p", "hevc", "Main 10", 10), (video.Width!.Value, video.Height!.Value, video.ResolutionLabel, video.Codec, video.Profile, video.BitDepth!.Value));
        Assert.Equal(("HDR", "HDR10", 45_000_000, 23.976f), (video.VideoRange, video.VideoRangeType, video.BitRate!.Value, video.FrameRate!.Value));
        Assert.Equal([(1, "eng", "truehd", 8, true, false), (2, "eng", "ac3", 2, false, true)], first.Audio.Select(a => (a.Index, a.Language!, a.Codec!, a.Channels!.Value, a.IsDefault, a.IsCommentary)));
        Assert.Equal("Director's Commentary", first.Audio[1].Title);
        Assert.Equal(
            [(3, "eng", "PGSSUB", true, false, (string?)null), (4, "fre", "subrip", false, true, "Heat (1995) - 2160p.fr.srt")],
            first.Subtitles.Select(s => (s.Index, s.Language!, s.Codec!, s.IsForced, s.IsExternal, s.FileName)));
        Assert.Equal(("SDR", "SDR"), (compare.Copies[1].Video!.VideoRange, compare.Copies[1].Video!.VideoRangeType));

        // A commentary track is a special feature.
        Assert.Equal(1, first.SpecialFeatures.Count);
        Assert.Equal(("Commentary", 1), (first.SpecialFeatures.ByKind.Single().Name, first.SpecialFeatures.ByKind.Single().Count));
        Assert.Equal("AudioTrack", first.SpecialFeatures.ByLocation.Single().Name);

        // The best of each detail they differ in: the 2160p copy, but for nothing they share.
        Assert.Equal(
            ["Resolution", "Size", "Bitrate", "VideoBitrate", "BitDepth", "DynamicRange", "AudioTracks", "AudioChannels", "SubtitleTracks", "SpecialFeatures"],
            compare.Best.Select(b => b.Field));
        Assert.All(compare.Best, b => Assert.Equal([uhd.Id], b.ItemIds));
    }

    [Fact]
    public async Task Extras_AreCountedByKindAndPlace_ThemeMediaLeftOut()
    {
        var heat = Movie(Folder, "Heat (1995).mkv");
        var other = Movie(OtherFolder, "Heat (1995).mkv");
        Extra(heat, Folder + @"\Heat (1995)-trailer.mkv", ExtraType.Trailer, "Heat");
        Extra(heat, Folder + @"\extras\Making of.mkv", ExtraType.Featurette, "Making of");
        Extra(heat, Folder + @"\Featurettes\Shoot-out.mkv", ExtraType.Featurette, "Heat");
        Extra(heat, Folder + @"\Featurettes\Score.mkv", ExtraType.Featurette, "Heat");
        Extra(heat, Folder + @"\deleted scenes\Scene 12.mkv", ExtraType.DeletedScene, "Scene 12");
        Extra(heat, @"E:\Trailers\Heat.mkv", ExtraType.Trailer, "Elsewhere");
        Extra(heat, Folder + @"\theme.mp3", ExtraType.ThemeSong, "Theme");
        Extra(heat, Folder + @"\backdrops\theme.mkv", ExtraType.ThemeVideo, "Theme video");

        var compare = await Compare(Group(heat, other));
        var features = compare.Copies[0].SpecialFeatures;

        Assert.Equal(6, features.Count);
        Assert.Equal([("Featurette", 3), ("Trailer", 2), ("DeletedScene", 1)], features.ByKind.Select(k => (k.Name, k.Count)));
        Assert.Equal(
            [("ExtrasFolder", "Featurettes", 2), ("BesideFile", null, 1), ("Elsewhere", null, 1), ("ExtrasFolder", "deleted scenes", 1), ("ExtrasFolder", "extras", 1)],
            features.ByLocation.Select(l => (l.Name, l.Folder, l.Count)));
        var items = features.Items.Select(i => (i.Kind, i.Name, i.FileName, i.Location, i.Folder)).ToList();
        Assert.Contains(("Trailer", "Heat", "Heat (1995)-trailer.mkv", "BesideFile", (string?)null), items);
        Assert.Contains(("Featurette", "Heat", "Score.mkv", "ExtrasFolder", "Featurettes"), items);
        Assert.Contains(("Trailer", "Elsewhere", "Heat.mkv", "Elsewhere", (string?)null), items);
        Assert.DoesNotContain(items, i => i.Kind.StartsWith("Theme", StringComparison.Ordinal));

        // The copy with none has none, and the one with them is best.
        Assert.Equal(0, compare.Copies[1].SpecialFeatures.Count);
        Assert.Empty(compare.Copies[1].SpecialFeatures.ByKind);
        Assert.Equal([heat.Id], compare.Best.Single(b => b.Field == "SpecialFeatures").ItemIds);
    }

    [Fact]
    public async Task ADiscFolder_KeepsItsExtrasInItsOwnFolder()
    {
        var disc = Movie(Folder, string.Empty);
        disc.Path = Folder;
        disc.VideoType = VideoType.BluRay;
        var file = Movie(OtherFolder, "Heat (1995).mkv");
        Extra(disc, Folder + @"\extras\Trailer.mkv", ExtraType.Trailer, "Trailer");
        string? askedFolder = null;

        var compare = await Compare(Group(disc, file), exists: (path, isFolder) =>
        {
            if (isFolder)
            {
                askedFolder = path;
            }

            return true;
        });

        Assert.Equal(Folder, askedFolder);
        Assert.Equal(("BluRay", "Heat (1995)"), (compare.Copies[0].VideoType, compare.Copies[0].FileName));
        Assert.Equal(("ExtrasFolder", "extras"), (compare.Copies[0].SpecialFeatures.Items.Single().Location, compare.Copies[0].SpecialFeatures.Items.Single().Folder));
    }

    [Fact]
    public async Task MissingData_IsNull_NotZero_AndTakesNoPartInTheBest()
    {
        // Never probed: no streams, no size, no bitrate, no runtime, no container.
        var bare = Movie(Folder, "Heat (1995).mkv");
        bare.Size = null;
        bare.TotalBitrate = null;
        bare.RunTimeTicks = null;
        bare.Container = null;
        var probed = Movie(OtherFolder, "Heat (1995).mkv", size: 1000, bitrate: 2000);
        _streams[probed.Id] = [new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = "aac", Channels = 2 }];

        var compare = await Compare(Group(bare, probed));
        var copy = compare.Copies[0];

        Assert.Null(copy.Video);
        Assert.Empty(copy.Audio);
        Assert.Empty(copy.Subtitles);
        Assert.Equal((null, null, null, null), (copy.SizeBytes, copy.TotalBitrate, copy.RunTimeTicks, copy.Container));
        Assert.Equal(0, copy.SpecialFeatures.Count);

        // One known size, bitrate or track count is no comparison, and both have no special features: nothing is best.
        Assert.Empty(compare.Best);
    }

    [Fact]
    public async Task AnOfflineCopy_IsMissing_YetShowsItsStoredTracks_AndUnansweredOrDevicePathsAreUnknown()
    {
        var offline = Movie(@"N:\Movies\Heat (1995)", "Heat (1995).mkv");
        var present = Movie(Folder, "Heat (1995).mkv");
        var slow = Movie(@"\\nas\movies\Heat (1995)", "Heat (1995).mkv");
        var device = Movie(@"\\?\C:\media\Heat (1995)", "Heat (1995).mkv");
        var stream = Movie(Folder, "x.strm");
        stream.Path = "https://example.invalid/heat.m3u8";
        _streams[offline.Id] = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Width = 1920, Height = 1080 }];
        using var never = new ManualResetEventSlim();

        var compare = await Compare(Group(offline, present, slow, device, stream), timeout: TimeSpan.FromSeconds(2), exists: (path, _) =>
        {
            if (path.StartsWith(@"\\nas", StringComparison.Ordinal))
            {
                never.Wait(TimeSpan.FromSeconds(30));
            }

            return !path.StartsWith("N:", StringComparison.Ordinal);
        });

        Assert.Equal(["Missing", "Present", "Unknown", "Unknown", "NotAFile"], compare.Copies.Select(c => c.FileState));
        Assert.Equal(1920, compare.Copies[0].Video!.Width);
        Assert.DoesNotContain(_checked, p => p.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.DoesNotContain(_checked, p => p.Contains("://", StringComparison.Ordinal));
        never.Set();
    }

    [Fact]
    public async Task AnEntrysVersions_AreCopiesOfTheirOwn_AndAGoneItemIsLeftOut()
    {
        var main = Movie(Folder, "Heat (1995) - 1080p.mkv");
        var version = Movie(Folder, "Heat (1995) - 720p.mkv");
        var other = Movie(OtherFolder, "Heat (1995).mkv");
        var group = Group(main, other);
        group.Entries[0].LinkedVersionIds = [version.Id, Guid.NewGuid()];

        var compare = await Compare(group);

        Assert.Equal([(main.Id, main.Id, "Main"), (version.Id, main.Id, "Version"), (other.Id, other.Id, "Main")], compare.Copies.Select(c => (c.ItemId, c.EntryItemId, c.Role)));
        Assert.False(compare.Truncated);
        Assert.Equal(main.Id, compare.SuggestedKeepId);
    }

#if !JELLYFIN_12
    [Fact]
    public async Task ALocalAlternate_IsACopyOfItsEntry()
    {
        var main = Movie(Folder, "Heat (1995) - 1080p.mkv");
        main.LocalAlternateVersions = [Folder + @"\Heat (1995) - 720p.mkv"];
        var alternate = Movie(Folder, "Heat (1995) - 720p.mkv");
        _library.Mock.Setup(l => l.GetNewItemId(Folder + @"\Heat (1995) - 720p.mkv", typeof(Video))).Returns(alternate.Id);
        var other = Movie(OtherFolder, "Heat (1995).mkv");

        var compare = await Compare(Group(main, other));

        Assert.Equal(["Main", "LocalAlternate", "Main"], compare.Copies.Select(c => c.Role));
    }
#endif

    [Fact]
    public async Task PlayCounts_AreEveryUsersCounted_WithNoName()
    {
        var heat = Movie(Folder, "Heat (1995).mkv");
        var other = Movie(OtherFolder, "Heat (1995).mkv");
        var alice = AddUser("alice");
        var bob = AddUser("bob");
        AddUser("carol");
        _data[(alice.Id, heat.Id)] = new UserItemData { Key = "k", Played = true, PlayCount = 3 };
        _data[(bob.Id, heat.Id)] = new UserItemData { Key = "k", PlaybackPositionTicks = 1000, PlayCount = 1 };

        var compare = await Compare(Group(heat, other));

        Assert.Equal((1, 1, 4), (compare.Copies[0].Played!.UsersPlayed, compare.Copies[0].Played!.UsersInProgress, compare.Copies[0].Played!.PlayCount));
        Assert.Equal((0, 0, 0), (compare.Copies[1].Played!.UsersPlayed, compare.Copies[1].Played!.UsersInProgress, compare.Copies[1].Played!.PlayCount));
        Assert.DoesNotContain("alice", System.Text.Json.JsonSerializer.Serialize(compare), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABigGroup_ShowsTheFirst24Copies()
    {
        var movies = Enumerable.Range(0, 30).Select(i => Movie(Folder + i, "Heat.mkv")).ToArray();

        var compare = await Compare(Group(movies));

        Assert.Equal(DuplicateComparison.MaxCopies, compare.Copies.Count);
        Assert.True(compare.Truncated);
    }

    [Fact]
    public async Task NoIndexYet_IsNull_AndAGroupThatIsGone_IsTheGroupsOwn404()
    {
        var comparison = Comparison();
        _groups.Setup(g => g.GetGroupAsync("pending", null, null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync((LibraryMaintenanceDuplicateGroupDto?)null);
        _groups.Setup(g => g.GetGroupAsync("gone", null, null, null, null, It.IsAny<CancellationToken>())).ThrowsAsync(Jellyfin.Plugin.Kit.Api.PluginException.NotFound("gone"));

        Assert.Null(await comparison.CompareAsync("pending", null, null, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<Jellyfin.Plugin.Kit.Api.PluginException>(() => comparison.CompareAsync("gone", null, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task TheComparison_ReadsOnly()
    {
        var heat = Movie(Folder, "Heat (1995).mkv");
        var other = Movie(OtherFolder, "Heat (1995).mkv");
        AddUser("alice");
        Extra(heat, Folder + @"\extras\a.mkv", ExtraType.Featurette, "a");

        await Compare(Group(heat, other));

        // Strict mocks: only GetMediaStreams, GetUserData and the users were asked; the library saw no write.
        _sources.Verify(s => s.GetMediaStreams(It.IsAny<Guid>()), Times.Exactly(2));
        _sources.VerifyNoOtherCalls();
        _userData.Verify(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()), Times.Exactly(2));
        _userData.VerifyNoOtherCalls();
        _library.Mock.Verify(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
        _library.Mock.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(@"C:\a\b\c.mkv", @"C:\a\b", "c.mkv")]
    [InlineData("/mnt/media/Heat/Heat.mkv", "/mnt/media/Heat", "Heat.mkv")]
    [InlineData(@"C:\a\b\", @"C:\a", "b")]
    [InlineData("c.mkv", "", "c.mkv")]
    [InlineData("", "", "")]
    public void PathParts_TakeEitherSlash(string path, string folder, string name)
    {
        Assert.Equal((folder, name), (DuplicateComparison.FolderOf(path), DuplicateComparison.NameOf(path)));
    }

    private DuplicateComparison Comparison(Func<string, bool, bool>? exists = null, TimeSpan? timeout = null)
    {
        var comparison = new DuplicateComparison(_groups.Object, _library.Object, _sources.Object, _userData.Object, _users.Object);
        var check = exists ?? ((_, _) => true);
        comparison.Exists = (path, isFolder) =>
        {
            lock (_checked)
            {
                _checked.Add(path);
            }

            return check(path, isFolder);
        };
        comparison.FileCheckTimeout = timeout ?? TimeSpan.FromSeconds(10);
        return comparison;
    }

    private async Task<LibraryMaintenanceDuplicateCompareDto> Compare(LibraryMaintenanceDuplicateGroupDto group, Func<string, bool, bool>? exists = null, TimeSpan? timeout = null)
    {
        _groups.Setup(g => g.GetGroupAsync(group.GroupId, "Title", null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(group);
        var compare = await Comparison(exists, timeout).CompareAsync(group.GroupId, "Title", null, null, null, CancellationToken.None);
        Assert.NotNull(compare);
        Assert.Equal((group.GroupId, group.DisplayTitle), (compare.GroupId, compare.DisplayTitle));
        return compare;
    }

    private static LibraryMaintenanceDuplicateGroupDto Group(params Movie[] movies) => new()
    {
        GroupId = "0123456789abcdef",
        DisplayTitle = "Heat",
        SuggestedKeepId = movies[0].Id,
        Entries = movies.Select(m => new LibraryMaintenanceDuplicateGroupEntryDto { ItemId = m.Id, Name = m.Name, Path = m.Path }).ToList(),
    };

    private Movie Movie(string folder, string file, long size = 1000, int bitrate = 1000)
    {
        var movie = new Movie
        {
            Name = "Heat",
            Path = folder + @"\" + file,
            Size = size,
            TotalBitrate = bitrate,
            Container = "mkv",
            RunTimeTicks = TimeSpan.FromMinutes(170).Ticks,
            DateCreated = new DateTime(2021, 3, 2, 10, 0, 0, DateTimeKind.Utc),
        };
        return _library.Add(movie);
    }

    // An extra as each line keeps it: in the owner's ExtraIds on 10.11, owned by it on 12.
    private void Extra(Movie owner, string path, ExtraType type, string name)
    {
        var extra = _library.Add(new Video { Name = name, Path = path, ExtraType = type, OwnerId = owner.Id, RunTimeTicks = TimeSpan.FromMinutes(2).Ticks });
#if JELLYFIN_12
        _ = extra;
#else
        owner.ExtraIds = [.. owner.ExtraIds, extra.Id];
#endif
    }

    private User AddUser(string name)
    {
        var user = new User(name, "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Guid.NewGuid() };
        _userList.Add(user);
        return user;
    }
}
