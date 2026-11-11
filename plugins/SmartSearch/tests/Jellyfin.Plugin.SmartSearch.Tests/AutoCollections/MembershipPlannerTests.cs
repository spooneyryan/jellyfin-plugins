using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// A definition's members: the query in the creator's libraries, the excluded items out, the pins after the query's order
/// (only those inside the creator's libraries), the limit before the cap; the mixed-audience check; and the preview,
/// whose samples show only what the caller may open.
/// </summary>
public sealed class MembershipPlannerTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly MembershipPlanner _planner;
    private readonly TempDirectory _folder = new();

    public MembershipPlannerTests()
    {
        _harness = new EngineHarness(_world);
        _planner = new MembershipPlanner(_harness.Engine, _world.Core.Access(), _world.Core.Library.Object);
    }

    public void Dispose()
    {
        _harness.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task Pins_FollowTheQueryOrder_AndExcludedItemsWinOverBoth()
    {
        var a = Movie("A");
        var b = Movie("B");
        var c = Movie("C");
        var pinned = Movie("Pinned", overview: "other");
        var pinnedAndExcluded = Movie("Pinned and excluded", overview: "other");
        var definition = Edits(pins: [pinned.Id, a.Id, pinnedAndExcluded.Id], excludes: [b.Id, pinnedAndExcluded.Id]);

        var desired = await PlanAsync(definition, Sorted(Keep), _world.Admin);

        Assert.True(desired.Ok);
        Assert.Equal([a.Id, c.Id, pinned.Id], desired.Ids);
        Assert.Equal(3, desired.QueryCount);
        Assert.Equal(1, desired.ExcludedCount);
        Assert.Equal(1, desired.PinsAdded);
        Assert.Equal(0, desired.PinsSkipped);
    }

    [Fact]
    public async Task ExcludedItemsGoFirst_ThenTheLimitCuts_SoTheNextOnesMoveUp_ThenPinsFollow_ThenTheCapRefuses()
    {
        var films = Enumerable.Range(0, 5).Select(i => Movie("F" + i)).ToList();
        var pin = Movie("Pin", overview: "other");
        var definition = Edits(pins: [pin.Id], excludes: [films[1].Id, films[4].Id]);

        var desired = await PlanAsync(definition, Sorted(Keep, ",\"Limit\":3"), _world.Admin);
        var capped = await PlanAsync(definition, Sorted(Keep, ",\"Limit\":3"), _world.Admin, maxMembers: 3);
        var noExcludes = await PlanAsync(Edits(), Sorted(Keep, ",\"Limit\":3"), _world.Admin);

        Assert.Equal([films[0].Id, films[2].Id, films[3].Id, pin.Id], desired.Ids);
        Assert.Equal(1, desired.ExcludedCount);
        Assert.Equal(4, desired.QueryCount);
        Assert.True(desired.Ok);
        Assert.Equal(AutoCollectionReasons.TooManyMembers, capped.Error);
        Assert.Equal([films[0].Id, films[1].Id, films[2].Id], noExcludes.Ids);
    }

    [Fact]
    public async Task APinOfAKindACollectionDoesntHold_IsLeftOut()
    {
        var film = Movie("Pinned film", overview: "other");
        var legacy = _world.Add(BaseItemKind.BoxSet, "Legacy box set", _world.Movies, s => s.Overview = "other");

        var desired = await PlanAsync(Edits(pins: [legacy.Id, film.Id]), Sorted(Keep), _world.Admin);

        Assert.Equal([film.Id], desired.Ids);
        Assert.Equal(1, desired.PinsSkipped);
    }

    [Fact]
    public async Task TheQuery_ReadsOnlyTheCreatorsLibraries()
    {
        var mine = Movie("Movies film");
        Movie("Private film", _world.Private);

        var kid = await PlanAsync(Edits(), Sorted(Keep), _world.Kid);
        var admin = await PlanAsync(Edits(), Sorted(Keep), _world.Admin);

        Assert.Equal([mine.Id], kid.Ids);
        Assert.Equal(2, admin.Ids.Count);
        Assert.DoesNotContain(_world.Collections.Id, admin.LibraryIds);
    }

    [Fact]
    public async Task PinsOutsideTheCreatorsLibraries_OrGone_AreSkipped()
    {
        var inMovies = Movie("Pinned in Movies", overview: "other");
        var inPrivate = Movie("Pinned in Private", _world.Private, overview: "other");
        var definition = Edits(pins: [inPrivate.Id, Guid.NewGuid(), inMovies.Id]);

        var desired = await PlanAsync(definition, Sorted(Keep), _world.Kid);

        Assert.Equal([inMovies.Id], desired.Ids);
        Assert.Equal(1, desired.PinsAdded);
        Assert.Equal(2, desired.PinsSkipped);
    }

    [Fact]
    public async Task DetachedPinsAndExcludes_DontCount()
    {
        var a = Movie("A");
        var pin = Movie("Pin", overview: "other");
        var definition = AutoCollectionTestData.Definition(_world.Admin.Id, null) with
        {
            ManualEdits = new AutoCollectionManualEdits
            {
                Pinned = [new AutoCollectionItemEntry { ItemId = pin.Id, DetachedUtc = PlannerWorld.Now }],
                Excluded = [new AutoCollectionItemEntry { ItemId = a.Id, DetachedUtc = PlannerWorld.Now }]
            }
        };

        var desired = await PlanAsync(definition, Sorted(Keep), _world.Admin);

        Assert.Equal([a.Id], desired.Ids);
    }

    [Fact]
    public async Task TheRatingsViewer_IsTheEvaluationUser_ElseTheCreator()
    {
        var facts = new ViewerFacts();
        var world = new PlannerWorld();
        using var harness = new EngineHarness(world, TestFacts.Registry, [facts]);
        var planner = new MembershipPlanner(harness.Engine, world.Core.Access(), world.Core.Library.Object);
        world.Add(BaseItemKind.Movie, "Film", world.Movies, s => { s.OfficialRating = "PG"; s.InheritedRating = 10; });
        var query = harness.Read(PlannerQ("""{"Field":"TestFact","Op":"Ge","Value":0}"""));
        var definition = AutoCollectionTestData.Definition(world.Admin.Id, null);

        await planner.PlanAsync(definition, Check(query, world.Admin), EvaluationPurpose.AutoCollection, null, 100, TestContext.Current.CancellationToken);
        await planner.PlanAsync(definition, Check(query, world.Admin, world.Kid), EvaluationPurpose.AutoCollection, null, 100, TestContext.Current.CancellationToken);

        Assert.Equal([world.Admin.Id, world.Kid.Id], facts.Viewers);
    }

    [Fact]
    public void MixedAudience_CountsWhoSeesTheCollectionButNotAllOfIt()
    {
        var movies = Movie("In Movies");
        var hidden = Movie("In Private", _world.Private);
        var restricted = PlannerWorld.NewUser("restricted");
        _world.Core.Restrict(restricted, _world.Movies);
        var disabled = PlannerWorld.NewUser("disabled");
        disabled.SetPermission(PermissionKind.IsDisabled, true);
        _world.Core.Restrict(disabled, _world.Movies, _world.Collections);
        var check = new MixedAudienceCheck(_world.Core.Access(), Users(_world.Admin, _world.Kid, restricted, disabled).Object);

        // Kid sees the collections library but not Private; "restricted" doesn't see collections at all.
        Assert.Equal(1, check.CountPartialViewers([movies.Id, hidden.Id], CancellationToken.None));
        Assert.Equal(0, check.CountPartialViewers([movies.Id], CancellationToken.None));
        Assert.Equal(0, check.CountPartialViewers([], CancellationToken.None));
    }

    [Fact]
    public void MixedAudience_AsksAUserWithoutRulesOnlyAboutTheirLibraries()
    {
        var movies = Movie("In Movies");
        var hidden = Movie("In Private", _world.Private);
        var plain = PlannerWorld.NewUser("plain");
        _world.Core.Restrict(plain, _world.Movies, _world.Collections);
        var check = new MixedAudienceCheck(_world.Core.Access(), Users(_world.Admin, plain).Object, _world.Core.Library.Object);

        Assert.Equal(1, check.CountPartialViewers([movies.Id, hidden.Id], CancellationToken.None));
        Assert.Equal(0, check.CountPartialViewers([movies.Id], CancellationToken.None));
        Assert.Equal(1, check.CountPartialViewers([movies.Id, hidden.Id], CancellationToken.None, [_world.Movies.Id, _world.Private.Id]));

        // Every library the members come from is theirs: nothing is asked, and nothing is loaded for either user.
        var before = _world.Core.Library.Invocations.Count;
        Assert.Equal(0, check.CountPartialViewers([movies.Id], CancellationToken.None, [_world.Movies.Id]));
        Assert.DoesNotContain(_world.Core.Library.Invocations.Skip(before), i => i.Method.Name is "GetItemIds" or "GetItemList");
    }

    [Fact]
    public void MixedAudience_KeepsAUserWithRulesAnswers_ForThePass()
    {
        var movies = Movie("In Movies");
        var other = Movie("Also in Movies");
        var check = new MixedAudienceCheck(_world.Core.Access(), Users(_world.Kid).Object, _world.Core.Library.Object);
        var memo = new AudienceMemo();

        var expected = check.CountPartialViewers([movies.Id, other.Id], CancellationToken.None);
        var first = check.CountPartialViewers([movies.Id, other.Id], CancellationToken.None, [_world.Movies.Id], memo);
        var before = _world.Core.Library.Invocations.Count(i => i.Method.Name == "GetItemList");
        var second = check.CountPartialViewers([other.Id, movies.Id], CancellationToken.None, [_world.Movies.Id], memo);

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
        Assert.Equal(before, _world.Core.Library.Invocations.Count(i => i.Method.Name == "GetItemList"));
    }

    [Fact]
    public async Task ThePreview_EqualsTheSearchAsAnAdministrator_AndSamplesOnlyWhatTheCallerSees()
    {
        var movies = Enumerable.Range(0, 3).Select(i => Movie("Film " + i)).ToList();
        var show = _world.Add(BaseItemKind.Series, "Show", _world.Shows, s => { s.Overview = "keep"; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        var manager = PlannerWorld.NewUser("manager");
        manager.SetPermission(PermissionKind.EnableCollectionManagement, true);
        _world.Core.Restrict(manager, _world.Movies, _world.Collections);
        var query = PlannerQ(Keep, "\"Movie\",\"Series\"");
        using var service = new PreviewHost(this, manager);

        var admin = await service.PreviewAsync(_world.Admin, true, query);
        var search = await _harness.SearchAll(_harness.Read(query), _world.Admin);
        Assert.Equal(search.Ids.Order(), admin.Ids.Order());
        Assert.Equal(4, admin.Dto.Total);
        Assert.Equal(4, admin.Dto.AddSamples.Count);
        Assert.Equal(0, admin.Dto.HiddenFromCaller);
        Assert.StartsWith("sha256:", admin.Dto.PreviewHash, StringComparison.Ordinal);

        // An administrator's definition previewed by a Movies-only manager: no Shows samples, the show counted as hidden.
        var saved = await service.SaveAsync(AutoCollectionTestData.Definition(_world.Admin.Id, null) with
        {
            Source = new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Snapshot, Snapshot = System.Text.Json.JsonDocument.Parse(query).RootElement.Clone() }
        });
        var forManager = await service.Service.PreviewAsync(manager, false, new SmartSearchAutoCollectionPreviewRequestDto { DefinitionId = saved.Id }, CancellationToken.None);
        Assert.Equal(4, forManager.Total);
        Assert.Equal(movies.Select(m => m.Id).Order(), forManager.AddSamples.Select(d => d.Id).Order());
        Assert.DoesNotContain(show.Id, forManager.AddSamples.Select(d => d.Id));
        Assert.Equal(1, forManager.HiddenFromCaller);
        Assert.True(forManager.MixedAudience);
    }

    [Fact]
    public async Task ThePreview_RefusesNonManagers_OthersSources_AndAnInvalidDefinition()
    {
        var viewer = PlannerWorld.NewUser("viewer");
        var manager = PlannerWorld.NewUser("manager");
        manager.SetPermission(PermissionKind.EnableCollectionManagement, true);
        using var host = new PreviewHost(this, manager, viewer);
        var saved = await host.SaveAsync(AutoCollectionTestData.Definition(_world.Admin.Id, null) with
        {
            Source = new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Snapshot, Snapshot = System.Text.Json.JsonDocument.Parse(PlannerQ(Keep)).RootElement.Clone() }
        });

        var notManager = await Assert.ThrowsAsync<PluginException>(() => host.PreviewAsync(viewer, false, PlannerQ(Keep)));
        Assert.Equal(AutoCollectionErrors.ManagerRequiredCode, notManager.Code);

        var notCreator = await Assert.ThrowsAsync<PluginException>(() => host.Service.PreviewAsync(manager, false, new SmartSearchAutoCollectionPreviewRequestDto { DefinitionId = saved.Id, Definition = AutoCollectionHost.Def(null, PlannerQ("""{"Field":"Year","Op":"Eq","Value":2000}""")) }, CancellationToken.None));
        Assert.Equal(AutoCollectionErrors.NotCreatorCode, notCreator.Code);

        var missing = await Assert.ThrowsAsync<PluginException>(() => host.Service.PreviewAsync(manager, false, new SmartSearchAutoCollectionPreviewRequestDto { DefinitionId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Equal(PluginErrorKind.NotFound, missing.Kind);

        var boxSets = await Assert.ThrowsAsync<PluginException>(() => host.PreviewAsync(manager, false, PlannerQ(Keep, "\"Movie\",\"BoxSet\"")));
        Assert.Equal(AutoCollectionReasons.BoxSetKind, boxSets.Code);
        Assert.Equal(PluginErrorKind.Invalid, boxSets.Kind);

        var noQuery = await Assert.ThrowsAsync<PluginException>(() => host.Service.PreviewAsync(manager, false, new SmartSearchAutoCollectionPreviewRequestDto { Definition = AutoCollectionHost.Def(null, savedQueryId: Guid.NewGuid()) }, CancellationToken.None));
        Assert.Equal(AutoCollectionReasons.QueryMissing, noQuery.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Definition\":{}}")]
    [InlineData("{\"Definition\":{\"Source\":{\"SavedQueryId\":\"11111111-1111-1111-1111-111111111111\",\"Query\":{}}}}")]
    [InlineData("{\"DefinitionId\":\"x\"}")]
    [InlineData("{\"DefinitionId\":\"11111111-1111-1111-1111-111111111111\",\"Definition\":{\"ManualEdits\":{\"PinnedItemIds\":[\"x\"]}}}")]
    [InlineData("{\"DefinitionId\":\"11111111-1111-1111-1111-111111111111\",\"Other\":1}")]
    [InlineData("{\"SavedQueryId\":\"11111111-1111-1111-1111-111111111111\"}")]
    public void ThePreviewBody_IsReadStrictly(string body)
    {
        var error = Assert.Throws<PluginException>(() => Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.ReadPreview(System.Text.Encoding.UTF8.GetBytes(body)));

        Assert.Equal(Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.InvalidRequestCode, error.Code);
    }

    private static string PlannerQ(string rules, string kinds = "\"Movie\"", string extra = "") => QueryPlannerTests.Q(rules, kinds, extra: extra);

    private static string Sorted(string rules, string extra = "") => PlannerQ(rules, extra: ",\"Sort\":[{\"Key\":\"SortName\",\"Order\":\"Ascending\"}]" + extra);

    private static AutoCollectionCheck Check(SmartSearchQueryDefinition query, User creator, User? evaluationUser = null)
        => new(SmartSearchAutoCollectionStatus.Ok, null, [], query, "sha256:test", evaluationUser is not null, creator, evaluationUser, false);

    private static Mock<IUserManager> Users(params User[] users)
    {
        var mock = new Mock<IUserManager>();
        mock.SetupGet(m => m.Users).Returns(users);
        mock.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => users.FirstOrDefault(u => u.Id.Equals(id)));
        return mock;
    }

    private AutoCollectionDefinition Edits(IReadOnlyList<Guid>? pins = null, IReadOnlyList<Guid>? excludes = null)
        => AutoCollectionTestData.Definition(_world.Admin.Id, null) with
        {
            ManualEdits = new AutoCollectionManualEdits
            {
                Pinned = (pins ?? []).Select(id => new AutoCollectionItemEntry { ItemId = id }).ToList(),
                Excluded = (excludes ?? []).Select(id => new AutoCollectionItemEntry { ItemId = id }).ToList()
            }
        };

    private Task<DesiredMembership> PlanAsync(AutoCollectionDefinition definition, string query, User creator, int maxMembers = 5000)
        => _planner.PlanAsync(definition, Check(_harness.Read(query), creator), EvaluationPurpose.AutoCollection, null, maxMembers, TestContext.Current.CancellationToken);

    private FakeRow Movie(string name, CollectionFolder? library = null, string overview = "keep")
        => _world.Add(BaseItemKind.Movie, name, library ?? _world.Movies, s =>
        {
            s.Year = 2000;
            s.Overview = overview;
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });

    private sealed class ViewerFacts : IItemFactsSource
    {
        public List<Guid?> Viewers { get; } = [];

        public bool Answers(string fieldId) => fieldId == TestFacts.FieldId;

        public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> Load(IReadOnlyList<BaseItem> items, IReadOnlyCollection<string> fieldIds, Guid? viewerId, CancellationToken cancellationToken)
        {
            Viewers.Add(viewerId);
            return items.ToDictionary(i => i.Id, TestFacts.For);
        }
    }

    // The preview service over the test world, with a store of its own; no collection carries a mark here.
    private sealed class PreviewHost : IDisposable
    {
        private readonly AutoCollectionHost _host;

        public PreviewHost(MembershipPlannerTests test, params User[] more)
        {
            var world = test._world;
            var users = Users([world.Admin, world.Kid, .. more]);
            world.Core.Library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.HasAnyProviderId != null && q.HasAnyProviderId.Count > 0))).Returns(new List<BaseItem>());
            _host = new AutoCollectionHost(world, test._harness, test._folder.Combine("data"), new RecordingPersistence(), users.Object);
        }

        public AutoCollectionService Service => _host.Service;

        public async Task<(SmartSearchAutoCollectionPreviewDto Dto, IReadOnlyList<Guid> Ids)> PreviewAsync(User caller, bool isAdmin, string query)
        {
            var dto = await Service.PreviewAsync(caller, isAdmin, new SmartSearchAutoCollectionPreviewRequestDto { Definition = AutoCollectionHost.Def(null, query) }, CancellationToken.None);
            return (dto, dto.AddSamples.Select(d => d.Id).ToList());
        }

        public async Task<AutoCollectionDefinition> SaveAsync(AutoCollectionDefinition definition)
        {
            await _host.Store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [.. d.Definitions, definition]), CancellationToken.None);
            return definition;
        }

        public void Dispose() => _host.Dispose();
    }
}
