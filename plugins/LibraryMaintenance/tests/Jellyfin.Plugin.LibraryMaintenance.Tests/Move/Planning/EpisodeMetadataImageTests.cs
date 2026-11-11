using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Session;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;

/// <summary>
/// An episode's images in the <c>metadata</c> folder beside it: 10.11.5 reads
/// <c>&lt;stem&gt;.&lt;ext&gt;</c> and <c>&lt;stem&gt;-thumb.&lt;ext&gt;</c> there, the folder named exactly <c>metadata</c>
/// (<c>v10.11.5:MediaBrowser.LocalMetadata/Images/EpisodeLocalImageProvider.cs:45-50, 55-83</c>). They move into the target
/// season's <c>metadata</c> folder, made when it is missing; a folder of another case on either side leaves them with a
/// warning; a link on either side is never listed or followed.
/// </summary>
public sealed class EpisodeMetadataImageTests : IDisposable
{
    private readonly PlannerWorld _w = new();
    private readonly Series _lost;
    private readonly Season _lost1;
    private readonly Season _lost2;

    public EpisodeMetadataImageTests()
    {
        _lost = _w.Series("Lost");
        _lost1 = _w.Season(_lost, 1);
        _lost2 = _w.Season(_lost, 2);
    }

    public void Dispose() => _w.Dispose();

    [Theory]
    [InlineData("Ep 5.jpg", true)]
    [InlineData("EP 5.PNG", true)]
    [InlineData("Ep 5-thumb.webp", true)]
    [InlineData("ep 5-THUMB.tbn", true)]
    [InlineData("Ep 5-poster.jpg", false)]
    [InlineData("Ep 5.en.jpg", false)]
    [InlineData("Ep 5_thumb.jpg", false)]
    [InlineData("Ep 5.nfo", false)]
    [InlineData("Ep 5.srt", false)]
    [InlineData("Ep 50.jpg", false)]
    [InlineData("Ep 5-thumb-2.jpg", false)]
    public void IsEpisodeImageName_TakesTheNamesEpisodeLocalImageProviderReads(string name, bool expected)
        => Assert.Equal(expected, SidecarMatcher.IsEpisodeImageName(name, "Ep 5"));

    [Fact]
    public void TheEpisodesImages_MoveIntoANewMetadataFolder_OtherEpisodesImagesStay()
    {
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1, "Ep 5.srt");
        _w.Episode(_lost, _lost1, "Ep 50.mkv", 1);
        var metadata = Path.Join(_lost1.Path, "metadata");
        foreach (var name in new[] { "Ep 5.jpg", "ep 5-THUMB.png", "Ep 5-poster.jpg", "Ep 5.en.jpg", "Ep 50.jpg", "Ep 5.nfo" })
        {
            _w.File(Path.Join(metadata, name));
        }

        _w.Folder("TV", "Lost", "Season 1", "metadata", "Ep 5.webp");

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");

        Assert.Empty(unit.Blockers);
        Assert.DoesNotContain(unit.Warnings, w => w.Code == MoveErrorCodes.SidecarLeftBehind);
        var target = Path.Join(_lost2.Path, "metadata");
        Assert.Equal([target], unit.CreatedDirectories);

