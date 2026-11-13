using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Search;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// The <c>People</c> rule: people in a role or in any, matched any, all, none or not all; its pushdown on each line
/// (<c>PersonIds</c>, and on Jellyfin 12 <c>PersonTypes</c> beside it; 10.11 checks the role per item); a stale person
/// id resolved by name; <c>GET /SmartSearch/People</c>, which never suggests a person credited only on items the caller
/// can't see; and a saved People query and an auto-collection built from one.
/// </summary>
public sealed class PeopleTests
{
    private static string People(string op, string? role, params Person[] people) => People(op, role, people.Select(p => (p.Id, p.Name)).ToArray());

    private static string People(string op, string? role, params (Guid Id, string Name)[] people)
        => "{\"Field\":\"People\",\"Op\":\"" + op + "\",\"Values\":[" + string.Join(",", people.Select(p => "{\"Id\":\"" + p.Id + "\",\"Name\":" + JsonSerializer.Serialize(p.Name) + "}")) + "]"
            + (role is null ? string.Empty : ",\"Role\":\"" + role + "\"") + "}";

    private static List<string> Names(PlannerWorld world, IEnumerable<Guid> ids) => [.. ids.Select(id => world.Core.Rows.Single(r => r.Id == id).Item.Name).Order(StringComparer.Ordinal)];

    private static List<string?> Names(JsonElement people) => [.. people.EnumerateArray().Select(p => p.GetProperty("Name").GetString())];

