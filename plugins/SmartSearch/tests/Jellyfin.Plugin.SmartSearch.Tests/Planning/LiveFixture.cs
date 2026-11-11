using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The planner's live check, recorded (<c>Planning/Live/planner-live.json</c>, written by a probe against a 10.11.5 sandbox):
/// a sandbox's libraries, users' access, rating scores, studios and item rows read from a copy of its database, the
/// generated queries (and targeted ones), and for each query and user what the real 10.11.5
/// library manager did with the planner's plan (the path, the steps, the candidate rows, the results and the count, the
/// truth and the matches <c>IsVisible</c> refuses) and what the real <c>/Items</c> showed and counted for the scope.
/// <see cref="Build"/> loads the rows, and the seasons and folders they descend from, into a <see cref="FakeCore"/> with
/// the same ids, users and libraries.
/// </summary>
internal sealed class LiveFixture
{
    public const string FileName = "planner-live.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public required string Recorded { get; init; }

    public required string Server { get; init; }

    public required string SmartSearch { get; init; }

    public int Seed { get; init; }

    /// <summary>Gets how many of the queries, at the end, are the targeted ones (not generated).</summary>
    public int Targets { get; init; }

    /// <summary>Gets the Fast path's page size in the live run.</summary>
    public int PageSize { get; init; } = 200;

    /// <summary>Gets the seasons, folders and root the rows descend from (items that aren't rows of the planner's kinds).</summary>
    public List<LiveOther> Others { get; init; } = [];

    public required string TimeZone { get; init; }

    public DateTime NowUtc { get; init; }

    public string? Snapshot { get; init; }

    public List<LiveLibrary> Libraries { get; init; } = [];

    public List<LiveUser> Users { get; init; } = [];

    public List<LiveRating> Ratings { get; init; } = [];

    public List<LiveStudio> Studios { get; init; } = [];

    public Dictionary<string, Guid> StudioIds { get; init; } = [];

    public List<LiveValue> Values { get; init; } = [];

    public List<LiveRow> Rows { get; init; } = [];

    public List<string> Queries { get; init; } = [];

    public List<Guid> CheckUsers { get; init; } = [];

    public List<List<string>> Steps { get; init; } = [];

    public List<LiveRun> Runs { get; init; } = [];

    public JsonElement Live { get; init; }

    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Planning", "Live", FileName);

    public static LiveFixture Load(string? file = null) => JsonSerializer.Deserialize<LiveFixture>(File.ReadAllText(file ?? Path), Json)!;

    /// <summary>A fake core holding the recorded rows, its planner and environment, and the recorded users by name.</summary>
    public (FakeCore Core, QueryPlanner Planner, EvaluationEnvironment Environment, Dictionary<string, User> Users) Build()
    {
        var core = new FakeCore { StudioIds = name => StudioIds.TryGetValue(name, out var id) ? id : null };
        var libraries = Libraries.ToDictionary(l => l.Id, l => core.AddLibrary(l.Id, l.Name, l.CollectionType is { } t ? Enum.Parse<CollectionType>(t, true) : null, [.. l.Folders]));
        foreach (var other in Others)
        {
            core.AddOther(other.Id, other.Kind);
        }

        foreach (var studio in Studios)
        {
            core.AddStudio(studio.Id, studio.Name, studio.CleanName);
        }

        foreach (var value in Values)
        {
            core.SeedValue((ItemValueType)value.Type, value.Value, value.Clean);
        }

        var users = new Dictionary<string, User>(StringComparer.Ordinal);
        foreach (var u in Users)
        {
            var user = PlannerWorld.NewUser(u.Name);
            user.Id = u.Id;
            user.MaxParentalRatingScore = u.MaxParentalRatingScore;
            user.MaxParentalRatingSubScore = u.MaxParentalRatingSubScore;
            user.SetPreference(PreferenceKind.BlockedTags, [.. u.BlockedTags]);
            user.SetPreference(PreferenceKind.AllowedTags, [.. u.AllowedTags]);
            user.SetPreference(PreferenceKind.BlockUnratedItems, [.. u.BlockUnratedItems]);
            core.Restrict(user, [.. u.Visible.Select(id => libraries[id])]);
            users[u.Name] = user;
        }

        foreach (var r in Rows)
        {
            core.Add(Spec(r));
        }

        var zone = TimeZoneInfo.FromSerializedString(TimeZone);
        var scores = Ratings.Where(r => r.Score is not null).ToDictionary(r => r.Rating, r => new ParentalRatingScore(r.Score!.Value, r.SubScore), StringComparer.Ordinal);
        var environment = new EvaluationEnvironment
        {
            NowUtc = NowUtc,
            TimeZone = zone,
            RatingScore = rating => scores.TryGetValue(rating, out var score) ? score : null
        };
        return (core, new QueryPlanner(PlanRunner.Registry, core.Access(), core.Library.Object, core), environment, users);
    }

    /// <summary>The row indexes of a recorded set (hex digits, digit <c>j</c> holding rows <c>4j</c> to <c>4j + 3</c>).</summary>
    public static IEnumerable<int> Indexes(string bits)
    {
        for (var j = 0; j < bits.Length; j++)
        {
            var digit = Convert.ToInt32(bits[j].ToString(), 16);
            for (var b = 0; b < 4; b++)
            {
                if ((digit & (1 << b)) != 0)
                {
                    yield return (4 * j) + b;
                }
            }
        }
    }

