using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The limits on the planner: a cancellation token through <c>Plan()</c>, its database reads and
/// the library membership, and the item sets of <c>Library</c> rules counted against the evaluation's
/// <see cref="CandidateBudget"/> (<c>MaxCandidates</c>).
/// </summary>
public sealed class PlannerLimitsTests
{
    [Fact]
    public void Plan_WithACancelledToken_ReadsNothing()
    {
        var world = World(out _);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var query = PlanRunner.Read(QueryPlannerTests.Q($$"""{"Match":"Any","Children":[{"Field":"Library","Op":"AnyOf","Values":["{{world.Movies.Id}}"]},{"Field":"Year","Op":"Eq","Value":1999}]}"""));

        Assert.ThrowsAny<OperationCanceledException>(() => world.Planner.Plan(new PlanRequest(query, world.Admin), world.Environment, null, cancelled.Token));
        Assert.Empty(world.Core.Queries);
    }

    [Theory]
    [InlineData("""{"Field":"Genres","Op":"AnyOf","Values":["Drama"]}""", "Movie")]
    [InlineData("""{"Field":"SeriesName","Op":"Eq","Value":"Alpha"}""", "Episode")]
    [InlineData("""{"Field":"Played","Op":"Is","Value":true}""", "Movie")]
    public void Plan_PassesItsTokenToEveryFactsRead(string rule, string kind)
    {
        var world = World(out _);
        using var source = new CancellationTokenSource();
        var facts = new CancellingFacts(world.Core, source);
        var planner = new QueryPlanner(PlanRunner.Registry, world.Core.Access(), world.Core.Library.Object, facts);
        var query = PlanRunner.Read(QueryPlannerTests.Q(rule, "\"" + kind + "\""));

        // The facts cancel the token on their first read, then pass it on: the read must see it. (Series names are read
        // from the database only without a user; a user's are read in their scope, below.)
        var user = rule.Contains("SeriesName", StringComparison.Ordinal) ? null : world.Admin;
        Assert.ThrowsAny<OperationCanceledException>(() => planner.Plan(new PlanRequest(query, user), world.Environment, null, source.Token));
        Assert.Equal(1, facts.Reads);

        // Without a token the same plan reads its facts and finishes.
        Assert.NotNull(planner.Plan(new PlanRequest(query, user), world.Environment));
    }

    [Theory]
    [InlineData("""{"Field":"SeriesName","Op":"Eq","Value":"Alpha"}""", "Episode")]
    [InlineData("""{"Field":"Studios","Op":"AnyOf","Values":["A24"]}""", "Movie")]
    public void Plan_PassesItsTokenToTheLookupsInTheCallersScope(string rule, string kind)
    {
        var world = World(out _);
        world.Core.AddStudio("A24");
        using var source = new CancellationTokenSource();
        var lookups = new CancellingLookups(new LoadedLookups(world.Core.Library.Object), source);
        var planner = new QueryPlanner(PlanRunner.Registry, world.Core.Access(), world.Core.Library.Object, world.Core, lookups);
        var query = PlanRunner.Read(QueryPlannerTests.Q(rule, "\"" + kind + "\""));

        Assert.ThrowsAny<OperationCanceledException>(() => planner.Plan(new PlanRequest(query, world.Kid), world.Environment, null, source.Token));
        Assert.Equal(1, lookups.Reads);
        Assert.NotNull(planner.Plan(new PlanRequest(query, world.Kid), world.Environment));
    }

    [Fact]
    public void Membership_WithACancelledToken_RunsNoQuery()
    {
        var world = World(out _);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var membership = new LibraryMembership(world.Core.Access(), world.Core.Library.Object, world.Admin, [BaseItemKind.Movie], false, null, cancelled.Token);

        Assert.ThrowsAny<OperationCanceledException>(() => membership.Members([world.Movies.Id]));
        Assert.Empty(world.Core.Queries);
    }

