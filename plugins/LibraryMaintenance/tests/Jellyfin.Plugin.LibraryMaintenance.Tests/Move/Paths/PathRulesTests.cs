using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;

/// <summary>
/// The path matrix: the forms of location that are never opened, the names a client may send, Jellyfin's
/// ignored names (our copy pinned against 10.11.5's list), and the separator-aware location test.
/// </summary>
public sealed class PathRulesTests
{
    // v10.11.5:Emby.Server.Implementations/Library/IgnorePatterns.cs:14-100, the patterns that aren't comments, in order
    // (checked with `git show v10.11.5:…` on 2026-11-01). A change on Jellyfin's side is a change here and in PathRules.
    private static readonly string[] _upstreamIgnorePatterns =
    [
        "**/small.jpg", "**/albumart.jpg",
        "**/sample.?", "**/sample.??", "**/sample.???", "**/sample.????", "**/sample.?????",
        "**/*.sample.?", "**/*.sample.??", "**/*.sample.???", "**/*.sample.????", "**/*.sample.?????", "**/sample/*",
        "**/metadata/**", "**/metadata", "**/ps3_update/**", "**/ps3_update", "**/ps3_vprm/**", "**/ps3_vprm",
        "**/extrafanart/**", "**/extrafanart", "**/extrathumbs/**", "**/extrathumbs", "**/.actors/**", "**/.actors",
        "**/.wd_tv/**", "**/.wd_tv", "**/lost+found/**", "**/lost+found", "**/subs/**", "**/subs",
        "**/*.trickplay", "**/*.trickplay/**", "**/TempRec/**", "**/TempRec", "**/TempSBE/**", "**/TempSBE",
        "**/eaDir/**", "**/eaDir", "**/@eaDir/**", "**/@eaDir", "**/#recycle/**", "**/#recycle",
        "**/@Recycle/**", "**/@Recycle", "**/.@__thumb/**", "**/.@__thumb", "**/$RECYCLE.BIN/**", "**/$RECYCLE.BIN",
        "**/System Volume Information/**", "**/System Volume Information", "**/.grab/**", "**/.grab",
        "**/.*", "**/thumbs.db", "**/*.bts", "**/*.sync", "**/.zfs/**", "**/.zfs",
    ];