        var images = unit.Renames.Where(r => PathRules.SamePath(Path.GetDirectoryName(r.From), metadata)).ToList();
        Assert.Equal(
            [(Path.Join(metadata, "Ep 5.jpg"), Path.Join(target, "Ep 5.jpg")), (Path.Join(metadata, "ep 5-THUMB.png"), Path.Join(target, "ep 5-THUMB.png"))],
            images.Select(r => (r.From, r.To)).OrderBy(r => r.From, StringComparer.Ordinal));
        Assert.All(images, r => Assert.False(r.IsDirectory));
        Assert.Contains(unit.Entries, e => e.RelativePath == Path.Join("metadata", "Ep 5.jpg") && e.Length == 10);
        Assert.DoesNotContain(unit.Entries, e => e.RelativePath == "metadata");
        Assert.Equal(
            ["Ep 5.mkv", "Ep 5.srt"],
            unit.Renames.Where(r => PathRules.SamePath(Path.GetDirectoryName(r.To), _lost2.Path)).Select(r => Path.GetFileName(r.From)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnImageTwoEpisodesRead_GoesToTheLongestStem()
    {
        // 10.11.5 reads "Ep 5-thumb.jpg" for Ep 5 (its thumb) and for Ep 5-thumb (its own): as beside the video, the
        // longest stem owns it, so it moves with Ep 5-thumb and never with Ep 5.
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        var thumbEpisode = _w.Episode(_lost, _lost1, "Ep 5-thumb.mkv", 1);
        var metadata = Path.Join(_lost1.Path, "metadata");
        _w.File(Path.Join(metadata, "Ep 5.jpg"));
        _w.File(Path.Join(metadata, "Ep 5-thumb.jpg"));
        _w.File(Path.Join(metadata, "Ep 5-thumb-thumb.jpg"));

        string[] Images(MoveUnit unit) => unit.Renames.Where(r => PathRules.SamePath(Path.GetDirectoryName(r.From), metadata)).Select(r => Path.GetFileName(r.From)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Ep 5.jpg"], Images(_w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2")));
        Assert.Equal(["Ep 5-thumb-thumb.jpg", "Ep 5-thumb.jpg"], Images(_w.Plan(thumbEpisode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2")));
    }

    [Fact]
    public void IntoANewSeasonFolder_TheMetadataFolderIsMadeBelowIt_TopMostFirst()
    {
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        _w.File(Path.Join(_lost1.Path, "metadata", "Ep 5.jpg"));

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost", "Season 3");

        var season = Path.Join(_lost.Path, "Season 3");
        Assert.Equal([season, Path.Join(season, "metadata")], unit.CreatedDirectories);
        Assert.Contains(unit.Renames, r => r.To == Path.Join(season, "metadata", "Ep 5.jpg"));
    }

    [Fact]
    public void AnExistingMetadataFolder_IsUsed_AndAnImageOfTheSameNameThere_IsACollision()
    {
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        _w.File(Path.Join(_lost1.Path, "metadata", "Ep 5.jpg"));
        _w.File(Path.Join(_lost1.Path, "metadata", "Ep 5-thumb.jpg"));
        var target = Path.Join(_lost2.Path, "metadata");
        _w.File(Path.Join(target, "Ep 6.jpg"));

        var free = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");
        Assert.Empty(free.Blockers);
        Assert.Empty(free.CreatedDirectories);
        Assert.Equal(2, free.Renames.Count(r => PathRules.SamePath(Path.GetDirectoryName(r.To), target)));

        _w.File(Path.Join(target, "EP 5.JPG"));
        var taken = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");
        var blocker = Assert.Single(taken.Blockers);
        Assert.Equal((MoveErrorCodes.Collision, Path.Join(target, "EP 5.JPG")), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Equal(Path.Join(target, "EP 5.JPG") + " exists already; Move never overwrites.", blocker.Message);
    }

    [Fact]
    public void AMetadataFolderOfAnotherCase_BesideTheEpisode_IsNotJellyfinsSo_ItsImagesStayWithAWarning()
    {
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        var other = Path.Join(_lost1.Path, "Metadata");
        _w.File(Path.Join(other, "Ep 5.jpg"));

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");

        Assert.Empty(unit.Blockers);
        Assert.Empty(unit.CreatedDirectories);
        Assert.DoesNotContain(unit.Renames, r => r.From.Contains("etadata", StringComparison.OrdinalIgnoreCase));
        var warning = Assert.Single(unit.Warnings, w => w.Code == MoveErrorCodes.SidecarLeftBehind);
        Assert.Equal([Path.Join(other, "Ep 5.jpg")], warning.Paths);
        Assert.Contains("named \"metadata\", in lower case", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMetadataFolderOfAnotherCase_AtTheTarget_LeavesTheImagesWithAWarning()
    {
        // "metadata" can't be made beside "Metadata", and Jellyfin wouldn't read the images in "Metadata".
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        _w.File(Path.Join(_lost1.Path, "metadata", "Ep 5.jpg"));
        _w.Folder("TV", "Lost", "Season 2", "Metadata");

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");

        Assert.Empty(unit.Blockers);
        Assert.Empty(unit.CreatedDirectories);
        Assert.DoesNotContain(unit.Renames, r => r.From.Contains("metadata", StringComparison.OrdinalIgnoreCase));
        var warning = Assert.Single(unit.Warnings, w => w.Code == MoveErrorCodes.SidecarLeftBehind);
        Assert.Equal([Path.Join(_lost1.Path, "metadata", "Ep 5.jpg")], warning.Paths);
        Assert.Contains(Path.Join(_lost2.Path, "Metadata") + " is named in another case", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileNamedMetadata_AtTheTarget_IsACollision()
    {
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        _w.File(Path.Join(_lost1.Path, "metadata", "Ep 5.jpg"));
        _w.File(Path.Join(_lost2.Path, "metadata"));

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.Collision, Path.Join(_lost2.Path, "metadata")), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Empty(unit.CreatedDirectories);
    }

    [Fact]
    public void ALinkedMetadataFolder_BesideTheEpisode_IsBlocked_AndNothingBelowItIsRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        var outside = _w.Folder("Outside", "images");
        _w.File(Path.Join(outside, "Ep 5.jpg"));
        var link = Path.Join(_lost1.Path, "metadata");
        Paths.Junction.Create(link, outside);

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.ReparsePoint, link), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Equal([link], unit.Links);
        Assert.DoesNotContain(unit.Entries, e => e.RelativePath.StartsWith("metadata", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(unit.CreatedDirectories);
    }

    [Fact]
    public async Task ALinkedMetadataFolder_AtTheTarget_IsBlocked_AndNeverAccessChecked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        _w.File(Path.Join(_lost1.Path, "metadata", "Ep 5.jpg"));
        var outside = _w.Folder("Outside", "images");
        var link = Path.Join(_lost2.Path, "metadata");
        Paths.Junction.Create(link, outside);

        var unit = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");
        var access = await Preflight(unit);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.ReparsePoint, link), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Contains(link, unit.Links);
        Assert.Equal([(_lost2.Path, true, false)], access);
        Assert.DoesNotContain(_w.Volumes.Asked, p => PathRules.IsSameOrUnder(p, link));
    }

    [Fact]
    public async Task TheWriteCheck_AsksAnExistingMetadataFolderForFiles_AndTheSeasonForAFolderWhenOneIsMade()
    {
        var episode = _w.Episode(_lost, _lost1, "Ep 5.mkv", 1);
        _w.File(Path.Join(_lost1.Path, "metadata", "Ep 5.jpg"));

        var made = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");
        Assert.Equal([(_lost2.Path, true, true)], await Preflight(made));

        var target = _w.Folder("TV", "Lost", "Season 2", "metadata");
        var existing = _w.Plan(episode, _w.TvLibrary, _w.TvLibrary, "Lost/Season 2");
        Assert.Equal([(_lost2.Path, true, false), (target, true, false)], await Preflight(existing));
        Assert.Empty(existing.Blockers);
    }

    [Fact]
    public void AMoviesMetadataFolder_IsNeverRead_ForItsImages()
    {
        // Only EpisodeLocalImageProvider reads it; a movie in a mixed folder has no such images.
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.mkv"), "Heat", 1995, true);
        _w.File(Path.Join(_w.Movies, "metadata", "Heat.jpg"));

        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary, wrap: false);

        Assert.Equal(["Heat.mkv"], unit.Renames.Select(r => Path.GetFileName(r.From)));
        Assert.Empty(unit.CreatedDirectories);
    }

    private async Task<List<(string Directory, bool Files, bool Folders)>> Preflight(MoveUnit unit)
    {
        var sessions = new Mock<ISessionManager>();
        sessions.SetupGet(s => s.Sessions).Returns([]);
        using var gate = new LibraryOperationGate(_w.Library.Object);
        var access = new RecordingAccess();
        await new MovePreflight(sessions.Object, gate, new FileLockProbe(static (_, _) => Task.CompletedTask), access)
            .CheckAsync(unit, DestinationWriteCheck.Access, "test", null, TestContext.Current.CancellationToken);
        return access.Asked;
    }

    private sealed class RecordingAccess : IFolderWriteAccess
    {
        public List<(string Directory, bool Files, bool Folders)> Asked { get; } = [];

        public bool CanAdd(string directory, bool files, bool folders)
        {
            Asked.Add((directory, files, folders));
            return true;
        }
    }
}
