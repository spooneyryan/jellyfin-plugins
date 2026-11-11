using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The libraries' folders of the In library badge: every location of a multi-location library (for example a TV library
/// with the folders <c>D:\Video\TV</c> and <c>E:\Overflow\TV</c>), compared on whole path segments and without regard to
/// case, the deepest of nested locations, the answer kept 60 s, and no badge at all when the libraries can't be read.
/// </summary>
public sealed class ClipLibraryLocationsTests
{
    private static readonly Guid TvId = Guid.Parse("a3c1d2b4e5f60718293a4b5c6d7e8f90");
    private static readonly Guid MediaId = Guid.Parse("0f1e2d3c4b5a69788796a5b4c3d2e1f0");
    private static readonly Guid ClipsId = Guid.Parse("11112222333344445555666677778888");

    private readonly Mock<ILibraryManager> _library = new(MockBehavior.Strict);
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private List<VirtualFolderInfo> _folders = [];

    public ClipLibraryLocationsTests()
    {
        _library.Setup(m => m.GetVirtualFolders()).Returns(() => _folders);
    }

    [Fact]
    public void EveryLocationOfAMultiLocationLibrary_Counts_AndTheFolderBesideItDoesnt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive-letter paths.");
        _folders =
        [
            new VirtualFolderInfo { Name = "TV", Locations = [@"D:\Video\TV", @"E:\Overflow\TV"], CollectionType = CollectionTypeOptions.tvshows, ItemId = TvId.ToString("N") },
            new VirtualFolderInfo { Name = "Movies", Locations = [@"D:\Video\Movies"], CollectionType = CollectionTypeOptions.movies, ItemId = Guid.NewGuid().ToString("N") }
        ];
        var locations = Create();

        var inTv = locations.Find(@"E:\Overflow\TV\clips\alex\Heat (1995) - Bank job [01.02.03-01.05.00].mp4");
        Assert.NotNull(inTv);
        Assert.Equal("TV", inTv.LibraryName);
        Assert.Equal(CollectionTypeOptions.tvshows, inTv.CollectionType);
        Assert.Equal(TvId, inTv.LibraryId);
        Assert.Equal("TV", locations.Find(@"D:\Video\TV\Show\clip [00.00.01-00.00.02].mkv")?.LibraryName);
        Assert.Equal("Movies", locations.Find(@"D:\Video\Movies\clip [00.00.01-00.00.02].mkv")?.LibraryName);

        // Next to the library's folder, not in it.
        Assert.Null(locations.Find(@"E:\Home Clips\alex\clip [00.00.01-00.00.02].mp4"));
        Assert.Null(locations.Find(@"E:\Overflow\clip [00.00.01-00.00.02].mp4"));
    }

    [Fact]
    public void Paths_AreComparedOnWholeSegments_AndWithoutRegardToCase()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows paths compare without regard to case.");
        _folders =
        [
            new VirtualFolderInfo { Name = "TV", Locations = [@"E:\TV"], CollectionType = CollectionTypeOptions.tvshows, ItemId = TvId.ToString("N") },
            new VirtualFolderInfo { Name = "Clips", Locations = [@"E:\Home Clips\"], CollectionType = CollectionTypeOptions.homevideos, ItemId = ClipsId.ToString("N") }
        ];
        var locations = Create();

        // E:\TV2 isn't under E:\TV; an admin's lower-case clip folder is under the library's E:\Home Clips.
        Assert.Null(locations.Find(@"E:\TV2\clip [00.00.01-00.00.02].mp4"));
        Assert.Equal("TV", locations.Find(@"e:\tv\clip [00.00.01-00.00.02].mp4")?.LibraryName);
        Assert.Equal("Clips", locations.Find(@"e:\home clips\alex\x [00.00.01-00.00.02].mp4")?.LibraryName);
        Assert.Equal("Clips", locations.Find(@"E:/Home Clips/alex/x [00.00.01-00.00.02].mp4")?.LibraryName);

        // The library's folder itself is no clip file in it.
        Assert.Null(locations.Find(@"E:\TV"));
        Assert.Null(locations.Find(null));
        Assert.Null(locations.Find("  "));
    }

    [Fact]
    public void NestedLocations_NameTheDeepest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive-letter paths.");
        _folders =
        [
            new VirtualFolderInfo { Name = "Media", Locations = [@"E:\Media"], CollectionType = CollectionTypeOptions.homevideos, ItemId = MediaId.ToString("N") },
            new VirtualFolderInfo { Name = "Clips", Locations = [@"E:\Media\Clips"], CollectionType = CollectionTypeOptions.homevideos, ItemId = ClipsId.ToString("N") }
        ];
        var locations = Create();

        Assert.Equal("Clips", locations.Find(@"E:\Media\Clips\alex\x [00.00.01-00.00.02].mp4")?.LibraryName);
        Assert.Equal("Media", locations.Find(@"E:\Media\Other\x [00.00.01-00.00.02].mp4")?.LibraryName);
    }

    [Fact]
    public void TheLibrariesFolders_AreKept60Seconds_AndALibraryWithoutAnIdHasNone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive-letter paths.");
        _folders = [new VirtualFolderInfo { Name = "Clips", Locations = [@"E:\Clips"], CollectionType = CollectionTypeOptions.homevideos, ItemId = null! }];
        var locations = Create();

        Assert.Null(locations.Find(@"E:\Clips\x [00.00.01-00.00.02].mp4")?.LibraryId);
        _folders = [];
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.NotNull(locations.Find(@"E:\Clips\x [00.00.01-00.00.02].mp4"));
        _library.Verify(m => m.GetVirtualFolders(), Times.Once);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(locations.Find(@"E:\Clips\x [00.00.01-00.00.02].mp4"));
        _library.Verify(m => m.GetVirtualFolders(), Times.Exactly(2));
    }

    [Fact]
    public void LibrariesThatCantBeRead_GiveNoBadge_AndNoError()
    {
        _library.Setup(m => m.GetVirtualFolders()).Throws(new InvalidOperationException("the database is busy"));

        Assert.Null(Create().Find(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "x [00.00.01-00.00.02].mp4")));
    }

    private ClipLibraryLocations Create() => new(_library.Object, NullLogger.Instance, _clock);
}
