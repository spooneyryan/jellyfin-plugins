using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Query;

/// <summary>
/// The field registry is the field table below, and every field, operator and option it declares is accepted by
/// the validator and compiled by the field's evaluator.
/// </summary>
public sealed class FieldRegistryTests
{
    // Every field, in the registry's order: id | group | kinds | operators | type and values | options | cost and flags.
    // Value sources follow /SmartSearch/Values/{fieldId}.
    // Operator sets: Text, Number, Date, List, Choice, Bool.
    private static readonly string[] FieldTable =
    [
        "Title | Text | M S E V H B | Text | Text | IncludeOriginalTitle | Row",
        "OriginalTitle | Text | M S E V | Text | Text | - | Row",
        "Overview | Text | M S E V H B | Contains NotContains IsEmpty IsNotEmpty | Text | - | Row",
        "SeriesName | Text | E | Text | Text | - | Row",
        "Series | Scope | E | Choice | Choice of Reference from /SmartSearch/Series | - | Row",
        "Path | Text | M S E V H B | Contains NotContains StartsWith Matches | Text | - | Row AdminOnly",
        "Year | Dates | M S E V H | Number AnyOf | Number of Integer 1800–2100 whole span 200 | - | Row",
        "PremiereDate | Dates | M S E V H | Date | Date | - | Row",
        "DateAdded | Dates | M S E V H B | Date | Date | - | Row",
        "Library | Scope | M S E V H B | Choice | Choice of Guid from /SmartSearch/Libraries | - | Set",
        "ItemType | Scope | M S E V H B | Choice | Choice of ItemKind [Movie Series Episode MusicVideo Video BoxSet] | - | Row",
        "Genres | Metadata | M S E V H B | List | List of String from /SmartSearch/Values/Genres | - | Row",
        "Tags | Metadata | M S E V H B | List | List of String from /SmartSearch/Values/Tags | - | Row",
        "Studios | Metadata | M S E V | List | List of String from /SmartSearch/Values/Studios | - | Row",
        "OfficialRating | Metadata | M S E V H B | Choice Le Ge | Choice of String from /SmartSearch/Values/OfficialRating | - | Row",
        "SeriesStatus | Metadata | S | Choice | Choice of String [Continuing Ended Unreleased] | - | Row",
        "CommunityRating | Ratings | M S E V H B | Number | Number 0–10 | - | Row",
        "CriticRating | Ratings | M S | Number | Number 0–100 | - | Row",
        "Runtime | Media | M E V H | Number | Number 0– whole | - | Row",
        "Resolution | Media | M E V H | Choice | Choice of String [SD 720p 1080p 1440p 2160p Unknown] | - | Row",
        "Container | Media | M E V H | Choice | Choice of String from /SmartSearch/Values/Container | - | Row",
        "VideoType | Media | M E V H | Choice | Choice of String [VideoFile Iso Dvd BluRay] | - | Row",
        "Is3D | Media | M E V H | Bool | Bool | - | Row",
        "FileSize | Media | M E V H | Number | Number 0– | - | Row",
        "Bitrate | Media | M E V H | Number | Number 0– | - | Row",
        "Played | Watch state | M E V H | Bool | Bool | - | User PerUser",
        "Favorite | Watch state | M S E V H B | Bool | Bool | - | User PerUser",
        "InProgress | Watch state | M E V H | Bool | Bool | - | User PerUser",
        "PlayCount | Watch state | M E V H | Number | Number 0– whole | - | User PerUser",
        "LastPlayed | Watch state | M E V H | Date | Date | - | User PerUser",
        "HasProviderId | Identity | M S E V | Bool | Bool | Provider required [Tmdb Imdb Tvdb] | Row ProviderIds",
        "ProviderId | Identity | M S E V | Eq | Text | Provider required [Tmdb Imdb Tvdb] | Row ProviderIds",
        "Locked | Identity | M S E V H B | Bool | Bool | - | Row",
    ];

    private static readonly FieldRegistry Registry = new();

    private static readonly QueryValidator Validator = new(Registry);

    [Fact]
    public void TheRegistry_IsTheFieldTable()
    {
        Assert.Equal(FieldTable, Registry.Fields.Select(Describe));
    }

    [Fact]
    public void TheLaterFields_AreNotInTheRegistryYet()
    {
        Assert.All(
            ["People", "InCollection", "MemberRating", "MyRating", "VideoRange", "VideoCodec", "AudioCodec", "AudioChannels", "AudioLanguage", "SubtitleLanguage", "HasSubtitles"],
            id => Assert.Null(Registry.Find(id)));
    }

