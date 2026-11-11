using System;
using System.IO;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;

/// <summary>
/// No arbitrary paths: a client's <c>{LibraryId, LocationIndex, RelativePath, NewFolderName}</c>
/// resolved against real folders in a temp directory; device, network and mapped-drive locations refused by their form,
/// before anything is opened.
/// </summary>
public sealed class DestinationResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _movies;

    public DestinationResolverTests()
    {
        _movies = _temp.Combine("Movies");
        Directory.CreateDirectory(Path.Combine(_movies, "Action", "Heist"));
        Directory.CreateDirectory(_temp.Combine("Movies2", "Action"));
        File.WriteAllText(Path.Combine(_movies, "Action", "movie.mkv"), "x");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Resolve_TheLocationItself()
    {
        var d = new DestinationResolver(static _ => false).Resolve(Library(_movies), 0, null, null);
        Assert.Equal(_movies, d.Directory);
        Assert.Empty(d.Segments);
        Assert.Equal(string.Empty, d.RelativePath);
        Assert.Null(d.NewFolderName);

        Assert.Equal(_movies, new DestinationResolver(static _ => false).Resolve(Library(_movies), 0, string.Empty, null).Directory);
    }

    [Theory]
    [InlineData("Action/Heist")]
    [InlineData(@"action\HEIST")]
    [InlineData("ACTION/heist")]
    public void Resolve_SpellsEachFolderAsOnDisk(string relativePath)
    {
        var d = new DestinationResolver(static _ => false).Resolve(Library(_movies), 0, relativePath, null);
        Assert.Equal(["Action", "Heist"], d.Segments);
        Assert.Equal(@"Action\Heist", d.RelativePath);
        Assert.Equal(Path.Combine(_movies, "Action", "Heist"), d.Directory);
    }

    [Fact]
    public void Resolve_ANewFolder_Trimmed()
    {
        var d = new DestinationResolver(static _ => false).Resolve(Library(_movies), 0, "Action", "  Heat (1995) ");
        Assert.Equal("Heat (1995)", d.NewFolderName);
        Assert.Equal(Path.Combine(_movies, "Action"), d.ExistingDirectory);
        Assert.Equal(Path.Combine(_movies, "Action", "Heat (1995)"), d.Directory);
    }

    [Theory]
    [InlineData("Heist")]
    [InlineData("heist")]
    [InlineData("movie.mkv")]
    public void Resolve_RefusesANewFolderThatExists_IgnoringCase(string name)
        => AssertInvalid(() => new DestinationResolver(static _ => false).Resolve(Library(_movies), 0, "Action", name), MoveErrorCodes.DestinationInvalid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(@"a\b")]
    [InlineData("a:b")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("CON .x")]
    [InlineData("metadata")]
    [InlineData(".hidden")]
    [InlineData("Heat.")]
    public void Resolve_RefusesANewFolderNameMoveMayNotCreate(string name)
        => AssertInvalid(() => new DestinationResolver(static _ => false).Resolve(Library(_movies), 0, "Action", name), MoveErrorCodes.DestinationInvalid);

    [Theory]
    [InlineData("Missing")]
    [InlineData("Action/Missing")]
    [InlineData("Action/movie.mkv")]
    [InlineData("..")]
    [InlineData(@"..\Movies2")]
    [InlineData("../Movies2/Action")]
    [InlineData(@"Action\..\..\Movies2")]
    [InlineData(@"\Movies2")]
    [InlineData(@"C:\Windows")]
    [InlineData("C:Windows")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData("Action/metadata")]
    public void Resolve_RefusesARelativePathThatIsntAnExistingFolderInside(string relativePath)
        => AssertInvalid(() => new DestinationResolver(static _ => false).Resolve(Library(_movies), 0, relativePath, null), MoveErrorCodes.DestinationInvalid);

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Resolve_RefusesALocationIndexOutOfRange(int index)
        => AssertInvalid(() => new DestinationResolver(static _ => false).Resolve(Library(_movies), index, null, null), MoveErrorCodes.DestinationInvalid);

    [Fact]
    public void Resolve_PicksTheLocationByIndex()
    {
        var library = Library(_movies, _temp.Combine("Movies2"));
        Assert.Equal(Path.Combine(_temp.Combine("Movies2"), "Action"), new DestinationResolver(static _ => false).Resolve(library, 1, "action", null).Directory);
    }

    [Fact]
    public void Resolve_AMissingLocation_IsUnavailable()
    {
        var e = Assert.Throws<PluginException>(() => new DestinationResolver(static _ => false).Resolve(Library(_temp.Combine("Gone")), 0, null, null));
        Assert.Equal(PluginErrorKind.Unavailable, e.Kind);
        Assert.Equal(MoveErrorCodes.NotWritable, e.Code);
    }

    [Fact]
    public void Resolve_RefusesALocationOrFolderWithADotIgnoreFile()
    {
        File.WriteAllText(Path.Combine(_movies, "Action", "Heist", ".ignore"), string.Empty);
        var resolver = new DestinationResolver(static _ => false);
        var segment = Assert.Throws<PluginException>(() => resolver.Resolve(Library(_movies), 0, "Action/Heist", null));
        Assert.Equal(MoveErrorCodes.DestinationInvalid, segment.Code);
        Assert.Equal(Path.Combine(_movies, "Action", "Heist") + " has a .ignore file; Jellyfin may ignore some or all of what is below it, so Move doesn't use folders under it.", segment.Message);
        Assert.Equal(Path.Combine(_movies, "Action"), resolver.Resolve(Library(_movies), 0, "Action", null).Directory);

        File.WriteAllText(Path.Combine(_movies, ".ignore"), string.Empty);
        AssertInvalid(() => resolver.Resolve(Library(_movies), 0, null, null), MoveErrorCodes.DestinationInvalid);
        AssertInvalid(() => resolver.Resolve(Library(_movies), 0, "Action", null), MoveErrorCodes.DestinationInvalid);
    }

    [Fact]
    public void Resolve_RefusesAJunction_WithTheReparseCode()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var link = Path.Combine(_movies, "Linked");
        Junction.Create(link, _temp.Combine("Movies2"));
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);

        var resolver = new DestinationResolver(static _ => false);
        AssertInvalid(() => resolver.Resolve(Library(_movies), 0, "Linked", null), MoveErrorCodes.ReparsePoint);
        AssertInvalid(() => resolver.Resolve(Library(_movies), 0, "linked/Action", null), MoveErrorCodes.ReparsePoint);

        // A new folder may not take a junction's name either.
        AssertInvalid(() => resolver.Resolve(Library(_movies), 0, null, "Linked"), MoveErrorCodes.DestinationInvalid);
    }

    [Theory]
    [InlineData(@"\\?\{0}")]
    [InlineData("//?/{0}")]
    [InlineData(@"\??\{0}")]
    [InlineData(@"\\.\{0}")]
    [InlineData(@"\\?\UNC\localhost\{1}")]
    [InlineData(@"\\localhost\{1}")]
    [InlineData("//localhost/{1}")]
    [InlineData(@"\\jflm-test-nowhere.invalid\share\Movies")]
    public void Resolve_RefusesDeviceAndNetworkLocations_EvenWhenTheyExist(string format)
    {
        // {0} is the real temp folder (so the device form exists); {1} the same folder through the admin share. A 503 would
        // mean the location was looked for on disk; the 400 says it was refused by its form first.
        var root = Path.GetPathRoot(_movies)!;
        var adminShare = root[0] + "$" + _movies[2..];
        var location = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, _movies, adminShare);

        AssertInvalid(() => new DestinationResolver(static _ => false).Resolve(Library(location), 0, null, null), MoveErrorCodes.DestinationInvalid);
        Assert.NotNull(new DestinationResolver(static _ => false).LocationProblem(location));
    }

    [Fact]
    public void Resolve_RefusesAMappedNetworkDrive_EvenWhenTheFolderExists()
    {
        var resolver = new DestinationResolver(root => string.Equals(root, Path.GetPathRoot(_movies), StringComparison.OrdinalIgnoreCase));
        AssertInvalid(() => resolver.Resolve(Library(_movies), 0, "Action", null), MoveErrorCodes.DestinationInvalid);
        Assert.Equal("network-location", resolver.LocationProblem(_movies));
    }

    [Theory]
    [InlineData(@"C:\Movies", null)]
    [InlineData(@"\\?\C:\Movies", "device-path")]
    [InlineData(@"\?\C:\Movies", "device-path")]
    [InlineData(@"\\server\share", "network-location")]
    [InlineData("Movies", "not-a-local-path")]
    [InlineData("C:Movies", "not-a-local-path")]
    [InlineData("", "not-a-local-path")]
    public void LocationProblem_ByForm(string location, string? expected)
        => Assert.Equal(expected, new DestinationResolver(static _ => false).LocationProblem(location));

    [Fact]
    public void FindEntry_ReadsTheOnDiskSpelling_AndNeverMatchesAPrefix()
    {
        Assert.Equal("Action", DestinationResolver.FindEntry(_movies, "ACTION")?.Name);
        Assert.Null(DestinationResolver.FindEntry(_movies, "Act"));
        Assert.Null(DestinationResolver.FindEntry(_temp.Combine("Gone"), "Action"));
    }

    [Theory]
    [InlineData(@"Action\extras")]
    [InlineData(@"Action\TRAILERS")]
    [InlineData(@"Action\behind the scenes\Deep")]
    public void Resolve_AnExtrasNamedFolderBelowAnother_IsRefused(string relativePath)
    {
        // An extras-named folder below another one: CoreResolutionIgnoreRule.cs:57-62 ignores it, and what is moved into it.
        Directory.CreateDirectory(Path.Combine(_movies, "Action", "extras"));
        Directory.CreateDirectory(Path.Combine(_movies, "Action", "Trailers"));
        Directory.CreateDirectory(Path.Combine(_movies, "Action", "Behind The Scenes", "Deep"));

        AssertInvalid(() => new DestinationResolver(static _ => false).Resolve(Library(_movies), 0, relativePath, null), MoveErrorCodes.ExtrasFolderName);
    }

    [Fact]
    public void Resolve_AnExtrasNamedNewFolder_IsRefusedBelowAnother_AllowedInATopLevelLocation()
    {
        var resolver = new DestinationResolver(static _ => false);
        AssertInvalid(() => resolver.Resolve(Library(_movies), 0, "Action", "Featurettes"), MoveErrorCodes.ExtrasFolderName);
        AssertInvalid(() => resolver.Resolve(Library(_movies), 0, "Action", " extras "), MoveErrorCodes.ExtrasFolderName);

        // Directly in a top-level location, Jellyfin reads it as an ordinary folder (CoreResolutionIgnoreRule.cs:45-50).
        Assert.Equal(Path.Combine(_movies, "Featurettes"), resolver.Resolve(Library(_movies), 0, null, "Featurettes").Directory);
        Directory.CreateDirectory(Path.Combine(_movies, "Extras"));
        var top = resolver.Resolve(Library(_movies), 0, "extras", null);
        Assert.Equal(["Extras"], top.Segments);
        Assert.False(top.LocationIsNested);
    }

    [Fact]
    public void Resolve_InALocationInsideAnotherLocation_ItsFirstLevelIsntTopLevel()
    {
        // A location inside another library's location is an ordinary folder of that library: no extras name there either.
        var kids = Path.Combine(_movies, "Action");
        Directory.CreateDirectory(Path.Combine(kids, "Extras"));
        var resolver = new DestinationResolver(static _ => false, () => [_movies, kids, _temp.Combine("Movies2")]);

        AssertInvalid(() => resolver.Resolve(Library(kids), 0, "Extras", null), MoveErrorCodes.ExtrasFolderName);
        AssertInvalid(() => resolver.Resolve(Library(kids), 0, null, "Trailers"), MoveErrorCodes.ExtrasFolderName);
        Assert.True(resolver.Resolve(Library(kids), 0, null, null).LocationIsNested);
        Assert.False(resolver.Resolve(Library(_movies), 0, "Action", null).LocationIsNested);
    }

    [Fact]
    public void Resolve_AnExistingFolderNamedLikeADeviceWithADot_IsAllowed()
    {
        // It exists, so this Windows doesn't map it; only names Move creates follow Windows 10's stricter rule.
        Directory.CreateDirectory(Path.Combine(_movies, "Con.Air.1997"));
        Assert.Equal(["Con.Air.1997"], new DestinationResolver(static _ => false).Resolve(Library(_movies), 0, @"con.air.1997\", null).Segments);
    }

    [Fact]
    public void Resolve_ADotIgnoreAboveTheLocation_RefusesIt_AsJellyfinIgnoresEverythingBelow()
    {
        // DotIgnoreIgnoreRule.FindIgnoreFile walks up to the drive root (DotIgnoreIgnoreRule.cs:18-30).
        var location = _temp.Combine("Above", "Library");
        Directory.CreateDirectory(location);
        var resolver = new DestinationResolver(static _ => false);
        Assert.Equal(location, resolver.ResolveLocation(Library(location), 0));

        File.WriteAllText(_temp.Combine("Above", ".ignore"), string.Empty);
        // The folder named is the one that holds the file, here above the location.
        var above = Assert.Throws<PluginException>(() => resolver.ResolveLocation(Library(location), 0));
        Assert.Equal((PluginErrorKind.Invalid, MoveErrorCodes.DestinationInvalid), (above.Kind, above.Code));
        Assert.Equal(_temp.Combine("Above") + " has a .ignore file; Jellyfin may ignore some or all of what is below it, so Move doesn't use folders under it.", above.Message);
        Assert.Equal(_temp.Combine("Above", ".ignore"), DestinationResolver.FindIgnoreFile(location));
        Assert.Null(DestinationResolver.FindIgnoreFile(_movies));
    }

    private static CatalogLibrary Library(params string[] locations)
        => new(new Guid("c0000000-0000-0000-0000-0000000000a1"), "Movies", CollectionTypeOptions.movies, locations, null);

    private static void AssertInvalid(Action action, string code)
    {
        var e = Assert.Throws<PluginException>(action);
        Assert.Equal(PluginErrorKind.Invalid, e.Kind);
        Assert.Equal(code, e.Code);
    }
}
