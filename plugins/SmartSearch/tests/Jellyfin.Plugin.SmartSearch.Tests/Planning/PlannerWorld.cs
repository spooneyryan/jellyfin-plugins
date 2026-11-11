using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The planner tests' server: a <see cref="FakeCore"/> with a Movies library over two folders, Shows, Home videos, a
/// boxsets library, a Private library, a Hidden library that shares Movies' first folder and a Hidden Shows library over
/// Shows' folder (one folder can be in two libraries); an administrator who sees everything, and a restricted user (Kid)
/// who can't open Hidden, Hidden Shows or Private, with a
/// parental limit of 13, unrated movies and "Other" blocked, and the tag "Gore" blocked.
/// </summary>
internal sealed class PlannerWorld
{
    public static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    public static readonly TimeZoneInfo West = TimeZoneInfo.CreateCustomTimeZone("Test-West", TimeSpan.FromHours(-5), "UTC-05", "UTC-05");

    public static readonly TimeZoneInfo East = TimeZoneInfo.CreateCustomTimeZone("Test-East", TimeSpan.FromHours(10), "UTC+10", "UTC+10");

    public static readonly Dictionary<string, int> Scores = new(StringComparer.Ordinal) { ["G"] = 0, ["PG"] = 10, ["PG-13"] = 13, ["R"] = 17, ["NC-17"] = 18 };

    public PlannerWorld(TimeZoneInfo? zone = null)
    {
        Zone = zone ?? TimeZoneInfo.Utc;
        FolderA = Guid.NewGuid();
        FolderB = Guid.NewGuid();
        Movies = Core.AddLibrary("Movies", CollectionType.movies, FolderA, FolderB);
        Shows = Core.AddLibrary("Shows", CollectionType.tvshows);
        Home = Core.AddLibrary("Home", CollectionType.homevideos);
        Collections = Core.AddLibrary("Collections", CollectionType.boxsets);
        Private = Core.AddLibrary("Private", CollectionType.movies);
        Hidden = Core.AddLibrary("Hidden", CollectionType.movies, FolderA);
        HiddenShows = Core.AddLibrary("Hidden Shows", CollectionType.tvshows, Shows.PhysicalFolderIds[0]);
        Admin = NewUser("admin");
        Kid = NewUser("kid");
        Kid.MaxParentalRatingScore = 13;
        Kid.SetPreference(PreferenceKind.BlockUnratedItems, [nameof(UnratedItem.Movie), nameof(UnratedItem.Other)]);
        Kid.SetPreference(PreferenceKind.BlockedTags, ["Gore"]);
        Core.Restrict(Kid, Movies, Shows, Home, Collections);
        Planner = new QueryPlanner(PlanRunner.Registry, Core.Access(), Core.Library.Object, Core);
        Environment = new EvaluationEnvironment
        {
            NowUtc = Now,
            TimeZone = Zone,
            RatingScore = rating => Scores.TryGetValue(rating, out var score) ? new ParentalRatingScore(score, null) : null
        };
    }

    public FakeCore Core { get; } = new();

    public TimeZoneInfo Zone { get; }

    public Guid FolderA { get; }

    public Guid FolderB { get; }

    public CollectionFolder Movies { get; }

    public CollectionFolder Shows { get; }

    public CollectionFolder Home { get; }

    public CollectionFolder Collections { get; }

    public CollectionFolder Private { get; }

    public CollectionFolder Hidden { get; }

    /// <summary>Gets a library over Shows' folder that Kid can't open: Shows' episodes have its id among their ancestors.</summary>
    public CollectionFolder HiddenShows { get; }

    public User Admin { get; }

    public User Kid { get; }

    public QueryPlanner Planner { get; }

    public EvaluationEnvironment Environment { get; }

    /// <summary>Gets the plans <see cref="RunAndCheck"/> ran.</summary>
    public List<QueryPlan> Plans { get; } = [];

    public static User NewUser(string name) => new(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider") { Id = Guid.NewGuid() };

    public QueryPlan Plan(string json, User? user = null, PlanPurpose purpose = PlanPurpose.Search)
        => Planner.Plan(new PlanRequest(PlanRunner.Read(json), user, purpose), Environment);

    public FakeRow Add(BaseItemKind kind, string name, CollectionFolder library, Action<RowSpec>? set = null)
    {
        var spec = new RowSpec(kind, name) { Library = library, Zone = Zone };
        set?.Invoke(spec);
        return Core.Add(spec);
    }

    /// <summary>Plans and runs a query, and checks it against the truth: no false negative, no false positive.</summary>
    public HashSet<Guid> RunAndCheck(string json, User? user = null, PlanPurpose purpose = PlanPurpose.Search)
    {
        var query = PlanRunner.Read(json);
        var plan = Planner.Plan(new PlanRequest(query, user, purpose), Environment);
        Plans.Add(plan);
        var results = PlanRunner.Run(plan, Core, user);
        var truth = PlanRunner.Truth(query, Core, user, Environment);
        var (missing, extra) = Compare(plan, truth, results);
        Xunit.Assert.True(missing.Count == 0 && extra.Count == 0, "missing (false negatives): [" + string.Join(", ", missing) + "]; extra: [" + string.Join(", ", extra) + "]; steps: " + string.Join(" | ", plan.Steps));
        return results;
    }

    /// <summary>
    /// The truth's rows the plan didn't return and the plan's rows the truth hasn't, by name; on the Fast path by
    /// presentation key (core returns one row per key there).
    /// </summary>
    public (List<string> Missing, List<string> Extra) Compare(QueryPlan plan, IReadOnlySet<Guid> truth, IReadOnlySet<Guid> results) => PlanRunner.Compare(plan, truth, results, Core);
}