    [Fact]
    public void TheSortKeys_AreTheFifteenKeys_WithoutMemberRating()
    {
        Assert.Equal(
            ["SortName", "ProductionYear", "PremiereDate", "DateCreated", "CommunityRating", "CriticRating", "Runtime", "OfficialRating", "VideoBitRate", "DatePlayed", "PlayCount", "Random", "FileSize", "Resolution", "SeriesName"],
            Registry.SortKeys);
        Assert.Equal("DatePlayed", Registry.FindSortKey("dateplayed"));
    }

    [Fact]
    public void EveryField_IsAvailable_AndOnlyPathIsAdminOnly()
    {
        Assert.All(Registry.Fields, f => Assert.True(f.Available, f.Id));
        Assert.Equal(["Path"], Registry.Fields.Where(f => f.AdminOnly).Select(f => f.Id));
        Assert.Equal(["Played", "Favorite", "InProgress", "PlayCount", "LastPlayed"], Registry.Fields.Where(f => f.PerUser).Select(f => f.Id));
    }

    public static TheoryData<string, string> EveryFieldAndOperator()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in Registry.Fields)
        {
            foreach (var op in field.Operators)
            {
                data.Add(field.Id, op.ToString());
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFieldAndOperator))]
    public void EveryFieldAndOperator_Validates_Compiles_AndEvaluates(string fieldId, string op)
    {
        var field = Registry.Find(fieldId)!;
        var json = SampleQuery(field, Enum.Parse<SmartSearchQueryOperator>(op));
        var read = QueryJson.Read(json);
        Assert.True(read.Definition is not null, json);
        var validation = Validator.Validate(read.Definition!, TestCatalog.AdminWithRegex); // Matches is dormant, still tested
        Assert.True(validation.Valid, json + " → " + string.Join(", ", validation.Errors.Select(e => e.Path + ":" + e.Code)));

        var rule = read.Definition!.Where.Children![0];
        var environment = new EvaluationEnvironment
        {
            NowUtc = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc),
            TimeZone = TimeZoneInfo.Utc,
            RatingScore = rating => new MediaBrowser.Model.Entities.ParentalRatingScore(rating.Length, null),
            LibraryMembers = _ => new HashSet<Guid>()
        };
        var test = Registry.EvaluatorOf(field).Compile(rule, environment);
        foreach (var item in SampleItems())
        {
            _ = test(new EvaluationRow(item));
        }
    }

    [Fact]
    public void TheRegistryJson_ListsEachFieldOnce_WithItsOperatorsAsNames()
    {
        var fields = new Jellyfin.Plugin.SmartSearch.Services.SmartSearchFields(Registry).GetCatalog(isAdmin: true);
        var json = JsonNode.Parse(JsonSerializer.Serialize(fields))!;
        Assert.Equal(FieldRegistry.Version, (int)json["RegistryVersion"]!);
        var title = json["Fields"]!.AsArray().Single(f => (string)f!["Id"]! == "Title")!;
        Assert.Equal(QueryOperators.Text.Where(o => o != SmartSearchQueryOperator.Matches).Select(o => o.ToString()), title["Operators"]!.AsArray().Select(o => (string)o!));
        Assert.DoesNotContain(json["Fields"]!.AsArray(), f => f!["Operators"]!.AsArray().Any(o => (string)o! == "Matches")); // not offered yet
        Assert.Equal("IncludeOriginalTitle", (string)title["Options"]![0]!["Name"]!);
        Assert.Equal(Registry.Fields.Count, json["Fields"]!.AsArray().Count);
    }

    // One query per field and operator with valid sample values: the field's kinds as the scope, and its required options.
    private static string SampleQuery(FieldDescriptor field, SmartSearchQueryOperator op)
    {
        var rule = new JsonObject { ["Field"] = field.Id, ["Op"] = op.ToString() };
        switch (QueryOperators.ShapeOf(op))
        {
            case OperatorShape.Value:
                rule["Value"] = SampleScalar(field, op, 0);
                break;
            case OperatorShape.Range:
                rule["From"] = SampleScalar(field, op, 0);
                rule["To"] = SampleScalar(field, op, 1);
                break;
            case OperatorShape.Relative:
                rule["Amount"] = 30;
                rule["Unit"] = "Days";
                break;
            case OperatorShape.Values:
                rule["Values"] = new JsonArray(SampleElement(field));
                break;
        }

        foreach (var option in field.Options.Where(o => o.Required))
        {
            rule[option.Option.ToString()] = option.Choices![0];
        }

        var kinds = new JsonArray([.. field.Kinds.Select(k => (JsonNode)k.ToString())]);
        return new JsonObject
        {
            ["Scope"] = new JsonObject { ["ItemKinds"] = kinds },
            ["Where"] = new JsonObject { ["Children"] = new JsonArray(rule) }
        }.ToJsonString();
    }

    private static JsonNode SampleScalar(FieldDescriptor field, SmartSearchQueryOperator op, int index) => field.Type switch
    {
        FieldValueType.Number => field.Min >= QueryLimits.MinYear ? 2000 + index : (field.Min ?? 0) + index + 1,
        FieldValueType.Date => index == 0 ? "2020-01-01" : "2020-12-31",
        FieldValueType.Bool => true,
        _ when op == SmartSearchQueryOperator.Matches => "^a.*",
        _ when field.Id == "OfficialRating" => "PG-13",
        _ => "a"
    };

    private static JsonNode SampleElement(FieldDescriptor field) => field.ElementKind switch
    {
        FieldElementKind.Integer => 2000,
        FieldElementKind.Guid => Guid.NewGuid().ToString("N"),
        FieldElementKind.Reference => new JsonObject { ["Id"] = Guid.NewGuid().ToString("N"), ["Name"] = "A Show" },
        FieldElementKind.ItemKind => field.Kinds[0].ToString(),
        _ => field.Choices?[0] ?? "a"
    };

    private static IEnumerable<BaseItem> SampleItems()
    {
        yield return new Movie();
        yield return new Movie { Name = "Amélie", OriginalTitle = "Le Fabuleux Destin d'Amélie Poulain", ProductionYear = 2001, Genres = ["Comedy"], Width = 1920, RunTimeTicks = TimeSpan.FromMinutes(122).Ticks };
        yield return new Episode { Name = "Pilot", SeriesName = "A Show", SeriesId = Guid.NewGuid() };
        yield return new Series { Name = "A Show", Status = MediaBrowser.Model.Entities.SeriesStatus.Ended };
    }

    private static string Describe(FieldDescriptor f)
    {
        var flags = string.Join(' ', new[] { f.Cost.ToString(), f.PerUser ? "PerUser" : null, f.AdminOnly ? "AdminOnly" : null, f.NeedsProviderIds ? "ProviderIds" : null }.OfType<string>());
        var options = f.Options.Count == 0 ? "-" : string.Join(", ", f.Options.Select(o => o.Option + (o.Required ? " required" : string.Empty) + (o.Choices is { } c ? " [" + string.Join(' ', c) + "]" : string.Empty)));
        return f.Id + " | " + f.Group + " | " + Kinds(f.Kinds) + " | " + Operators(f) + " | " + Values(f) + " | " + options + " | " + flags;
    }

    private static string Kinds(IReadOnlyList<BaseItemKind> kinds) => string.Join(' ', kinds.Select(k => k switch
    {
        BaseItemKind.Movie => "M",
        BaseItemKind.Series => "S",
        BaseItemKind.Episode => "E",
        BaseItemKind.MusicVideo => "V",
        BaseItemKind.Video => "H",
        BaseItemKind.BoxSet => "B",
        _ => k.ToString()
    }));

    private static string Operators(FieldDescriptor f)
    {
        var standard = QueryOperators.For(f.Type);
        if (f.Operators.SequenceEqual(standard))
        {
            return f.Type.ToString();
        }

        if (f.Operators.Take(standard.Count).SequenceEqual(standard))
        {
            return f.Type + " " + string.Join(' ', f.Operators.Skip(standard.Count));
        }

        return string.Join(' ', f.Operators);
    }

    private static string Values(FieldDescriptor f)
    {
        var text = f.Type.ToString();
        if (f.ElementKind != FieldElementKind.None)
        {
            text += " of " + f.ElementKind;
        }

        if (f.Min is not null || f.Max is not null)
        {
            text += " " + f.Min + "–" + f.Max;
        }

        if (f.Integer)
        {
            text += " whole";
        }

        if (f.MaxSpan is { } span)
        {
            text += " span " + span;
        }

        if (f.Choices is { } choices)
        {
            text += " [" + string.Join(' ', choices) + "]";
        }

        if (f.ValueSource is { } source)
        {
            text += " from " + source;
        }

        return text;
    }
}