    private static IReadOnlyList<string> Split(string? column) => string.IsNullOrEmpty(column) ? [] : column.Split('|');

    private RowSpec Spec(LiveRow r)
    {
        var spec = new RowSpec(Enum.Parse<BaseItemKind>(r.Kind), r.Name)
        {
            Id = r.Id,
            CleanName = r.CleanName,
            OriginalTitle = r.OriginalTitle,
            Overview = r.Overview,
            Year = r.Year,
            PremiereStoredUtc = r.Premiere,
            DateCreated = r.Created,
            Genres = Split(r.Genres),
            Tags = Split(r.Tags),
            Studios = Split(r.Studios),
            OfficialRating = r.Rating,
            CommunityRating = (float?)r.Community,
            CriticRating = (float?)r.Critic,
            RunTimeTicks = r.Runtime,
            Width = r.Width ?? 0,
            Container = r.Container,
            IsLocked = r.Locked,
            TopParentId = r.Top,
            SeriesId = r.SeriesId,
            SeriesName = r.SeriesName,
            OwnerId = r.Owner,
            IsVirtual = r.Virtual,
            InheritedRating = r.Inherited,
            InheritedSubRating = r.InheritedSub,
            PresentationUniqueKey = r.Puk,
            UnratedType = r.UnratedType,
            Ancestors = r.Ancestors,
            StoredValues = [.. r.Values.Select(i => ((ItemValueType)Values[i].Type, Values[i].Value))],
            Configure = item =>
            {
                item.Height = r.Height ?? 0;
                item.Size = r.Size;
                item.TotalBitrate = r.Bitrate;
                if (item is Video video)
                {
                    video.VideoType = r.VideoType is { } type ? Enum.Parse<VideoType>(type) : default;
                    video.Video3DFormat = r.Video3D is { } format ? Enum.Parse<Video3DFormat>(format) : null;
                }

                if (item is Series series && r.Status is { } status)
                {
                    series.Status = Enum.Parse<SeriesStatus>(status);
                }
            }
        };
        spec.Providers.AddRange(r.Providers.Select(p => (p[0], p[1])));
        spec.UserData.AddRange(r.UserData.Select(u => new FakeUserData(u.User, u.Key, u.Played, u.Favorite, u.Position, u.Count, u.Last)));
        return spec;
    }
}

internal sealed record LiveLibrary(Guid Id, string Name, string? CollectionType, List<Guid> Folders);

internal sealed record LiveOther(Guid Id, string Kind);

internal sealed record LiveUser(Guid Id, string Name, bool IsAdmin, int? MaxParentalRatingScore, int? MaxParentalRatingSubScore, List<string> BlockedTags, List<string> AllowedTags, List<string> BlockUnratedItems, List<Guid> Visible);

internal sealed record LiveRating(string Rating, int? Score, int? SubScore);

internal sealed record LiveStudio(Guid Id, string Name, string? CleanName);

internal sealed record LiveValue(int Type, string Value, string Clean);

internal sealed record LiveUserData(Guid User, string Key, bool Played, bool Favorite, long Position, int Count, DateTime? Last);

internal sealed record LiveRow(
    Guid Id,
    string Kind,
    string Name,
    string? CleanName,
    string? OriginalTitle,
    string? Overview,
    int? Year,
    DateTime? Premiere,
    DateTime? Created,
    string? Genres,
    string? Tags,
    string? Studios,
    string? Rating,
    double? Community,
    double? Critic,
    long? Runtime,
    int? Width,
    int? Height,
    long? Size,
    int? Bitrate,
    string? Container,
    string? VideoType,
    string? Video3D,
    string? Status,
    bool Locked,
    bool Virtual,
    Guid? Top,
    Guid? SeriesId,
    string? SeriesName,
    Guid? Owner,
    int? Inherited,
    int? InheritedSub,
    string? UnratedType,
    string Puk,
    List<List<string>> Providers,
    List<Guid> Ancestors,
    List<int> Values,
    List<LiveUserData> UserData);

/// <summary>
/// One recorded run: query, user (index into <c>CheckUsers</c>), purpose, and what core did; each set of rows as hex digits,
/// digit <c>j</c> holding rows <c>4j</c> to <c>4j + 3</c> (<see cref="LiveFixture.Indexes"/>): the candidate rows (every
/// Fast page's), the results, the count reported, the truth (what <c>/Items</c> would show), the matches
/// <c>IsVisible</c> refused, the scope's rows SQL's access lets through (<c>ItemsSql</c>), and what the real
/// <c>/Items</c> showed (<c>Shown</c>) and counted (<c>ItemsCount</c>).
/// </summary>
internal sealed record LiveRun(int Q, int U, string P, string Path, int S, bool D, string Sql, string Results, string Truth, string Invisible, int Items, int Missing, int Extra, int Wider, string? Error, int Count = 0, int Pages = 0, int ShortPages = 0, string ItemsSql = "", string Shown = "", int ItemsCount = 0);