    public static TheoryData<string?, string> Locations => new()
    {
        { @"C:\Movies", nameof(LocationKind.Local) },
        { "c:/Movies", nameof(LocationKind.Local) },
        { @"Q:\Video\Movies\", nameof(LocationKind.Local) },
        { @"C:\", nameof(LocationKind.Local) },
        { @"  C:\Movies  ", nameof(LocationKind.Local) },
        { @"\\?\C:\Movies", nameof(LocationKind.Device) },
        { "//?/C:/Movies", nameof(LocationKind.Device) },
        { @"\\?\UNC\host\share\Movies", nameof(LocationKind.Device) },
        { @"\\.\C:\Movies", nameof(LocationKind.Device) },
        { "//./PhysicalDrive0", nameof(LocationKind.Device) },
        { @"\??\C:\Movies", nameof(LocationKind.Device) },
        { "/??/C:/Movies", nameof(LocationKind.Device) },
        { @"\?\UNC\host\share", nameof(LocationKind.Device) },
        { "/?/C:/Movies", nameof(LocationKind.Device) },
        { @"\\?\Volume{0b8e5a3c-0000-0000-0000-100000000000}\Movies", nameof(LocationKind.Device) },
        { @"\\server\share\Movies", nameof(LocationKind.Network) },
        { "//server/share/Movies", nameof(LocationKind.Network) },
        { @"\/server\share", nameof(LocationKind.Network) },
        { "C:Movies", nameof(LocationKind.NotFullyQualified) },
        { "C:", nameof(LocationKind.NotFullyQualified) },
        { "Movies", nameof(LocationKind.NotFullyQualified) },
        { @"\Movies", nameof(LocationKind.NotFullyQualified) },
        { "/Movies", nameof(LocationKind.NotFullyQualified) },
        { @"..\Movies", nameof(LocationKind.NotFullyQualified) },
        { "1:/Movies", nameof(LocationKind.NotFullyQualified) },
        { "", nameof(LocationKind.NotFullyQualified) },
        { "   ", nameof(LocationKind.NotFullyQualified) },
        { null, nameof(LocationKind.NotFullyQualified) },
    };

    public static TheoryData<string> RefusedNames => new()
    {
        "", ".", "..", @"a\b", "a/b", "a:b", "Movie.mkv:Zone.Identifier", "a<b", "a>b", "a\"b", "a|b", "a?b", "a*b",
        "a\u0001b", "a\tb", "a\u007fb", " Movies", "Movies.", "Movies ", "Movies. ", "CON", "con", "PRN", "AUX", "NUL",
        "COM1", "com9", "COM\u00b9", "LPT1", "LPT\u00b3", new string('a', 256),
    };

    public static TheoryData<string> AllowedNames => new()
    {
        "Movies", "Con.Air.1997", "Con Air (1997)", "CONSOLE", "COM10", "LPT0", "Amélie (2001)", "東京物語 (1953)",
        "Ünïcødé – Δοκιμή", "Movie.2001.mkv", "a b", "Season 01", "[2001] A Space Odyssey", "#1 Movie", "100% Love",
        new string('a', 255),
    };

    public static TheoryData<string> RefusedRelativePaths => new()
    {
        "..", @"a\..\b", "a/../b", ".", @"a\.\b", @"\Movies", "/Movies", @"C:\Movies", "C:/Movies", "C:Movies", @"\\?\C:\Movies",
        "//?/C:/Movies", @"\??\C:\Movies", @"\\server\share", @"a\\b", "a//b", @"Movies\\", "Movies//", @"\", "/", "a:b", "Movies/a:b",
        "metadata", "A/metadata", "A/extrafanart", "A/.hidden", "A/sample", "A/@eaDir", "A/subs", "A/x.trickplay",
        "A/CON", "A/Movies.", "A/ Movies", "A/a*b", new string('a', 1025),
    };

    public static TheoryData<string?, string[]> AllowedRelativePaths => new()
    {
        { null, [] },
        { "", [] },
        { "Movies", ["Movies"] },
        { "A/B", ["A", "B"] },
        { @"A\B", ["A", "B"] },
        { @"A\B/C", ["A", "B", "C"] },
        { "Ünïcødé/東京", ["Ünïcødé", "東京"] },
        { "Con.Air.1997", ["Con.Air.1997"] },
        { @"Movies\", ["Movies"] },
        { "A/B/", ["A", "B"] },
    };

    public static TheoryData<string, bool> IgnoredNames => new()
    {
        { "metadata", true },
        { "METADATA", true },
        { "extrafanart", true },
        { "extrathumbs", true },
        { ".actors", true },
        { ".hidden", true },
        { ".ignore", true },
        { "sample", true },
        { "Sample", true },
        { "sample.mkv", true },
        { "sample.m2ts", true },
        { "movie.sample.mkv", true },
        { "x.trickplay", true },
        { "thumbs.db", true },
        { "small.jpg", true },
        { "albumart.jpg", true },
        { "$RECYCLE.BIN", true },
        { "System Volume Information", true },
        { "lost+found", true },
        { "@eaDir", true },
        { "eaDir", true },
        { "#recycle", true },
        { "@Recycle", true },
        { ".@__thumb", true },
        { "subs", true },
        { "TempRec", true },
        { "x.bts", true },
        { "x.sync", true },
        { ".zfs", true },
        { "Movies", false },
        { "Samples", false },
        { "sample.mkv.part1", false },
        { "Subtitles", false },
        { "Metadata Movies", false },
        { "Trickplay Movies", false },
        { "Season 01", false },
        { "Extras", false },
        { "", false },
    };

    public static TheoryData<string, string, bool> Under => new()
    {
        { @"Q:\Video\Movies\a.mkv", @"Q:\Video\Movies", true },
        { @"Q:\Video\Movies", @"Q:\Video\Movies", true },
        { @"Q:\Video\Movies\", @"Q:\Video\Movies", true },
        { @"Q:\Video\Movies\a", @"Q:\Video\Movies\", true },
        { @"q:\video\movies\A", @"Q:\Video\Movies", true },
        { "Q:/Video/Movies/a", @"Q:\Video\Movies", true },
        { @"C:\x", @"C:\", true },
        { @"C:\", @"C:\", true },
        { @"Q:\Video\Movies2\a.mkv", @"Q:\Video\Movies", false },
        { @"Q:\Video\Movies2", @"Q:\Video\Movies", false },
        { @"Q:\Video\Movies2017\a.mkv", @"Q:\Video\Movies", false },
        { @"Q:\Video\Movie", @"Q:\Video\Movies", false },
        { @"Q:\Video", @"Q:\Video\Movies", false },
        { @"D:\x", @"C:\", false },
        { @"Q:\Video\Movies\\a", @"Q:\Video\Movies", false },
        { @"Q:\Video\Movies.old\a", @"Q:\Video\Movies", false },
        { @"Q:\a", "", false },
    };

    [Theory]
    [MemberData(nameof(Locations))]
    public void Classify_TellsTheFormOfALocation(string? path, string expected)
        => Assert.Equal(expected, PathRules.Classify(path, static _ => false).ToString());

    [Fact]
    public void Classify_AsksAboutAMappedDrive_OnlyForADriveLetterPath()
    {
        // A device, UNC or relative path is refused by its form, without asking Windows anything (nothing is opened).
        var asked = new List<string>();
        bool Mapped(string root)
        {
            asked.Add(root);
            return char.ToUpperInvariant(root[0]) == 'Q';
        }

        Assert.Equal(LocationKind.MappedNetworkDrive, PathRules.Classify(@"Q:\Video\Movies", Mapped));
        Assert.Equal(LocationKind.MappedNetworkDrive, PathRules.Classify("q:/Video", Mapped));
        Assert.Equal(LocationKind.Local, PathRules.Classify(@"C:\Movies", Mapped));
        Assert.Equal([@"Q:\", "q:/", @"C:\"], asked);

        asked.Clear();
        foreach (var path in new[] { @"\\?\Q:\Movies", @"\??\Q:\Movies", @"\\server\share", "Q:Movies", @"\?\Q:\Movies", "Movies" })
        {
            Assert.NotEqual(LocationKind.Local, PathRules.Classify(path, Mapped));
        }

        Assert.Empty(asked);
    }

    [Theory]
    [InlineData(@"\\?\C:\Movies")]
    [InlineData(@"\?\UNC\host\share")]
    [InlineData("/?/x")]
    [InlineData("a?")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\??\C:\x")]
    public void IsDevicePath_IncludesAnyPathWhoseSecondCharacterIsAQuestionMark(string path)
        => Assert.True(PathRules.IsDevicePath(path));

    [Theory]
    [InlineData(@"C:\Movies")]
    [InlineData(@"\\server\share")]
    [InlineData("?")]
    [InlineData("")]
    [InlineData(null)]
    public void IsDevicePath_IsFalseForOtherPaths(string? path) => Assert.False(PathRules.IsDevicePath(path));

    [Theory]
    [MemberData(nameof(RefusedNames))]
    public void CheckName_RefusesNamesWindowsOrMoveDoesNotAllow(string name) => Assert.NotNull(PathRules.CheckName(name));

    [Fact]
    public void CheckName_RefusesNull() => Assert.NotNull(PathRules.CheckName(null));

    [Theory]
    [InlineData("NUL.txt")]
    [InlineData("nul.mkv")]
    [InlineData("CON .x")]
    [InlineData("AUX.avi")]
    [InlineData("COM1.txt")]
    [InlineData("com\u00b2.Extras")]
    [InlineData("COM0")]
    [InlineData("LPT0")]
    [InlineData("CONIN$")]
    [InlineData("conout$.log")]
    [InlineData("Con.Air.1997")]
    [InlineData("CON")]
    [InlineData("Movies.")]
    public void CheckCreatedName_RefusesWhatWindows10MapsToADevice(string name)
        => Assert.NotNull(PathRules.CheckCreatedName(name));

    [Theory]
    [InlineData("Con Air (1997)")]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("LPT10.txt")]
    [InlineData("NULL.txt")]
    [InlineData("Auxiliary")]
    [InlineData("Heat (1995)")]
    public void CheckCreatedName_AllowsOrdinaryNames(string name)
        => Assert.Null(PathRules.CheckCreatedName(name));

    [Theory]
    [MemberData(nameof(AllowedNames))]
    public void CheckName_AllowsOrdinaryNames_UnicodeAndLongOnesToo(string name) => Assert.Null(PathRules.CheckName(name));

    [Theory]
    [MemberData(nameof(RefusedRelativePaths))]
    public void SplitRelativePath_RefusesTraversalRootsDevicesAndIgnoredNames(string path)
    {
        Assert.NotNull(PathRules.SplitRelativePath(path, out var segments));
        Assert.Empty(segments);
    }

    [Theory]
    [MemberData(nameof(AllowedRelativePaths))]
    public void SplitRelativePath_SplitsOnEitherSlash(string? path, string[] expected)
    {
        Assert.Null(PathRules.SplitRelativePath(path, out var segments));
        Assert.Equal(expected, segments);
    }

    [Fact]
    public void SplitRelativePath_AllowsTheLongestPath()
    {
        var path = string.Join('/', new string('a', 255), new string('b', 255), new string('c', 255), new string('d', 250), new string('e', 5));
        Assert.Equal(PathRules.MaxRelativePathLength, path.Length);
        Assert.Null(PathRules.SplitRelativePath(path, out var segments));
        Assert.Equal(5, segments.Count);
    }

    [Fact]
    public void IgnorePatterns_AreJellyfin10115s()
        => Assert.Equal(_upstreamIgnorePatterns, PathRules.IgnorePatterns);

    [Theory]
    [MemberData(nameof(IgnoredNames))]
    public void IsIgnoredName_FollowsJellyfinsPatterns_IgnoringCase(string name, bool expected)
        => Assert.Equal(expected, PathRules.IsIgnoredName(name));

    [Fact]
    public void ExtrasFolderNames_AreTheServersOwn()
    {
        // v10.11.5:Emby.Naming/Common/NamingOptions.cs:484-570 (DirectoryName extras rules), 697-699; v12.4 adds "sample".
        string[] names = ["backdrops", "behind the scenes", "clips", "deleted scenes", "extra", "extras", "featurettes", "interviews", "other", .. (JellyfinLine.Is12 ? new[] { "sample" } : []), "samples", "scenes", "shorts", "theme-music", "trailers"];
        Assert.Equal(names, PathRules.ExtrasFolderNames.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("extras", false, true)]
    [InlineData("Trailers", false, true)]
    [InlineData("BEHIND THE SCENES", false, true)]
    [InlineData("theme-music", false, true)]
    [InlineData("Other", false, true)]
    [InlineData("extras", true, false)]
    [InlineData("Featurettes", true, false)]
    [InlineData("Extras (2001)", false, false)]
    [InlineData("trailer", false, false)]
    [InlineData("behind-the-scenes", false, false)]
    [InlineData("", false, false)]
    public void IsIgnoredExtrasFolder_BelowAnyFolderButATopLevelLocation(string name, bool inTopLevelLocation, bool expected)
        => Assert.Equal(expected, PathRules.IsIgnoredExtrasFolder(name, inTopLevelLocation));

    [Theory]
    [MemberData(nameof(Under))]
    public void IsSameOrUnder_IsSeparatorAware_AndIgnoresCase(string path, string location, bool expected)
        => Assert.Equal(expected, PathRules.IsSameOrUnder(path, location));

    [Fact]
    public void IsUnder_And_SamePath()
    {
        Assert.True(PathRules.IsUnder(@"Q:\Movies\a", @"Q:\Movies\"));
        Assert.False(PathRules.IsUnder(@"Q:\Movies\", @"Q:\Movies"));
        Assert.True(PathRules.SamePath(@"q:\movies\", @"Q:\Movies"));
        Assert.False(PathRules.SamePath(@"Q:\Movies2", @"Q:\Movies"));
        Assert.False(PathRules.SamePath(null, @"Q:\Movies"));
        Assert.Equal(@"C:\", PathRules.TrimEnd(@"C:\\"));
        Assert.Equal(@"C:\", PathRules.TrimEnd("C:"));
        Assert.Equal(@"C:\Movies", PathRules.TrimEnd(@"C:\Movies\/"));
    }
}
