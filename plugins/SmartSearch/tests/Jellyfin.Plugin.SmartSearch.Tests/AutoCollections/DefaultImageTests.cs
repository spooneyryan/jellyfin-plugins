using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Configuration;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;
using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The default poster of an auto-collection's collection: given through the server's image saving when a create or a
/// run reaches a collection without a poster, once per collection (the state records it), never over a poster the
/// collection has, never again after it is removed or replaced, not while the setting is off (which removes nothing),
/// and without touching the members, the edit listener or the collection writes.
/// </summary>
public sealed class DefaultImageTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly TempDirectory _folder = new();
    private readonly RecordingPersistence _persistence = new();

    public DefaultImageTests()
    {
        _harness = new EngineHarness(_world);
        AutoCollectionHost.SetUpLibrary(_world, _persistence);
    }

    private static CancellationToken Ct => CancellationToken.None;

    private static byte[] Embedded
    {
        get
        {
            using var stream = AutoCollectionDefaultImage.OpenEmbedded();
            Assert.NotNull(stream);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    public void Dispose()
    {
        _harness.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void TheEmbeddedPoster_IsThe1000x1500Png()
    {
        var bytes = Embedded;
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        Assert.Equal(1000, (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]);
        Assert.Equal(1500, (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
    }

    [Fact]
    public void TheSetting_IsOnByDefault_AndReachesTheRuns()
    {
        Assert.True(new PluginConfiguration().AutoCollectionDefaultImage);
        Assert.True(AutoCollectionSettings.From(new PluginConfiguration()).DefaultImage);
        Assert.False(AutoCollectionSettings.From(new PluginConfiguration { AutoCollectionDefaultImage = false }).DefaultImage);
    }

    [Fact]
    public async Task ACreatedCollection_GetsThePoster_ThroughTheImageSaving_WithoutAWriteAnEditOrAnotherRun()
    {
        var a = Movie("A");
        var b = Movie("B");
        using var host = Host();

        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));

        var box = Assert.Single(_persistence.Boxes);
        var saved = Assert.Single(host.Images.Saved);
        Assert.Equal(box.Id, saved.CollectionId);
        Assert.Equal(Embedded, saved.Bytes);
        Assert.Equal("image/png", saved.MimeType);
        Assert.True(box.HasImage(ImageType.Primary, 0));
        var record = host.State.Find(created.Id)!.DefaultImage!;
        Assert.Equal(box.Id, record.CollectionId);
        Assert.Equal(AutoCollectionDefaultImageRecord.Applied, record.Outcome);

        // The collection's own write only (the create and its save), no edit recorded, no other run asked for, and the
        // members and their hash as the run planned them.
        Assert.Equal(["Create", "Save"], _persistence.Calls);
        Assert.Equal(0, host.Listener.Recorded);
        var state = host.State.Find(created.Id)!;
        Assert.Empty(state.PendingEdits);
        Assert.Equal(MembershipHash.Of([a.Id, b.Id]), state.LastResultHash);
        Assert.Equal("Ok", created.State.Status);
        Assert.Single(host.Queue.Requests);
    }

    [Fact]
    public async Task ThePoster_IsGivenOnce_AndAPosterRemovedOrReplacedLater_IsLeftAsItIs()
    {
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        var box = Assert.Single(_persistence.Boxes);
        Assert.Single(host.Images.Saved);

        // Another run: nothing more.
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Single(host.Images.Saved);

        // Removed in the server's editor: it stays removed.
        box.ImageInfos = [];
        Movie("B");
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Single(host.Images.Saved);
        Assert.False(box.HasImage(ImageType.Primary, 0));

        // Replaced by the user's own: theirs stays.
        box.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = Path.Combine(_folder.Path, "theirs.jpg") }, 0);
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Single(host.Images.Saved);
        Assert.Equal(Path.Combine(_folder.Path, "theirs.jpg"), box.GetImagePath(ImageType.Primary, 0));
        Assert.Equal(AutoCollectionDefaultImageRecord.Applied, host.State.Find(created.Id)!.DefaultImage!.Outcome);
    }

    [Fact]
    public async Task ACollectionWithItsOwnPoster_KeepsIt_AndNeverGetsThisOne_EvenAfterItsOwnIsRemoved()
    {
        Movie("A");
        using var host = Host();
        host.Settings = host.Settings with { DefaultImage = false };
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        var box = Assert.Single(_persistence.Boxes);
        var theirs = Path.Combine(_folder.Path, "theirs.jpg");
        box.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = theirs }, 0);

        host.Settings = host.Settings with { DefaultImage = true };
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Empty(host.Images.Saved);
        Assert.Equal(theirs, box.GetImagePath(ImageType.Primary, 0));
        var record = host.State.Find(created.Id)!.DefaultImage!;
        Assert.Equal(AutoCollectionDefaultImageRecord.HadImage, record.Outcome);
        Assert.Equal(box.Id, record.CollectionId);

        box.ImageInfos = [];
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Empty(host.Images.Saved);
    }

    [Fact]
    public async Task ACollectionMadeWithoutThePoster_GetsItAtItsNextRun_AndTurningItOff_RemovesNothing()
    {
        Movie("A");
        using var host = Host();

        // As a collection made before this version (or while the setting was off): the state remembers nothing.
        host.Settings = host.Settings with { DefaultImage = false };
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        var box = Assert.Single(_persistence.Boxes);
        Assert.Empty(host.Images.Saved);
        Assert.Null(host.State.Find(created.Id)!.DefaultImage);
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Empty(host.Images.Saved);

        host.Settings = host.Settings with { DefaultImage = true };
        _persistence.Calls.Clear();
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Equal(box.Id, Assert.Single(host.Images.Saved).CollectionId);
        Assert.Empty(_persistence.Calls);

        // Off again: the poster stays, and nothing is given or recorded anew.
        host.Settings = host.Settings with { DefaultImage = false };
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.True(box.HasImage(ImageType.Primary, 0));
        Assert.Single(host.Images.Saved);
        Assert.Equal(AutoCollectionDefaultImageRecord.Applied, host.State.Find(created.Id)!.DefaultImage!.Outcome);
    }

    [Fact]
    public async Task AFailedSave_NeverFailsTheRun_AndIsTriedAgainAtTheNextOne()
    {
        Movie("A");
        using var host = Host();
        host.Images.Fails = _ => true;

        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        Assert.Equal("Ok", created.State.Status);
        Assert.Empty(host.Images.Saved);
        Assert.Null(host.State.Find(created.Id)!.DefaultImage);

        host.Images.Fails = _ => false;
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Single(host.Images.Saved);
    }

    [Fact]
    public async Task ARecreatedCollection_IsANewCollection_AndGetsThePoster()
    {
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        var first = Assert.Single(_persistence.Boxes);
        _persistence.Boxes.Clear();
        await host.RefreshAsync(_world.Admin, true, created.Id);

        var recreated = await host.Service.RecreateAsync(_world.Admin, true, created.Id, new SmartSearchAutoCollectionRecreateRequestDto(), Ct);

        var second = Assert.Single(_persistence.Boxes);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal([first.Id, second.Id], host.Images.Saved.Select(s => s.CollectionId));
        Assert.Equal(second.Id, host.State.Find(recreated.Id)!.DefaultImage!.CollectionId);
    }

    [Fact]
    public async Task TheRecord_SurvivesTheStateFile_AndKeepsWhatALaterVersionAdds()
    {
        Movie("A");
        var data = _folder.Combine("data");
        Guid id;
        Guid collection;
        using (var host = Host())
        {
            var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
            id = created.Id;
            collection = Assert.Single(_persistence.Boxes).Id;
        }

        var file = Path.Combine(data, AutoCollectionStateStore.FileName);
        var text = await File.ReadAllTextAsync(file, Ct);
        Assert.Contains("\"DefaultImage\"", text, StringComparison.Ordinal);
        await File.WriteAllTextAsync(file, text.Replace("\"Outcome\": \"Applied\"", "\"Outcome\": \"Applied\", \"Later\": 1", StringComparison.Ordinal), Ct);

        using var again = Host();
        var record = again.State.Find(id)!.DefaultImage!;
        Assert.Equal(collection, record.CollectionId);
        Assert.Equal(AutoCollectionDefaultImageRecord.Applied, record.Outcome);
        Assert.True(record.Extra!.ContainsKey("Later"));
        _persistence.Boxes[0].ImageInfos = [];
        await again.RefreshAsync(_world.Admin, true, id);
        Assert.Empty(again.Images.Saved);
        Assert.True(again.State.Find(id)!.DefaultImage!.Extra!.ContainsKey("Later"));
    }

    private static string Sorted(string rules)
        => QueryPlannerTests.Q(rules, "\"Movie\"", extra: ",\"Sort\":[{\"Key\":\"SortName\",\"Order\":\"Ascending\"}]");

    private AutoCollectionHost Host()
    {
        var users = new Mock<IUserManager>();
        var all = new[] { _world.Admin, _world.Kid };
        users.SetupGet(m => m.Users).Returns(all);
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => all.FirstOrDefault(u => u.Id.Equals(id)));
        return new AutoCollectionHost(_world, _harness, _folder.Combine("data"), _persistence, users.Object);
    }

    private FakeRow Movie(string name)
        => _world.Add(BaseItemKind.Movie, name, _world.Movies, s =>
        {
            s.Year = 2000;
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });
}
