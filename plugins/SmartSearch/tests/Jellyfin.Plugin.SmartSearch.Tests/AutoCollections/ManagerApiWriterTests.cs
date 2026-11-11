using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Configuration;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The writer through the server's own collection calls: one remove and one add at most, never empty, never with a dead
/// link (those are counted and cause no write, run after run); the query's order becomes the name's; the edit listener
/// sees the writes as the plugin's own; the direct writer prunes the dead links again once it is chosen.
/// </summary>
public sealed class ManagerApiWriterTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";
    private const string Recent = """{"Field":"Year","Op":"Ge","Value":2006}""";

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly TempDirectory _folder = new();
    private readonly RecordingPersistence _persistence = new();

    public ManagerApiWriterTests()
    {
        _harness = new EngineHarness(_world);
        AutoCollectionHost.SetUpLibrary(_world, _persistence);
    }

    public void Dispose()
    {
        _harness.Dispose();
        _folder.Dispose();
    }

    [Theory]
    [InlineData("ManagerApi", true)]
    [InlineData("managerapi", true)]
    [InlineData("DirectReplace", false)]
    [InlineData("Auto", false)]
    [InlineData("Bogus", false)]
    [InlineData(null, false)]
    public void TheSetting_ChoosesTheWriter_AutoBeingTheDirectOneOnThisServer(string? value, bool managerApi)
    {
        Assert.Equal("Auto", new PluginConfiguration().CollectionWriter);
        Assert.Equal(managerApi ? CollectionWriterKind.ManagerApi : CollectionWriterKind.DirectReplace, AutoCollectionSettings.From(new PluginConfiguration { CollectionWriter = value! }).CollectionWriter);
    }

    [Fact]
    public async Task Adds_AndRemoves_OnceEach_NeverADeadLink_WhichCausesNoWrites_UntilTheDirectWriterPrunesIt()
    {
        var a = Movie("A");
        var b = Movie("B");
        var c = Movie("C");
        using var host = Host();
        host.Settings = host.Settings with { CollectionWriter = CollectionWriterKind.ManagerApi };
        var admin = _world.Admin;
        var writing = new List<bool>();
        _persistence.DuringCall = id => writing.Add(host.Suppression.IsWriting(id));

        // Created through the server's create, sorted by name: the query's order can't be kept by appending.
        var created = await host.CreateAsync(admin, true, AutoCollectionHost.Def("Keepers", Sorted(), "QueryOrder"));
        var box = Assert.Single(_persistence.Boxes);
        Assert.Equal("SortName", box.DisplayOrder);
        Assert.Equal(["Create", "Save"], Take());
        Assert.Empty(Take(await host.RefreshAsync(admin, true, created.Id)));

        // One member gone (a dead link) and one new: only the add is called; the dead link stays, counted, no save.
        var d = Movie("D");
        _world.Core.Remove(b);
        var updated = await host.RefreshAsync(admin, true, created.Id);
        Assert.Equal(["Add"], Take());
        Assert.Equal([d.Id], Assert.Single(_persistence.Added));
        Assert.Empty(_persistence.Removed);
        Assert.Equal(1, updated.State.UnprunableDeadLinks);
        Assert.Equal(0, updated.State.LastDeadLinks);
        Assert.Equal(4, box.LinkedChildren.Length);

        // The dead link alone is no change: run after run, nothing is called or saved.
        for (var i = 0; i < 3; i++)
        {
            var again = await host.RefreshAsync(admin, true, created.Id);
            Assert.Empty(Take());
            Assert.Equal(1, again.State.UnprunableDeadLinks);
        }

        // A member excluded: one remove with that member only, then the rating and presentation saved.
        var changes = new SmartSearchAutoCollectionDefinitionDto { ManualEdits = new SmartSearchAutoCollectionManualEditsDto { ExcludedItemIds = [a.Id] } };
        var excluded = await host.UpdateAsync(admin, true, created.Id, changes, updated.UpdatedUtc);
        Assert.Equal(["Remove", "Rating", "Save"], Take());
        Assert.Equal([a.Id], Assert.Single(_persistence.Removed));
        Assert.DoesNotContain(Guid.Empty, _persistence.Removed.SelectMany(r => r));
        Assert.Equal(1, excluded.State.LastRemoved);
        Assert.All(writing, Assert.True);
        Assert.Equal(2, writing.Count);

        // The direct writer chosen again: its write prunes the dead link (and puts the query's order back).
        host.Settings = host.Settings with { CollectionWriter = CollectionWriterKind.DirectReplace };
        var direct = await host.RefreshAsync(admin, true, created.Id);
        Assert.Equal(["Rating", "Save", "Refresh"], Take());
        Assert.Equal([c.Id, d.Id], box.LinkedChildren.Select(l => l.ItemId!.Value).ToList());
        Assert.Equal("Default", box.DisplayOrder);
        Assert.Equal(1, direct.State.LastDeadLinks);
        Assert.Equal(0, direct.State.UnprunableDeadLinks);
    }

    [Fact]
    public async Task HeldRemovals_AreNotPassed_TheAddsAre()
    {
        var movies = Enumerable.Range(0, 10).Select(i => Movie("M" + i, 2000 + i)).ToList();
        using var host = Host();
        host.Settings = host.Settings with { CollectionWriter = CollectionWriterKind.ManagerApi };
        var admin = _world.Admin;
        var created = await host.CreateAsync(admin, true, AutoCollectionHost.Def("Keepers", Sorted()));
        Take();

        // During a scan, a change saved without its preview would take 6 of 10 and add a pinned one: the removals are held.
        host.ScanRunning = true;
        var n = Movie("N", 1990);
        var changes = AutoCollectionHost.Def(null, Sorted(Keep + "," + Recent));
        changes.ManualEdits = new SmartSearchAutoCollectionManualEditsDto { PinnedItemIds = [n.Id] };
        var held = await host.UpdateAsync(admin, true, created.Id, changes, created.UpdatedUtc);
        Assert.Equal("Guarded", held.State.Status);
        Assert.Equal(6, held.State.PendingGuard!.WouldRemoveCount);
        Assert.Equal(["Add"], Take());
        Assert.Equal([n.Id], Assert.Single(_persistence.Added));
        Assert.Empty(_persistence.Removed);
        Assert.Equal(11, _persistence.Boxes.Single().LinkedChildren.Length);
    }

    private static string Sorted(string rules = Keep) => QueryPlannerTests.Q(rules, "\"Movie\"", extra: ",\"Sort\":[{\"Key\":\"SortName\",\"Order\":\"Ascending\"}]");

    private AutoCollectionHost Host()
    {
        var users = new Mock<IUserManager>();
        var all = new[] { _world.Admin, _world.Kid };
        users.SetupGet(m => m.Users).Returns(all);
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => all.FirstOrDefault(u => u.Id.Equals(id)));
        return new AutoCollectionHost(_world, _harness, _folder.Combine("data"), _persistence, users.Object);
    }

    private List<string> Take(object? after = null)
    {
        _ = after;
        var calls = _persistence.Calls.ToList();
        _persistence.Calls.Clear();
        return calls;
    }

    private FakeRow Movie(string name, int year = 2000)
        => _world.Add(BaseItemKind.Movie, name, _world.Movies, s =>
        {
            s.Year = year;
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });
}