    [Fact]
    public void Membership_CountsAgainstTheBudget_AndRefusesPastIt()
    {
        var world = World(out var movies);
        var query = PlanRunner.Read(QueryPlannerTests.Q($$"""{"Match":"Any","Children":[{"Field":"Library","Op":"AnyOf","Values":["{{world.Movies.Id}}"]},{"Field":"Year","Op":"Eq","Value":1999}]}"""));

        // Room for every member: the set is loaded once and counted.
        var roomy = new CandidateBudget(movies, [BaseItemKind.Movie]);
        var plan = world.Planner.Plan(new PlanRequest(query, world.Admin), world.Environment, roomy, CancellationToken.None);
        Assert.Equal(QueryPlanPath.Residual, plan.Path);
        Assert.Equal(movies, roomy.Used);
        Assert.Equal(movies + 1, world.Core.Queries.Single().Limit);

        // One short: refused with the exact count (a count query), never a set cut at the cap.
        var tight = new CandidateBudget(movies - 1, [BaseItemKind.Movie]);
        var refusal = Assert.Throws<SmartSearchRefusedException>(() => world.Planner.Plan(new PlanRequest(query, world.Admin), world.Environment, tight, CancellationToken.None));
        Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, refusal.Code);
        Assert.Equal(movies, refusal.CandidateCount);
        Assert.Equal([BaseItemKind.Movie], refusal.Kinds);
    }

    [Fact]
    public void Budget_IsShared_AndRefusesWithTheHintForEpisodes()
    {
        var budget = new CandidateBudget(10, [BaseItemKind.Episode]);
        budget.Take(6);
        Assert.Equal(4, budget.Remaining);
        Assert.Equal(5, budget.NextLimit);
        var refusal = Assert.Throws<SmartSearchRefusedException>(() => budget.Take(5, () => 40));
        Assert.Equal(46, refusal.CandidateCount);
        Assert.Contains("series rule", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(Kit.Api.PluginErrorKind.Invalid, refusal.Kind);
    }

    private static PlannerWorld World(out int movies)
    {
        var world = new PlannerWorld();
        for (var i = 0; i < 12; i++)
        {
            world.Add(BaseItemKind.Movie, "Movie " + i, world.Movies, s => s.Year = 1990 + i);
        }

        var series = world.Add(BaseItemKind.Series, "Alpha", world.Shows);
        world.Add(BaseItemKind.Episode, "Pilot", world.Shows, s => s.Series = series);
        movies = 12;
        return world;
    }

    // Cancels the token on the first read, then reads with it.
    private sealed class CancellingLookups(IScopedLookups inner, CancellationTokenSource source) : IScopedLookups
    {
        public int Reads { get; private set; }

        public IReadOnlyList<EpisodeSeries>? EpisodeSeries(User user, IReadOnlyList<Guid> folders, bool includeMissing, CancellationToken cancellationToken)
            => inner.EpisodeSeries(user, folders, includeMissing, Cancel(cancellationToken));

        public bool HasStudio(User user, IReadOnlyList<Guid> folders, IReadOnlyCollection<BaseItemKind> kinds, bool includeMissing, string name, Guid studioId, CancellationToken cancellationToken)
            => inner.HasStudio(user, folders, kinds, includeMissing, name, studioId, Cancel(cancellationToken));

        private CancellationToken Cancel(CancellationToken cancellationToken)
        {
            Reads++;
            source.Cancel();
            return cancellationToken;
        }
    }

    private sealed class CancellingFacts(FakeCore core, CancellationTokenSource source) : IPushdownFacts
    {
        public int Reads { get; private set; }

        public CleanFormSnapshot? CleanForms => core.CleanForms;

        public IReadOnlyList<ItemValueRow>? PipeValues(CancellationToken cancellationToken) => core.PipeValues(Cancel(cancellationToken));

        public bool UserDataAgrees(Guid userId, UserDataFlag flag, CancellationToken cancellationToken) => core.UserDataAgrees(userId, flag, Cancel(cancellationToken));

        public IReadOnlyList<EpisodeSeries>? EpisodeSeries(IReadOnlyCollection<Guid> topParentIds, CancellationToken cancellationToken) => core.EpisodeSeries(topParentIds, Cancel(cancellationToken));

        private CancellationToken Cancel(CancellationToken token)
        {
            Reads++;
            if (token.CanBeCanceled)
            {
                source.Cancel();
            }

            return token;
        }
    }
}