    [Fact]
    public void ARole_IsMatchedOnTheSameCredit_AndPushedExactlyOnJellyfin12_CheckedPerItemOn1011()
    {
        var w = new Cast();

        var directed = w.World.Plan(QueryPlannerTests.Q(People("AnyOf", "Director", w.Ridley)), w.World.Admin);
        Assert.Equal([w.Ridley.Id], directed.CandidateQuery!.PersonIds);
        var step = Assert.Single(directed.Steps);
        if (JellyfinLine.Is12)
        {
            Assert.Equal(["Director"], directed.CandidateQuery.PersonTypes);
            Assert.Equal(PushdownExactness.Exact, step.Exactness);
            Assert.Equal(QueryPlanPath.Fast, directed.Path);
        }
        else
        {
            Assert.Empty(directed.CandidateQuery.PersonTypes);
            Assert.Equal(PushdownExactness.Superset, step.Exactness);
            Assert.NotEqual(QueryPlanPath.Fast, directed.Path);
        }

        // Ridley Scott directed Alien and produced Gladiator: as director, only Alien; in any role, both.
        Assert.Equal(["Alien"], Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(People("AnyOf", "Director", w.Ridley)))));
        Assert.Equal(["Alien"], Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(People("AnyOf", "Director", w.Ridley)), w.World.Admin)));
        Assert.Equal(["Alien", "Gladiator"], Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(People("AnyOf", null, w.Ridley)))));

        // 10.11 reads the credits of the role per item (GetPeople); 12 never needs to.
        w.World.Core.PeopleQueries.Clear();
        w.World.RunAndCheck(QueryPlannerTests.Q(People("AnyOf", "Producer", w.Ridley)), purpose: PlanPurpose.EvaluateAll);
        if (JellyfinLine.Is12)
        {
            Assert.Empty(w.World.Core.PeopleQueries);
        }
        else
        {
            Assert.NotEmpty(w.World.Core.PeopleQueries);
            Assert.All(w.World.Core.PeopleQueries, q => Assert.Equal(["Producer"], q.PersonTypes));
        }
    }

    [Fact]
    public void AnyAllNoneAndNotAll_MatchAsTheirNamesSay()
    {
        var w = new Cast();
        string Run(string rule) => string.Join(", ", Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(rule))));

        // Directed by Ridley Scott or Tony Scott: Exact without a role (PersonIds holds both).
        var either = w.World.Plan(QueryPlannerTests.Q(People("AnyOf", null, w.Ridley, w.Tony)));
        Assert.Equal([w.Ridley.Id, w.Tony.Id], either.CandidateQuery!.PersonIds);
        Assert.Equal(PushdownExactness.Exact, Assert.Single(either.Steps).Exactness);
        Assert.Equal("Alien, Top Gun", Run(People("AnyOf", "Director", w.Ridley, w.Tony)));

        // Robert De Niro and Al Pacino as actors: both credited, each as an actor.
        var both = w.World.Plan(QueryPlannerTests.Q(People("AllOf", "Actor", w.Pacino, w.DeNiro)));
        Assert.Equal(PushdownExactness.Superset, Assert.Single(both.Steps).Exactness);
        Assert.Equal([w.Pacino.Id], both.CandidateQuery!.PersonIds);
        Assert.Equal("Heat, The Godfather Part II", Run(People("AllOf", "Actor", w.Pacino, w.DeNiro)));
        Assert.Equal("Heat, Serpico, The Godfather Part II", Run(People("AnyOf", "Actor", w.Pacino, w.DeNiro)));

        // None and not all are never pushed.
        Assert.Equal(PushdownExactness.Declined, Assert.Single(w.World.Plan(QueryPlannerTests.Q(People("NoneOf", null, w.Pacino))).Steps).Exactness);
        Assert.Equal(PushdownExactness.Declined, Assert.Single(w.World.Plan(QueryPlannerTests.Q(People("NotAllOf", null, w.Pacino, w.DeNiro))).Steps).Exactness);
        Assert.Equal("Alien, Gladiator, Grim, Secret, Top Gun", Run(People("NoneOf", null, w.Pacino)));
        Assert.Equal("Alien, Gladiator, Grim, Secret, Serpico, Top Gun", Run(People("NotAllOf", "Actor", w.Pacino, w.DeNiro)));

        // A guest star is credited on the episode, a show's producers on the show.
        Assert.Equal("Pilot", string.Join(", ", Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(People("AnyOf", "GuestStar", w.Guest), "\"Episode\",\"Series\"")))));
        Assert.Equal("Numbers", string.Join(", ", Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(People("AllOf", "Producer", w.Ridley, w.Tony), "\"Series\"")))));
    }

    [Fact]
    public void APerson_IsFoundByIdThenByName_AndAnUnknownOne_IsCreditedOnNothing()
    {
        var w = new Cast();

        // People rebuilt: the stored id is gone, the name finds the new Person item.
        Assert.Equal(["Alien"], Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(People("AnyOf", "Director", (Guid.NewGuid(), "Ridley Scott"))))));
        w.World.Core.RemovePerson(w.Ridley);
        var reborn = w.World.Core.AddPerson("Ridley Scott");
        var stale = w.World.Plan(QueryPlannerTests.Q(People("AnyOf", "Director", w.Ridley)));
        Assert.Equal([reborn.Id], stale.CandidateQuery!.PersonIds);
        Assert.Equal(["Alien"], Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(People("AnyOf", "Director", w.Ridley)))));

        // Nobody of that id or name: any of them is empty (answered without a query), none of them everything.
        Assert.Equal(QueryPlanPath.Empty, w.World.Plan(QueryPlannerTests.Q(People("AnyOf", null, (Guid.NewGuid(), "Nobody")))).Path);
        Assert.Equal(QueryPlanPath.Empty, w.World.Plan(QueryPlannerTests.Q(People("AllOf", null, (w.Pacino.Id, w.Pacino.Name), (Guid.NewGuid(), "Nobody")))).Path);
        Assert.Equal(8, w.World.RunAndCheck(QueryPlannerTests.Q(People("NoneOf", null, (Guid.NewGuid(), "Nobody")))).Count);

        // The name is matched as core matches it (exactly): another case is another person.
        Assert.Equal(QueryPlanPath.Empty, w.World.Plan(QueryPlannerTests.Q(People("AnyOf", null, (Guid.NewGuid(), "ridley scott")))).Path);
    }

    [Fact]
    public void PeopleConstraints_CombineAsSupersets()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var director = new PeopleConstraint([a], "Director");
        var anyRole = new PeopleConstraint([a, b], null);

        Assert.True(director.Implies(anyRole));
        Assert.False(anyRole.Implies(director));
        Assert.False(new PeopleConstraint([a], "Actor").Implies(director));
        Assert.True(new PeopleConstraint([], null).Impossible);
        Assert.Same(director, director.And(anyRole));

        // A second impossible rule empties the plan, whichever side it is on.
        var nobody = new PeopleConstraint([], "Director");
        Assert.Same(nobody, director.And(nobody));
        Assert.Same(nobody, nobody.And(director));
        var or = (PeopleConstraint)director.Or(new PeopleConstraint([b], "Director"))!;
        Assert.Equal([a, b], or.Ids);
        Assert.Equal("Director", or.Role);
        Assert.Null(((PeopleConstraint)director.Or(new PeopleConstraint([b], "Writer"))!).Role);
        Assert.Equal([a], new PeopleConstraint([a, a], null).Ids);
    }

    [Fact]
    public async Task ThePicker_SuggestsOnlyPeopleCreditedOnWhatTheCallerCanSee()
    {
        var w = new Cast();
        var viewer = PlannerWorld.NewUser("viewer");
        w.World.Core.Restrict(viewer, w.World.Movies);
        await using var host = new SearchApiHost(w.World);
        host.Users.Add(viewer);
        await host.StartAsync();

        // Rob Morrow is only in Shows: the administrator and Kid get him, the viewer (Movies only) never does.
        Assert.Equal(["Rob Morrow", "Robert De Niro"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=rob", host.Admin)));
        Assert.Equal(["Rob Morrow", "Robert De Niro"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=rob", host.Kid)));
        Assert.Equal(["Robert De Niro"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=rob", viewer)));
        Assert.Empty(Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=morrow", viewer)));
        Assert.Empty(Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=guest", viewer)));

        // Kid can't open Private, and the R-rated film is above Kid's limit: neither person is suggested to Kid.
        Assert.Equal(["Secret Person"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=secret", host.Admin)));
        Assert.Empty(Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=secret", host.Kid)));
        Assert.Equal(["Grim Actor"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=grim", host.Admin)));
        Assert.Empty(Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=grim", host.Kid)));
        Assert.Empty(Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=grim", host.Twin)));

        // Those whose name starts with the text first, then by name; the limit after the order; the shape is { Id, Name }.
        var scott = await host.OkAsync("GET", "/SmartSearch/People?prefix=scott", host.Admin);
        Assert.Equal(["Ridley Scott", "Tony Scott"], Names(scott));
        Assert.Equal(["Id", "Name"], scott[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(w.Ridley.Id, Guid.Parse(scott[0].GetProperty("Id").GetString()!));
        Assert.Equal(["Rob Morrow", "Robert De Niro", "Al Ross"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=RO", host.Admin)));
        Assert.Equal(["Ridley Scott"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=scott&limit=1", host.Admin)));

        // Only the field's kinds the server allows count: with Series only, Alien's director isn't suggested.
        host.AllowedKinds = [BaseItemKind.Series];
        Assert.Equal(["Ridley Scott", "Tony Scott"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=scott", host.Admin)));
        Assert.Empty(Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=pacino", host.Admin)));
        host.AllowedKinds = QueryLimits.ItemKindOrder;
    }

    [Theory]
    [InlineData("/SmartSearch/People?prefix=r")]
    [InlineData("/SmartSearch/People?prefix=%20%20r%20")]
    [InlineData("/SmartSearch/People")]
    [InlineData("/SmartSearch/People?prefix=ro&limit=0")]
    [InlineData("/SmartSearch/People?prefix=ro&limit=101")]
    public async Task ThePicker_RefusesShortTextAndOddLimits(string path)
    {
        await using var host = await new SearchApiHost(new Cast().World).StartAsync();

        var (status, _, text) = await host.SendAsync("GET", path, host.Kid);

        Assert.True(status == HttpStatusCode.BadRequest, text);
    }

    [Fact]
    public async Task ThePicker_IsOff_WhileTheSearchPageIs()
    {
        await using var host = await new SearchApiHost(new Cast().World).StartAsync();
        host.Enabled = false;

        var (status, _, text) = await host.SendAsync("GET", "/SmartSearch/People?prefix=ro", host.Admin);

        Assert.True(status == HttpStatusCode.Forbidden, text);
    }

    [Fact]
    public async Task ASavedPeopleQuery_RunsAfterThePeopleAreRebuilt_AndAnAutoCollection_IsBuiltFromIt()
    {
        var w = new Cast();
        var manager = PlannerWorld.NewUser("manager");
        manager.SetPermission(PermissionKind.EnableCollectionManagement, true);
        manager.SetPermission(PermissionKind.EnableAllFolders, true);
        await using var host = new SearchApiHost(w.World);
        host.Users.Add(manager);
        host.UseAutoCollections();
        await host.StartAsync();

        var query = QueryPlannerTests.Q(People("AllOf", "Actor", w.Pacino, w.DeNiro));
        var (status, saved, text) = await host.SendAsync("POST", "/SmartSearch/Queries", manager, SavedQueryTestData.Body("Pacino and De Niro", query));
        Assert.True(status == HttpStatusCode.Created, text);
        var id = saved.GetProperty("Id").GetString();
        async Task<List<string>> RunSaved() => Names(w.World, (await host.OkAsync("POST", "/SmartSearch/Queries/" + id + "/Search", manager, "{}"))
            .GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)));

        Assert.Equal(["Heat", "The Godfather Part II"], await RunSaved());

        // The people are rebuilt: new Person ids, the saved ids are stale; the names still find them.
        w.World.Core.RemovePerson(w.Pacino);
        w.World.Core.RemovePerson(w.DeNiro);
        w.World.Core.AddPerson("Al Pacino");
        w.World.Core.AddPerson("Robert De Niro");
        Assert.Equal(["Heat", "The Godfather Part II"], await RunSaved());

        // An auto-collection from the saved query: the server's evaluation, every rule in memory.
        var definition = "{\"Name\":\"Pacino and De Niro\",\"Source\":{\"SavedQueryId\":\"" + id + "\"}}";
        var preview = await host.OkAsync("POST", "/SmartSearch/AutoCollections/Preview", manager, "{\"Definition\":" + definition + "}");
        var (created, _, createdText) = await host.SendAsync("POST", "/SmartSearch/AutoCollections", manager, "{\"Definition\":" + definition + ",\"AcceptedPreviewHash\":\"" + preview.GetProperty("PreviewHash").GetString() + "\"}");
        Assert.True(created == HttpStatusCode.Created, createdText);
        await host.AutoCollections!.Queue.WhenIdleAsync();

        var box = Assert.Single(host.Collections.Boxes);
        Assert.Equal(["Heat", "The Godfather Part II"], Names(w.World, box.LinkedChildren.Select(l => l.ItemId!.Value)));
    }

    [Fact]
    public void AStaleIdUnderTwoNames_IsTheSmallestName_InTheEvaluatorAsInTheCanonicalForm()
    {
        var w = new Cast();
        var stale = Guid.NewGuid();

        // The canonical form keeps the smallest name (ordinal) for a duplicated id; the evaluator and the pushdown agree,
        // whichever order the rule lists them in.
        var first = w.World.RunAndCheck(QueryPlannerTests.Q(People("AllOf", null, (stale, "Robert De Niro"), (stale, "Al Pacino"))));
        var second = w.World.RunAndCheck(QueryPlannerTests.Q(People("AllOf", null, (stale, "Al Pacino"), (stale, "Robert De Niro"))));
        Assert.Equal(["Heat", "Serpico", "The Godfather Part II"], Names(w.World, first));
        Assert.Equal(Names(w.World, first), Names(w.World, second));
        Assert.Equal(
            Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(People("NoneOf", null, (stale, "Al Pacino"), (stale, "Robert De Niro"))))),
            Names(w.World, w.World.RunAndCheck(QueryPlannerTests.Q(People("NoneOf", null, (stale, "Robert De Niro"), (stale, "Al Pacino"))))));
    }

    [Fact]
    public void APersonsItems_CountAgainstTheBudget_AndRefusePastIt()
    {
        var w = new Cast();
        PeopleRoleResolver Resolver(CandidateBudget budget, CancellationToken token)
            => new(w.World.Core.Library.Object, w.World.Admin, [BaseItemKind.Movie, BaseItemKind.Series], [w.World.Movies.PhysicalFolderIds[0], w.World.Shows.PhysicalFolderIds[0]], false, budget, token);

        // Ridley Scott is credited on Alien, Gladiator and Numbers: room for three, they're loaded once and counted.
        var roomy = new CandidateBudget(3, [BaseItemKind.Movie, BaseItemKind.Series]);
        var members = Resolver(roomy, TestContext.Current.CancellationToken).Members(w.Ridley.Id, w.Ridley.Name, null);
        Assert.Equal(3, roomy.Used);
        Assert.True(members(w.World.Core.Rows.Single(r => r.Item.Name == "Alien").Id));

        // One short: refused QueryTooBroad with the exact count.
        var refusal = Assert.Throws<SmartSearchRefusedException>(() => Resolver(new CandidateBudget(2, [BaseItemKind.Movie]), TestContext.Current.CancellationToken).Members(w.Ridley.Id, w.Ridley.Name, null));
        Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, refusal.Code);
        Assert.Equal(3, refusal.CandidateCount);

        // A cancelled request stops before any query.
        w.World.Core.Queries.Clear();
        Assert.ThrowsAny<OperationCanceledException>(() => Resolver(new CandidateBudget(3, [BaseItemKind.Movie]), new CancellationToken(true)).Members(w.Ridley.Id, w.Ridley.Name, null));
        Assert.Empty(w.World.Core.Queries);
    }

    [Fact]
    public void ARole_IsCheckedLazily_PerItemAskedAbout_On1011_AndCountsOnlyItsCredits_On12()
    {
        var w = new Cast();
        var budget = new CandidateBudget(3, [BaseItemKind.Movie, BaseItemKind.Series]);
        var resolver = new PeopleRoleResolver(w.World.Core.Library.Object, w.World.Admin, [BaseItemKind.Movie, BaseItemKind.Series], [w.World.Movies.PhysicalFolderIds[0], w.World.Shows.PhysicalFolderIds[0]], false, budget, TestContext.Current.CancellationToken);
        Guid Item(string name) => w.World.Core.Rows.Single(r => r.Item.Name == name).Id;
        w.World.Core.PeopleQueries.Clear();

        var directed = resolver.Members(w.Ridley.Id, w.Ridley.Name, "Director");
        Assert.Empty(w.World.Core.PeopleQueries);
        Assert.True(directed(Item("Alien")));
        Assert.False(directed(Item("Gladiator")));
        Assert.False(directed(Item("Numbers")));
        Assert.False(directed(Item("Heat")));
        Assert.True(directed(Item("Alien")));
        if (JellyfinLine.Is12)
        {
            // 12 pushes the role: only the credits in it are loaded and counted, and core's people are never read.
            Assert.Equal(1, budget.Used);
            Assert.Empty(w.World.Core.PeopleQueries);
        }
        else
        {
            // 10.11: the person's items in any role are loaded, not counted; one GetPeople per credited item asked about,
            // once each (Heat isn't Ridley Scott's, so it's never asked about).
            Assert.Equal(0, budget.Used);
            Assert.Equal([Item("Alien"), Item("Gladiator"), Item("Numbers")], w.World.Core.PeopleQueries.Select(q => q.ItemId));
        }
    }

    [Fact]
    public async Task ThePicker_FindsAPersonPastAHundredNamesTheCallerCantSee()
    {
        var w = new Cast();
        var viewer = PlannerWorld.NewUser("viewer");
        w.World.Core.Restrict(viewer, w.World.Movies);

        // "Tom Hanks" and "Stomper Zed" in Movies first (10.11's fake answers the newest first), then 150 people each in
        // Shows only whose names hold "tom" too and sort before them ("Atom 001" < "Stom 001" < "Stomper Zed" < "Tom Hanks").
        w.World.Add(BaseItemKind.Movie, "Big", w.World.Movies, s => s.People.Add(("Tom Hanks", "Actor")));
        w.World.Add(BaseItemKind.Movie, "Stomp", w.World.Movies, s => s.People.Add(("Stomper Zed", "Actor")));
        w.World.Core.AddPerson("Tom Hanks");
        w.World.Core.AddPerson("Stomper Zed");
        for (var i = 1; i <= 150; i++)
        {
            var atom = "Atom " + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            var stom = "Stom " + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            w.World.Core.AddPerson(atom);
            w.World.Core.AddPerson(stom);
            w.World.Add(BaseItemKind.Series, "Show " + i, w.World.Shows, s => s.People.AddRange([(atom, "Actor"), (stom, "Actor")]));
        }

        await using var host = new SearchApiHost(w.World);
        host.Users.Add(viewer);
        await host.StartAsync();

        // The name starting with the text first, whatever the window: 12 asks for that range first; 10.11 reads the names
        // and orders them itself.
        // ("Stomper Zed" holds "tom" too: 12 scopes the names in SQL; on 10.11 it is past the hundred people checked.)
        Assert.Equal(JellyfinLine.Is12 ? ["Tom Hanks", "Stomper Zed"] : ["Tom Hanks"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=tom", viewer)));
        var admin = Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=tom&limit=100", host.Admin));
        Assert.Equal(["Tom Hanks", "Atom 001", "Atom 002"], admin.Take(3));
        Assert.Equal(100, admin.Count);

        // 151 names start with "stom", the 150 before "Stomper Zed" in Shows only.
        w.World.Core.PeopleNameQueries.Clear();
        var stomper = Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=stom", viewer));
        if (JellyfinLine.Is12)
        {
            // 12 asks SQL only for the names credited on what the viewer's access query lets through.
            Assert.Equal(["Stomper Zed"], stomper);
#if JELLYFIN_12
            Assert.All(w.World.Core.PeopleNameQueries, q => Assert.NotNull(q.AccessFilter));
            Assert.Equal("stom", w.World.Core.PeopleNameQueries[0].NameStartsWithOrGreater);
            Assert.Equal("ston", w.World.Core.PeopleNameQueries[0].NameLessThan);
#endif
        }
        else
        {
            // 10.11 can't scope the names: a hundred people are checked per request, and the 151st is past them (README).
            Assert.Empty(stomper);
            Assert.Equal(PersonTypeahead.MaxNames, Assert.Single(w.World.Core.PeopleNameQueries).Limit);
        }
    }

    [Fact]
    public async Task ThePicker_MatchesAccentsAndCase_InMemory()
    {
        var w = new Cast();
        w.World.Add(BaseItemKind.Movie, "Big Little Lies", w.World.Movies, s => s.People.AddRange([("Zoë Kravitz", "Actor"), ("Björk", "Actor")]));
        w.World.Core.AddPerson("Zoë Kravitz");
        w.World.Core.AddPerson("Björk");
        await using var host = await new SearchApiHost(w.World).StartAsync();

        Assert.Equal(["Zoë Kravitz"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=" + Uri.EscapeDataString("zoë"), host.Admin)));
        Assert.Equal(["Zoë Kravitz"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=" + Uri.EscapeDataString("ZOË K"), host.Admin)));
        Assert.Equal(["Björk"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=" + Uri.EscapeDataString("björk"), host.Admin)));
        Assert.Equal(["Björk"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=" + Uri.EscapeDataString("jör"), host.Admin)));
        Assert.Empty(Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=" + Uri.EscapeDataString("ëö"), host.Admin)));
    }

    [Theory]
    [InlineData("zoë", "zo")]
    [InlineData("björk", "bj")]
    [InlineData("a ë bcd", "bcd")]
    [InlineData("Tom Hanks", "Tom Hanks")]
    [InlineData(" ö-x ", "-x")]
    [InlineData("ëö", null)]
    [InlineData("-- ", null)]
    public void ThePicker_AsksCore_ForTheLongestAsciiRun(string text, string? expected)
        => Assert.Equal(expected, PersonTypeahead.SqlText(text));

    [Fact]
    public async Task ThePicker_SkipsAPersonCoreHides_TakesASearchSlot_AndStopsWhenCancelled()
    {
        var w = new Cast();
        await using var host = await new SearchApiHost(w.World, slots: 1, slotWait: TimeSpan.FromMilliseconds(100)).StartAsync();

        // Person.IsVisible (parental) hides a person from Kid.
        w.World.Core.PersonVisible = (person, user) => person.Name != "Al Pacino" || user.Id != host.Kid.Id;
        Assert.Equal(["Al Pacino"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=pacino", host.Admin)));
        Assert.Empty(Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=pacino", host.Kid)));

        // Every search slot taken: Busy (503), as a search is.
        using (await host.Engine.Slots.EnterAsync(EvaluationPurpose.Search, Guid.NewGuid(), CancellationToken.None))
        {
            var (status, problem, text) = await host.SendAsync("GET", "/SmartSearch/People?prefix=pacino", host.Admin);
            Assert.True(status == HttpStatusCode.ServiceUnavailable, text);
            Assert.Equal(SmartSearchRefusedException.BusyCode, problem.GetProperty("code").GetString());
        }

        Assert.Equal(["Al Pacino"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=pacino", host.Admin)));

        // An aborted request reads nothing.
        w.World.Core.PeopleNameQueries.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.People.FindAsync(host.Admin, "pacino", 10, [BaseItemKind.Movie], new CancellationToken(true)));
        Assert.Empty(w.World.Core.PeopleNameQueries);
    }

    [Fact]
    public async Task ThePicker_LooksUpAHundredNamesAtMost_AndStopsWhenCoreHidesPeopleWholesale()
    {
        var w = new Cast();

        // 150 credited names without a Person item sort before the one person who has one: every name counts.
        w.World.Add(BaseItemKind.Movie, "Crowd", w.World.Movies, s =>
        {
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
            s.People.AddRange(Enumerable.Range(1, 150).Select(i => ("Ghostly " + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture), "Actor")));
        });
        w.World.Add(BaseItemKind.Movie, "Haunt", w.World.Movies, s => s.People.Add(("Ghostly Real", "Actor")));
        w.World.Core.AddPerson("Ghostly Real");
        await using var host = await new SearchApiHost(w.World).StartAsync();
        var token = TestContext.Current.CancellationToken;

        w.World.Core.PersonLookups = 0;
        Assert.Empty(await host.People.FindAsync(host.Admin, "ghostly", 10, [BaseItemKind.Movie], token));
        Assert.Equal(PersonTypeahead.MaxChecks, w.World.Core.PersonLookups);
        Assert.Equal(["Ghostly Real"], (await host.People.FindAsync(host.Admin, "ghostly r", 10, [BaseItemKind.Movie], token)).Select(p => p.Name));

        // A caller Person.IsVisible hides every person from: ten lookups, no item query, an empty answer.
        for (var i = 1; i <= 150; i++)
        {
            w.World.Core.AddPerson("Ghostly " + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
        }

        w.World.Core.PersonVisible = (_, user) => user.Id != host.Kid.Id;
        w.World.Core.PersonLookups = 0;
        w.World.Core.Queries.Clear();
        Assert.Empty(await host.People.FindAsync(host.Kid, "ghostly", 10, [BaseItemKind.Movie], token));
        Assert.Equal(PersonTypeahead.MaxHiddenInARow, w.World.Core.PersonLookups);
        Assert.DoesNotContain(w.World.Core.Queries, q => q.PersonIds.Length > 0);
        Assert.Equal(10, (await host.People.FindAsync(host.Admin, "ghostly", 10, [BaseItemKind.Movie], token)).Count);
    }

    [Fact]
    public void RoleSupersets_On1011_AreBoundedTogether_PerEvaluation()
    {
        var w = new Cast();
        var budget = new CandidateBudget(4, [BaseItemKind.Movie, BaseItemKind.Series]);
        var resolver = new PeopleRoleResolver(w.World.Core.Library.Object, w.World.Admin, [BaseItemKind.Movie, BaseItemKind.Series], [w.World.Movies.PhysicalFolderIds[0], w.World.Shows.PhysicalFolderIds[0]], false, budget, TestContext.Current.CancellationToken);

        // Ridley Scott: Alien, Gladiator, Numbers (3 in any role, 1 as director); Tony Scott: Top Gun, Numbers (2; 1).
        resolver.Members(w.Ridley.Id, w.Ridley.Name, "Director");
        if (JellyfinLine.Is12)
        {
            resolver.Members(w.Tony.Id, w.Tony.Name, "Director");
            Assert.Equal(2, budget.Used);
        }
        else
        {
            // 3 + 2 any-role ids past the maximum of 4 together: refused with their count, the results' budget untouched.
            var refusal = Assert.Throws<SmartSearchRefusedException>(() => resolver.Members(w.Tony.Id, w.Tony.Name, "Director"));
            Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, refusal.Code);
            Assert.Equal(5, refusal.CandidateCount);
            Assert.Equal(0, budget.Used);
        }
    }

#if JELLYFIN_12
    [Fact]
    public async Task ThePicker_On12_ScopesNames_AsCoresPeoplePagesDo()
    {
        var w = new Cast();
        var everything = PlannerWorld.NewUser("everything");
        everything.SetPermission(PermissionKind.EnableAllFolders, true);
        var viewer = PlannerWorld.NewUser("viewer");
        w.World.Core.Restrict(viewer, w.World.Movies);
        await using var host = new SearchApiHost(w.World);
        host.Users.Add(everything);
        host.Users.Add(viewer);
        await host.StartAsync();

        // No content restrictions: no AccessFilter (core sends none); the per-person check still applies.
        w.World.Core.PeopleNameQueries.Clear();
        Assert.Equal(["Rob Morrow", "Robert De Niro"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=rob", everything)));
        Assert.All(w.World.Core.PeopleNameQueries, q => Assert.Null(q.AccessFilter));

        // Restrictions: the user's access, owned items (alternate versions) included, in the libraries they can open.
        w.World.Core.PeopleNameQueries.Clear();
        Assert.Equal(["Robert De Niro"], Names(await host.OkAsync("GET", "/SmartSearch/People?prefix=rob", viewer)));
        Assert.All(w.World.Core.PeopleNameQueries, q =>
        {
            Assert.True(q.AccessFilter!.IncludeOwnedItems);
            Assert.Same(viewer, q.AccessFilter.User);
            Assert.Equal(w.World.Movies.PhysicalFolderIds, q.AccessFilter.TopParentIds);
        });
    }
#endif

    // Films, a show and its episode, credited; the people's Person items; a Private film and an R-rated one.
    private sealed class Cast
    {
        public Cast()
        {
            var core = World.Core;
            Ridley = core.AddPerson("Ridley Scott");
            Tony = core.AddPerson("Tony Scott");
            Pacino = core.AddPerson("Al Pacino");
            DeNiro = core.AddPerson("Robert De Niro");
            Guest = core.AddPerson("Guest Person");
            foreach (var name in new[] { "Rob Morrow", "Secret Person", "Grim Actor", "Michael Mann", "Al Ross" })
            {
                core.AddPerson(name);
            }

            Movie("Alien", world => world.Movies, ("Ridley Scott", "Director"), ("Sigourney Weaver", "Actor"));
            Movie("Gladiator", world => world.Movies, ("Ridley Scott", "Producer"), ("Russell Crowe", "Actor"));
            Movie("Top Gun", world => world.Movies, ("Tony Scott", "Director"));
            Movie("Heat", world => world.Movies, ("Al Pacino", "Actor"), ("Robert De Niro", "Actor"), ("Michael Mann", "Director"), ("Michael Mann", "Writer"));
            Movie("The Godfather Part II", world => world.Movies, ("Al Pacino", "Actor"), ("Robert De Niro", "Actor"));
            Movie("Serpico", world => world.Movies, ("Al Pacino", "Actor"), ("Robert De Niro", "Producer"), ("Al Ross", "Actor"));
            Movie("Secret", world => world.Private, ("Secret Person", "Actor"));
            World.Add(BaseItemKind.Movie, "Grim", World.Movies, s =>
            {
                s.OfficialRating = "R";
                s.InheritedRating = 17;
                s.People.Add(("Grim Actor", "Actor"));
            });
            World.Add(BaseItemKind.Series, "Numbers", World.Shows, s =>
            {
                s.OfficialRating = "PG";
                s.InheritedRating = 10;
                s.People.AddRange([("Ridley Scott", "Producer"), ("Tony Scott", "Producer"), ("Rob Morrow", "Actor")]);
            });
            World.Add(BaseItemKind.Episode, "Pilot", World.Shows, s =>
            {
                s.OfficialRating = "PG";
                s.InheritedRating = 10;
                s.People.Add(("Guest Person", "GuestStar"));
            });
        }

        public PlannerWorld World { get; } = new();

        public Person Ridley { get; }

        public Person Tony { get; }

        public Person Pacino { get; }

        public Person DeNiro { get; }

        public Person Guest { get; }

        private void Movie(string name, Func<PlannerWorld, CollectionFolder> library, params (string Name, string Type)[] credits)
            => World.Add(BaseItemKind.Movie, name, library(World), s =>
            {
                s.OfficialRating = "PG";
                s.InheritedRating = 10;
                s.People.AddRange(credits);
            });
    }
}
