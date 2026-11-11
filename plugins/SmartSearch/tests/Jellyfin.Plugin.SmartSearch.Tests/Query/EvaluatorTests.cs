using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Query;

/// <summary>
/// The evaluator tables: each field's in-memory semantics, with the null semantics,
/// diacritics and case, negative operators as <c>NOT(positive)</c>, resolution buckets and <c>InLast</c> boundaries.
/// Every rule goes through the registry's validator first, so the tables only use rules a client could send.
/// </summary>
public sealed class EvaluatorTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly FieldRegistry Registry = new();
    private static readonly QueryValidator Validator = new(Registry);
    private static readonly Dictionary<string, int> Scores = new(StringComparer.Ordinal) { ["G"] = 0, ["PG"] = 10, ["PG-13"] = 13, ["R"] = 17 };
    private static readonly TimeZoneInfo West = TimeZoneInfo.CreateCustomTimeZone("Test-West", TimeSpan.FromHours(-5), "UTC-05", "UTC-05");
    private static readonly TimeZoneInfo East = TimeZoneInfo.CreateCustomTimeZone("Test-East", TimeSpan.FromHours(10), "UTC+10", "UTC+10");
    private static readonly EvaluationEnvironment Environment = new()
    {
        NowUtc = Now,
        TimeZone = TimeZoneInfo.Utc,
        RatingScore = rating => Scores.TryGetValue(rating, out var score) ? new ParentalRatingScore(score, null) : null
    };

    // ------------------------------------------------------------------------------------------------ text

    [Theory]
    [InlineData("Contains", "amelie", "Amélie (2001)", true)]
    [InlineData("Contains", "AMÉLIE", "Amélie", true)]
    [InlineData("Contains", "creme brulee", "Crème Brûlée", true)]
    [InlineData("Contains", "x", null, false)]
    [InlineData("NotContains", "x", null, true)]
    [InlineData("NotContains", "amelie", "Amélie", false)]
    [InlineData("Eq", "amelie", "Amélie", true)]
    [InlineData("Eq", "amelie", "Amélie (2001)", false)]
    [InlineData("Ne", "amelie", "Amélie (2001)", true)]
    [InlineData("Ne", "amelie", "", true)]
    [InlineData("StartsWith", "ame", "Amélie", true)]
    [InlineData("StartsWith", "lie", "Amélie", false)]
    [InlineData("EndsWith", "LIE", "Amélie", true)]
    [InlineData("Matches", "^Amé", "Amélie", true)]
    [InlineData("Matches", "^amé", "Amélie", true)]
    [InlineData("Matches", "^amel", "Amélie", false)]
    [InlineData("IsEmpty", null, "  ", true)]
    [InlineData("IsEmpty", null, "Amélie", false)]
    [InlineData("IsNotEmpty", null, "Amélie", true)]
    public void Title_IsFolded_AndNegativesAreNotPositive(string op, string? value, string? name, bool expected)
    {
        var rule = value is null ? $$"""{"Field":"Title","Op":"{{op}}"}""" : $$"""{"Field":"Title","Op":"{{op}}","Value":"{{value}}"}""";
        Assert.Equal(expected, Compile(rule)(Row(new Movie { Name = name! })));
    }

    [Theory]
    [InlineData("""{"Field":"Title","Op":"Contains","Value":"poulain"}""", false)]
    [InlineData("""{"Field":"Title","Op":"Contains","Value":"poulain","IncludeOriginalTitle":true}""", true)]
    [InlineData("""{"Field":"Title","Op":"Contains","Value":"emilie","IncludeOriginalTitle":true}""", true)]
    [InlineData("""{"Field":"Title","Op":"NotContains","Value":"poulain","IncludeOriginalTitle":true}""", false)]
    [InlineData("""{"Field":"Title","Op":"Eq","Value":"le fabuleux destin d'émilie poulain","IncludeOriginalTitle":true}""", true)]
    [InlineData("""{"Field":"OriginalTitle","Op":"StartsWith","Value":"LE FABULEUX"}""", true)]
    public void Title_WithIncludeOriginalTitle_AlsoMatchesTheOriginalTitle(string rule, bool expected)
    {
        var movie = new Movie { Name = "Wonderful Fate", OriginalTitle = "Le Fabuleux Destin d'Émilie Poulain" };
        Assert.Equal(expected, Compile(rule)(Row(movie)));
    }

    [Fact]
    public void Title_IsEmpty_WithIncludeOriginalTitle_NeedsBothEmpty()
    {
        var test = Compile("""{"Field":"Title","Op":"IsEmpty","IncludeOriginalTitle":true}""");
        Assert.True(test(Row(new Movie { Name = string.Empty })));
        Assert.False(test(Row(new Movie { Name = string.Empty, OriginalTitle = "x" })));
    }

    [Theory]
    [InlineData("Contains", @"\movies\", true)]
    [InlineData("Contains", "AMÉLIE", true)]
    [InlineData("Contains", "amelie", false)]
    [InlineData("StartsWith", @"d:\MEDIA", true)]
    [InlineData("NotContains", @"\shows\", true)]
    [InlineData("Matches", @"\(\d{4}\)\.mkv$", true)]
    public void Path_IgnoresCase_ButNotAccents(string op, string value, bool expected)
    {
        var rule = $$"""{"Field":"Path","Op":"{{op}}","Value":{{System.Text.Json.JsonSerializer.Serialize(value)}}}""";
        Assert.Equal(expected, Compile(rule)(Row(new Movie { Path = @"D:\Media\Movies\Amélie (2001).mkv" })));
    }

    [Fact]
    public void SeriesName_ReadsTheEpisodesSeriesName()
    {
        var test = Compile("""{"Field":"SeriesName","Op":"Contains","Value":"alpha"}""");
        Assert.True(test(Row(new Episode { SeriesName = "Alpha Show" })));
        Assert.False(test(Row(new Episode { SeriesName = "Beta Show" })));
        Assert.False(test(Row(new Movie { Name = "Alpha Show" })));
    }

    // ------------------------------------------------------------------------------------------------ numbers

    [Theory]
    [InlineData("""{"Field":"CommunityRating","Op":"Eq","Value":7.3}""", 7.3f, true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Ge","Value":7.3}""", 7.3f, true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Gt","Value":7.3}""", 7.3f, false)]
    [InlineData("""{"Field":"CommunityRating","Op":"Between","From":7,"To":7.3}""", 7.3f, true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Lt","Value":7}""", 6.9f, true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Le","Value":6.9}""", 6.9f, true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Eq","Value":7.3}""", null, false)]
    [InlineData("""{"Field":"CommunityRating","Op":"Ne","Value":7.3}""", null, true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Lt","Value":10}""", null, false)]
    [InlineData("""{"Field":"CommunityRating","Op":"Between","From":0,"To":10}""", null, false)]
    [InlineData("""{"Field":"CommunityRating","Op":"IsEmpty"}""", null, true)]
    [InlineData("""{"Field":"CommunityRating","Op":"IsNotEmpty"}""", 0f, true)]
    public void CommunityRating_ComparesTheNumberShown_AndEmptyFailsPositives(string rule, float? rating, bool expected)
    {
        Assert.Equal(expected, Compile(rule)(Row(new Movie { CommunityRating = rating })));
    }

    // A stored community rating of 0 is a rating, not an empty one: core's own filter counts it (MinCommunityRating 0
    // returns it, in SQL and in memory), although its DTO leaves a 0 out and item pages show no rating for it.
    [Theory]
    [InlineData("""{"Field":"CommunityRating","Op":"IsEmpty"}""", false)]
    [InlineData("""{"Field":"CommunityRating","Op":"IsNotEmpty"}""", true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Ge","Value":0}""", true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Eq","Value":0}""", true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Between","From":0,"To":0.5}""", true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Lt","Value":0.5}""", true)]
    [InlineData("""{"Field":"CommunityRating","Op":"Gt","Value":0}""", false)]
    [InlineData("""{"Field":"CommunityRating","Op":"Ne","Value":0}""", false)]
    public void CommunityRating_StoredZero_IsARating_AsCoresFilterHasIt(string rule, bool expected)
    {
        Assert.Equal(expected, Compile(rule)(Row(new Movie { CommunityRating = 0f })));
    }

    // Ratings compare as SQLite stores them (REAL, the float widened): the rule's number is rounded to a float, so the
    // float just below 7.3f is below 7.3 here as it is in SQL. Through decimal it read as 7.3 and matched "Ge 7.3", which
    // the planner's MinCommunityRating pushdown would then have dropped.
    [Theory]
    [InlineData("Ge", 7.1, 7.1f, true)]
    [InlineData("Eq", 7.1, 7.1f, true)]
    [InlineData("Le", 7.1, 7.1f, true)]
    [InlineData("Gt", 7.1, 7.1f, false)]
    [InlineData("Ge", 7.3, 7.29999971f, false)]
    [InlineData("Eq", 7.3, 7.29999971f, false)]
    [InlineData("Lt", 7.3, 7.29999971f, true)]
    [InlineData("Le", 7.3, 7.30000019f, true)]
    public void CommunityRating_ComparesInFloatSpace_AsStored(string op, double value, float rating, bool expected)
    {
        var rule = $$"""{"Field":"CommunityRating","Op":"{{op}}","Value":{{value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}}""";
        Assert.Equal(expected, Compile(rule)(Row(new Movie { CommunityRating = rating })));
        Assert.Equal(expected, Compile(rule.Replace("CommunityRating", "CriticRating", StringComparison.Ordinal))(Row(new Movie { CriticRating = rating })));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(1799, true)]
    [InlineData(1800, false)]
    [InlineData(2100, false)]
    [InlineData(2101, true)]
    [InlineData(9999, true)]
    public void Year_OutsideTheValidatorsRange_IsNoYear(int? year, bool empty)
    {
        Assert.Equal(empty, Compile("""{"Field":"Year","Op":"IsEmpty"}""")(Row(new Movie { ProductionYear = year })));
        Assert.Equal(empty, Compile("""{"Field":"Year","Op":"Ne","Value":1800}""")(Row(new Movie { ProductionYear = year })) && Compile("""{"Field":"Year","Op":"Ne","Value":2100}""")(Row(new Movie { ProductionYear = year })));
    }

    [Fact]
    public void Year_AnyOf_AndBetween()
    {
        var anyOf = Compile("""{"Field":"Year","Op":"AnyOf","Values":[1999,2001]}""");
        var between = Compile("""{"Field":"Year","Op":"Between","From":1980,"To":1989}""");
        Assert.True(anyOf(Row(new Movie { ProductionYear = 2001 })));
        Assert.False(anyOf(Row(new Movie { ProductionYear = 2000 })));
        Assert.True(between(Row(new Movie { ProductionYear = 1980 })));
        Assert.True(between(Row(new Movie { ProductionYear = 1989 })));
        Assert.False(between(Row(new Movie { ProductionYear = 1990 })));
    }

    [Theory]
    [InlineData(89L * 60 + 29, 89)]
    [InlineData(89L * 60 + 30, 90)]
    [InlineData(90L * 60 + 29, 90)]
    [InlineData(30L, 1)]
    [InlineData(29L, 1)]
    [InlineData(1L, 1)]
    public void Runtime_IsRoundedToWholeMinutes(long seconds, int minutes)
    {
        var movie = new Movie { RunTimeTicks = seconds * TimeSpan.TicksPerSecond };
        Assert.True(Compile($$"""{"Field":"Runtime","Op":"Eq","Value":{{minutes}}}""")(Row(movie)));
        Assert.False(Compile($$"""{"Field":"Runtime","Op":"Eq","Value":{{minutes + 1}}}""")(Row(movie)));
    }

    [Fact]
    public void Runtime_FileSize_Bitrate_AreInTheirUnits_AndZeroIsUnknown()
    {
        var movie = new Movie { RunTimeTicks = TimeSpan.FromMinutes(90.25).Ticks, Size = 3L * 1024 * 1024 * 1024 / 2, TotalBitrate = 8_000_000 };
        Assert.True(Compile("""{"Field":"Runtime","Op":"Eq","Value":90}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"FileSize","Op":"Eq","Value":1.5}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"Bitrate","Op":"Eq","Value":8}""")(Row(movie)));
        var unknown = new Movie { RunTimeTicks = 0, Size = 0, TotalBitrate = 0 };
        Assert.True(Compile("""{"Field":"Runtime","Op":"IsEmpty"}""")(Row(unknown)));
        Assert.True(Compile("""{"Field":"FileSize","Op":"IsEmpty"}""")(Row(unknown)));
        Assert.True(Compile("""{"Field":"Bitrate","Op":"IsEmpty"}""")(Row(new Movie())));
    }

    // ------------------------------------------------------------------------------------------------ resolution

    [Theory]
    [InlineData(3840, "2160p")]
    [InlineData(3800, "2160p")]
    [InlineData(3799, "1440p")]
    [InlineData(2560, "1440p")]
    [InlineData(2500, "1440p")]
    [InlineData(2499, "1080p")]
    [InlineData(1920, "1080p")]
    [InlineData(1900, "1080p")]
    [InlineData(1899, "720p")]
    [InlineData(1280, "720p")]
    [InlineData(1200, "720p")]
    [InlineData(1199, "SD")]
    [InlineData(720, "SD")]
    [InlineData(1, "SD")]
    [InlineData(0, "Unknown")]
    public void Resolution_BucketsTheWidth(int width, string bucket)
    {
        Assert.Equal(bucket, ItemFacts.Resolution(width));
        Assert.True(Compile($$"""{"Field":"Resolution","Op":"AnyOf","Values":["{{bucket}}"]}""")(Row(new Movie { Width = width })));
    }

    [Fact]
    public void Resolution_OfAScopeFilm_IsItsWidths()
    {
        var scope = new Movie { Width = 1920, Height = 800 };
        Assert.True(Compile("""{"Field":"Resolution","Op":"AnyOf","Values":["1080p"]}""")(Row(scope)));
        Assert.True(Compile("""{"Field":"Resolution","Op":"NoneOf","Values":["720p","Unknown"]}""")(Row(scope)));
        Assert.False(Compile("""{"Field":"Resolution","Op":"NoneOf","Values":["Unknown"]}""")(Row(new Movie())));
    }

    // ------------------------------------------------------------------------------------------------ container

    // Probing stores a format's comma-joined names ("mov,mp4,m4a,3gp,3g2,mj2" for an MP4 file); each is a token.
    [Theory]
    [InlineData("""{"Field":"Container","Op":"AnyOf","Values":["mp4"]}""", "mov,mp4,m4a,3gp,3g2,mj2", true)]
    [InlineData("""{"Field":"Container","Op":"AnyOf","Values":["MP4"]}""", "mov, mp4", true)]
    [InlineData("""{"Field":"Container","Op":"AnyOf","Values":["mp"]}""", "mov,mp4", false)]
    [InlineData("""{"Field":"Container","Op":"AnyOf","Values":["mov,mp4"]}""", "mov,mp4", false)]
    [InlineData("""{"Field":"Container","Op":"NoneOf","Values":["mkv"]}""", "mov,mp4", true)]
    [InlineData("""{"Field":"Container","Op":"NoneOf","Values":["mp4"]}""", "mov,mp4", false)]
    [InlineData("""{"Field":"Container","Op":"NoneOf","Values":["mp4"]}""", ",", true)]
    [InlineData("""{"Field":"Container","Op":"AnyOf","Values":["mkv"]}""", "mkv", true)]
    public void Container_IsAListOfCommaSeparatedTokens(string rule, string container, bool expected)
        => Assert.Equal(expected, Compile(rule)(Row(new Movie { Container = container })));

    // ------------------------------------------------------------------------------------------------ kinds

    // A rule is false on a row whose kind its field doesn't apply to, whatever the operator: a
    // query over movies and series must not match every movie with "SeriesStatus NoneOf [Ended]" or "Played Is false".
    [Theory]
    [InlineData("""{"Field":"SeriesStatus","Op":"NoneOf","Values":["Ended"]}""", false, true)]
    [InlineData("""{"Field":"SeriesStatus","Op":"AnyOf","Values":["Continuing"]}""", false, true)]
    [InlineData("""{"Field":"Played","Op":"Is","Value":false}""", true, false)]
    [InlineData("""{"Field":"InProgress","Op":"Is","Value":false}""", true, false)]
    [InlineData("""{"Field":"PlayCount","Op":"Ne","Value":3}""", true, false)]
    [InlineData("""{"Field":"Runtime","Op":"IsEmpty"}""", true, false)]
    [InlineData("""{"Field":"CriticRating","Op":"IsEmpty"}""", true, true)]
    [InlineData("""{"Field":"SeriesName","Op":"NotContains","Value":"x"}""", false, false)]
    [InlineData("""{"Field":"OriginalTitle","Op":"IsEmpty"}""", true, true)]
    public void ARule_IsFalseOnKindsItsFieldDoesNotApplyTo(string rule, bool onMovie, bool onSeries)
    {
        var test = Compile(rule);
        Assert.Equal(onMovie, test(Row(new Movie())));
        Assert.Equal(onSeries, test(Row(new Series { Status = SeriesStatus.Continuing })));
        Assert.False(test(Row(new BoxSet())));
    }

    // ------------------------------------------------------------------------------------------------ dates

    [Theory]
    [InlineData(0L, true)]
    [InlineData(-1L, false)]
    [InlineData(30 * TimeSpan.TicksPerDay, true)]
    [InlineData(30 * TimeSpan.TicksPerDay + 1, false)]
    public void InLast_IncludesBothEnds_AndNotTheFuture(long ticksAfterStart, bool expected)
    {
        // ticksAfterStart counts forward from the start of the span (now - 30 days); -1 is one tick before it.
        var start = Now.AddDays(-30);
        var date = start.AddTicks(ticksAfterStart);
        Assert.Equal(expected, Compile("""{"Field":"DateAdded","Op":"InLast","Amount":30,"Unit":"Days"}""")(Row(new Movie { DateCreated = date })));
        Assert.Equal(!expected, Compile("""{"Field":"DateAdded","Op":"NotInLast","Amount":30,"Unit":"Days"}""")(Row(new Movie { DateCreated = date })));
    }

    [Fact]
    public void InLast_MonthsAndYearsAreCalendarSpans_WeeksAreSevenDays()
    {
        Assert.True(Compile("""{"Field":"PremiereDate","Op":"InLast","Amount":1,"Unit":"Months"}""")(Row(new Movie { PremiereDate = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc) })));
        Assert.False(Compile("""{"Field":"PremiereDate","Op":"InLast","Amount":1,"Unit":"Months"}""")(Row(new Movie { PremiereDate = new DateTime(2026, 10, 2, 11, 59, 59, DateTimeKind.Utc) })));
        Assert.True(Compile("""{"Field":"PremiereDate","Op":"InLast","Amount":1,"Unit":"Years"}""")(Row(new Movie { PremiereDate = new DateTime(2025, 11, 2, 12, 0, 0, DateTimeKind.Utc) })));
        Assert.True(Compile("""{"Field":"PremiereDate","Op":"InLast","Amount":2,"Unit":"Weeks"}""")(Row(new Movie { PremiereDate = Now.AddDays(-14) })));
        Assert.False(Compile("""{"Field":"PremiereDate","Op":"InLast","Amount":2,"Unit":"Weeks"}""")(Row(new Movie { PremiereDate = Now.AddDays(-14).AddTicks(-1) })));
    }

    [Fact]
    public void InLast_DropsFutureDates_AndNotInLastKeepsThemAndEmptyOnes()
    {
        var future = new Movie { PremiereDate = Now.AddDays(1) };
        var none = new Movie();
        var inLast = Compile("""{"Field":"PremiereDate","Op":"InLast","Amount":30,"Unit":"Days"}""");
        var notInLast = Compile("""{"Field":"PremiereDate","Op":"NotInLast","Amount":30,"Unit":"Days"}""");
        Assert.False(inLast(Row(future)));
        Assert.False(inLast(Row(none)));
        Assert.True(notInLast(Row(future)));
        Assert.True(notInLast(Row(none)));
    }

    // A date-only value is a whole calendar day: Before d is before the day, After d from the
    // next day, Between a b to the end of b. A time with a zone is an exact instant.
    [Theory]
    [InlineData("""{"Field":"PremiereDate","Op":"Before","Value":"2020-01-01"}""", "2019-12-31T23:59:59Z", true)]
    [InlineData("""{"Field":"PremiereDate","Op":"Before","Value":"2020-01-01"}""", "2020-01-01T00:00:00Z", false)]
    [InlineData("""{"Field":"PremiereDate","Op":"After","Value":"2020-01-01"}""", "2020-01-01T00:00:00Z", false)]
    [InlineData("""{"Field":"PremiereDate","Op":"After","Value":"2020-01-01"}""", "2020-01-01T23:59:59Z", false)]
    [InlineData("""{"Field":"PremiereDate","Op":"After","Value":"2020-01-01"}""", "2020-01-02T00:00:00Z", true)]
    [InlineData("""{"Field":"PremiereDate","Op":"After","Value":"2020-01-01T02:00:00+02:00"}""", "2020-01-01T00:00:00Z", false)]
    [InlineData("""{"Field":"PremiereDate","Op":"After","Value":"2020-01-01T02:00:00+02:00"}""", "2020-01-01T00:00:01Z", true)]
    [InlineData("""{"Field":"PremiereDate","Op":"Before","Value":"2020-01-01T00:00:00Z"}""", "2019-12-31T23:59:59Z", true)]
    [InlineData("""{"Field":"PremiereDate","Op":"Between","From":"2020-01-01","To":"2020-12-31"}""", "2020-01-01T00:00:00Z", true)]
    [InlineData("""{"Field":"PremiereDate","Op":"Between","From":"2020-01-01","To":"2020-12-31"}""", "2019-12-31T23:59:59Z", false)]
    [InlineData("""{"Field":"PremiereDate","Op":"Between","From":"2020-01-01","To":"2020-12-31"}""", "2020-12-31T00:00:00Z", true)]
    [InlineData("""{"Field":"PremiereDate","Op":"Between","From":"2020-01-01","To":"2020-12-31"}""", "2020-12-31T23:59:59Z", true)]
    [InlineData("""{"Field":"PremiereDate","Op":"Between","From":"2020-01-01","To":"2020-12-31"}""", "2021-01-01T00:00:00Z", false)]
    [InlineData("""{"Field":"PremiereDate","Op":"Between","From":"2020-01-01","To":"2020-01-01"}""", "2020-01-01T12:00:00Z", true)]
    [InlineData("""{"Field":"PremiereDate","Op":"Between","From":"2020-01-01T00:00:00Z","To":"2020-12-31T00:00:00Z"}""", "2020-12-31T00:00:01Z", false)]
    public void Days_AreWholeDays_InstantsAreExact(string rule, string date, bool expected)
    {
        var premiere = DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.Equal(expected, Compile(rule)(Row(new Movie { PremiereDate = premiere })));
    }

    // An instant (DateCreated, LastPlayedDate) is on the server's local calendar day: on a server five hours west of
    // UTC, 23:30 local on 31 January is 04:30Z on 1 February and still counts as 31 January.
    [Theory]
    [InlineData("West", """{"Field":"DateAdded","Op":"Between","From":"2026-01-01","To":"2026-01-31"}""", true)]
    [InlineData("Utc", """{"Field":"DateAdded","Op":"Between","From":"2026-01-01","To":"2026-01-31"}""", false)]
    [InlineData("West", """{"Field":"DateAdded","Op":"After","Value":"2026-01-31"}""", false)]
    [InlineData("Utc", """{"Field":"DateAdded","Op":"After","Value":"2026-01-31"}""", true)]
    [InlineData("East", """{"Field":"DateAdded","Op":"Before","Value":"2026-02-01"}""", false)]
    [InlineData("West", """{"Field":"DateAdded","Op":"Before","Value":"2026-02-01"}""", true)]
    [InlineData("West", """{"Field":"DateAdded","Op":"Before","Value":"2026-02-01T00:00:00Z"}""", false)]
    [InlineData("West", """{"Field":"LastPlayed","Op":"Between","From":"2026-01-31","To":"2026-01-31"}""", true)]
    [InlineData("East", """{"Field":"LastPlayed","Op":"Between","From":"2026-02-01","To":"2026-02-01"}""", true)]
    public void Instants_AreOnTheServersLocalDay(string zone, string rule, bool expected)
    {
        var utc = new DateTime(2026, 2, 1, 4, 30, 0, DateTimeKind.Utc);
        var environment = new EvaluationEnvironment { NowUtc = Now, TimeZone = Zone(zone) };
        var row = Row(new Movie { DateCreated = utc }, new UserItemData { Key = "k", LastPlayedDate = utc });
        Assert.Equal(expected, Compile(rule, environment)(row));
    }

    // PremiereDate is a calendar date: a stored 00:00:00Z (what NFO files write) is that UTC day on every server, and
    // any other time is the server's local day, which is where a date stored without a kind (TMDb's) lands, because
    // core's converter stored it as local midnight.
    [Theory]
    [InlineData("West")]
    [InlineData("Utc")]
    [InlineData("East")]
    public void PremiereDate_IsTheDayItNames_WestAndEastOfUtc(string zone)
    {
        var tz = Zone(zone);
        var environment = new EvaluationEnvironment { NowUtc = Now, TimeZone = tz };
        var on = Compile("""{"Field":"PremiereDate","Op":"Between","From":"2001-04-25","To":"2001-04-25"}""", environment);
        var before = Compile("""{"Field":"PremiereDate","Op":"Before","Value":"2001-04-25"}""", environment);
        var after = Compile("""{"Field":"PremiereDate","Op":"After","Value":"2001-04-25"}""", environment);
        var nfo = new DateTime(2001, 4, 25, 0, 0, 0, DateTimeKind.Utc);
        var unsaved = new DateTime(2001, 4, 25, 0, 0, 0, DateTimeKind.Unspecified);
        var stored = DateTime.SpecifyKind(unsaved - tz.GetUtcOffset(unsaved), DateTimeKind.Utc); // the converter's ToUniversalTime() on that server
        foreach (var premiere in new[] { nfo, unsaved, stored })
        {
            var row = Row(new Movie { PremiereDate = premiere });
            Assert.True(on(row), zone + " " + premiere.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(before(row));
            Assert.False(after(row));
        }
    }

    [Fact]
    public void Dates_UnsetDateCreatedIsEmpty_LocalTimesAreConverted_UnspecifiedIsTheServersLocalTime()
    {
        Assert.True(Compile("""{"Field":"DateAdded","Op":"IsEmpty"}""")(Row(new Movie { DateCreated = DateTime.MinValue })));
        var after = Compile("""{"Field":"DateAdded","Op":"After","Value":"2020-01-01T00:00:00Z"}""");
        Assert.True(after(Row(new Movie { DateCreated = new DateTime(2020, 1, 1, 0, 0, 1, DateTimeKind.Unspecified) })));
        Assert.False(after(Row(new Movie { DateCreated = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) })));
        var local = new DateTime(2020, 1, 1, 0, 0, 1, DateTimeKind.Utc).ToLocalTime();
        Assert.True(after(Row(new Movie { DateCreated = local })));

        // Five hours west, 19:00 without a kind is 00:00Z the next day, as the database would have stored it.
        var west = new EvaluationEnvironment { NowUtc = Now, TimeZone = West };
        var westAfter = Compile("""{"Field":"DateAdded","Op":"After","Value":"2019-12-31T23:59:59Z"}""", west);
        Assert.True(westAfter(Row(new Movie { DateCreated = new DateTime(2019, 12, 31, 19, 0, 0, DateTimeKind.Unspecified) })));
        Assert.False(westAfter(Row(new Movie { DateCreated = new DateTime(2019, 12, 31, 18, 59, 58, DateTimeKind.Unspecified) })));
    }

    // ------------------------------------------------------------------------------------------------ lists

    [Theory]
    [InlineData("""{"Field":"Genres","Op":"AnyOf","Values":["sci-fi"]}""", true)]
    [InlineData("""{"Field":"Genres","Op":"AnyOf","Values":["comedie"]}""", true)]
    [InlineData("""{"Field":"Genres","Op":"AllOf","Values":["drama","SCI-FI"]}""", true)]
    [InlineData("""{"Field":"Genres","Op":"AllOf","Values":["drama","horror"]}""", false)]
    [InlineData("""{"Field":"Genres","Op":"NoneOf","Values":["horror"]}""", true)]
    [InlineData("""{"Field":"Genres","Op":"NoneOf","Values":["drama"]}""", false)]
    [InlineData("""{"Field":"Genres","Op":"IsNotEmpty"}""", true)]
    public void Genres_CompareFolded(string rule, bool expected)
    {
        Assert.Equal(expected, Compile(rule)(Row(new Movie { Genres = ["Sci-Fi", "Drama", "Comédie"] })));
    }

    [Fact]
    public void Lists_WithOnlyBlankEntries_AreEmpty_AndNoneOfMatchesThem()
    {
        var blank = new Movie { Tags = [" ", string.Empty] };
        Assert.True(Compile("""{"Field":"Tags","Op":"IsEmpty"}""")(Row(blank)));
        Assert.True(Compile("""{"Field":"Tags","Op":"NoneOf","Values":["cult"]}""")(Row(blank)));
        Assert.False(Compile("""{"Field":"Tags","Op":"AnyOf","Values":["cult"]}""")(Row(blank)));
        Assert.True(Compile("""{"Field":"Studios","Op":"AnyOf","Values":["A24"]}""")(Row(new Movie { Studios = ["a24"] })));
    }

    // ------------------------------------------------------------------------------------------------ choices

    [Theory]
    [InlineData("""{"Field":"OfficialRating","Op":"AnyOf","Values":["PG-13"]}""", "PG-13", true)]
    [InlineData("""{"Field":"OfficialRating","Op":"AnyOf","Values":["pg-13"]}""", "PG-13", false)]
    [InlineData("""{"Field":"OfficialRating","Op":"NoneOf","Values":["PG-13"]}""", null, true)]
    [InlineData("""{"Field":"OfficialRating","Op":"Le","Value":"PG-13"}""", "PG", true)]
    [InlineData("""{"Field":"OfficialRating","Op":"Le","Value":"PG-13"}""", "PG-13", true)]
    [InlineData("""{"Field":"OfficialRating","Op":"Le","Value":"PG-13"}""", "R", false)]
    [InlineData("""{"Field":"OfficialRating","Op":"Le","Value":"PG-13"}""", null, false)]
    [InlineData("""{"Field":"OfficialRating","Op":"Le","Value":"PG-13"}""", "Unrated-ish", false)]
    [InlineData("""{"Field":"OfficialRating","Op":"Ge","Value":"PG-13"}""", "R", true)]
    [InlineData("""{"Field":"OfficialRating","Op":"Ge","Value":"PG-13"}""", "G", false)]
    [InlineData("""{"Field":"OfficialRating","Op":"Ge","Value":"No-such-rating"}""", "R", false)]
    public void OfficialRating_IsExactForChoices_AndByScoreForLeGe(string rule, string? rating, bool expected)
    {
        Assert.Equal(expected, Compile(rule)(Row(new Movie { OfficialRating = rating! })));
    }

    [Fact]
    public void SeriesStatus_Container_VideoType()
    {
        Assert.True(Compile("""{"Field":"SeriesStatus","Op":"AnyOf","Values":["Ended"]}""")(Row(new Series { Status = SeriesStatus.Ended })));
        Assert.False(Compile("""{"Field":"SeriesStatus","Op":"AnyOf","Values":["Ended"]}""")(Row(new Series())));
        Assert.True(Compile("""{"Field":"SeriesStatus","Op":"NoneOf","Values":["Ended"]}""")(Row(new Series())));
        Assert.True(Compile("""{"Field":"Container","Op":"AnyOf","Values":["MKV"]}""")(Row(new Movie { Container = "mkv" })));
        Assert.True(Compile("""{"Field":"VideoType","Op":"AnyOf","Values":["BluRay"]}""")(Row(new Movie { VideoType = VideoType.BluRay })));
        Assert.False(Compile("""{"Field":"VideoType","Op":"AnyOf","Values":["BluRay"]}""")(Row(new Movie())));
    }

    [Fact]
    public void Series_ItemType_Library_MatchById()
    {
        var seriesId = Guid.NewGuid();
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId };
        var other = new Episode { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid() };
        var movie = new Movie { Id = Guid.NewGuid() };
        var series = Compile($$"""{"Field":"Series","Op":"AnyOf","Values":[{"Id":"{{seriesId:N}}","Name":"Alpha Show"}]}""");
        Assert.True(series(Row(episode)));
        Assert.False(series(Row(other)));
        Assert.False(series(Row(movie)));
        Assert.True(Compile($$"""{"Field":"Series","Op":"NoneOf","Values":[{"Id":"{{seriesId:D}}"}]}""")(Row(other)));
        Assert.False(Compile($$"""{"Field":"Series","Op":"NoneOf","Values":[{"Id":"{{seriesId:D}}"}]}""")(Row(movie))); // Series is the episodes' field

        Assert.True(Compile("""{"Field":"ItemType","Op":"AnyOf","Values":["movie"]}""")(Row(movie)));
        Assert.False(Compile("""{"Field":"ItemType","Op":"AnyOf","Values":["Movie"]}""")(Row(episode)));

        var library = Guid.NewGuid();
        var environment = new EvaluationEnvironment { NowUtc = Now, TimeZone = TimeZoneInfo.Utc, LibraryMembers = ids => ids.Contains(library) ? new HashSet<Guid> { movie.Id } : [] };
        Assert.True(Compile($$"""{"Field":"Library","Op":"AnyOf","Values":["{{library}}"]}""", environment)(Row(movie)));
        Assert.False(Compile($$"""{"Field":"Library","Op":"AnyOf","Values":["{{library}}"]}""", environment)(Row(episode)));
        Assert.True(Compile($$"""{"Field":"Library","Op":"NoneOf","Values":["{{library}}"]}""", environment)(Row(episode)));
        Assert.Throws<InvalidOperationException>(() => Compile($$"""{"Field":"Library","Op":"AnyOf","Values":["{{library}}"]}"""));
    }

    // ------------------------------------------------------------------------------------------------ watch state, identity

    [Fact]
    public void WatchState_WithNoUserDataRow_IsUnplayedNotFavouriteNotInProgress()
    {
        var movie = new Movie();
        Assert.True(Compile("""{"Field":"Played","Op":"Is","Value":false}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"Favorite","Op":"Is","Value":false}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"InProgress","Op":"Is","Value":false}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"PlayCount","Op":"Eq","Value":0}""")(Row(movie)));
        Assert.False(Compile("""{"Field":"PlayCount","Op":"IsEmpty"}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"LastPlayed","Op":"IsEmpty"}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"LastPlayed","Op":"NotInLast","Amount":365,"Unit":"Days"}""")(Row(movie)));
    }

    [Fact]
    public void WatchState_ReadsTheUsersRow()
    {
        var data = new UserItemData { Key = "k", Played = true, IsFavorite = true, PlaybackPositionTicks = 10, PlayCount = 3, LastPlayedDate = Now.AddDays(-2) };
        var movie = new Movie();
        Assert.True(Compile("""{"Field":"Played","Op":"Is","Value":true}""")(Row(movie, data)));
        Assert.True(Compile("""{"Field":"Favorite","Op":"Is","Value":true}""")(Row(movie, data)));
        Assert.True(Compile("""{"Field":"InProgress","Op":"Is","Value":true}""")(Row(movie, data)));
        Assert.True(Compile("""{"Field":"PlayCount","Op":"Ge","Value":3}""")(Row(movie, data)));
        Assert.True(Compile("""{"Field":"LastPlayed","Op":"InLast","Amount":7,"Unit":"Days"}""")(Row(movie, data)));
        Assert.False(Compile("""{"Field":"InProgress","Op":"Is","Value":true}""")(Row(movie, new UserItemData { Key = "k" })));
    }

    [Fact]
    public void Identity_ProviderIds_Is3D_Locked()
    {
        var movie = new Movie { IsLocked = true, Video3DFormat = Video3DFormat.HalfSideBySide };
        movie.ProviderIds["Tmdb"] = "603";
        movie.ProviderIds["Imdb"] = "tt0133093";
        movie.ProviderIds["Tvdb"] = " ";
        Assert.True(Compile("""{"Field":"HasProviderId","Op":"Is","Value":true,"Provider":"Tmdb"}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"HasProviderId","Op":"Is","Value":false,"Provider":"tvdb"}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"ProviderId","Op":"Eq","Value":"TT0133093","Provider":"Imdb"}""")(Row(movie)));
        Assert.False(Compile("""{"Field":"ProviderId","Op":"Eq","Value":"603","Provider":"Imdb"}""")(Row(movie)));
        Assert.True(Compile("""{"Field":"Is3D","Op":"Is","Value":true}""")(Row(movie)));
        Assert.False(Compile("""{"Field":"Is3D","Op":"Is","Value":true}""")(Row(new Movie())));
        Assert.True(Compile("""{"Field":"Locked","Op":"Is","Value":true}""")(Row(movie)));
    }

    // ------------------------------------------------------------------------------------------------ NOT(positive)

    public static TheoryData<string, string> NegativePairs() => new()
    {
        { """{"Field":"Title","Op":"Contains","Value":"a"}""", """{"Field":"Title","Op":"NotContains","Value":"a"}""" },
        { """{"Field":"Title","Op":"Eq","Value":"amelie","IncludeOriginalTitle":true}""", """{"Field":"Title","Op":"Ne","Value":"amelie","IncludeOriginalTitle":true}""" },
        { """{"Field":"Overview","Op":"IsNotEmpty"}""", """{"Field":"Overview","Op":"IsEmpty"}""" },
        { """{"Field":"Year","Op":"Eq","Value":2001}""", """{"Field":"Year","Op":"Ne","Value":2001}""" },
        { """{"Field":"Year","Op":"IsNotEmpty"}""", """{"Field":"Year","Op":"IsEmpty"}""" },
        { """{"Field":"Genres","Op":"AnyOf","Values":["drama","comedy"]}""", """{"Field":"Genres","Op":"NoneOf","Values":["drama","comedy"]}""" },
        { """{"Field":"OfficialRating","Op":"AnyOf","Values":["R"]}""", """{"Field":"OfficialRating","Op":"NoneOf","Values":["R"]}""" },
        { """{"Field":"Resolution","Op":"AnyOf","Values":["1080p"]}""", """{"Field":"Resolution","Op":"NoneOf","Values":["1080p"]}""" },
        { """{"Field":"PremiereDate","Op":"InLast","Amount":10,"Unit":"Years"}""", """{"Field":"PremiereDate","Op":"NotInLast","Amount":10,"Unit":"Years"}""" },
        { """{"Field":"Played","Op":"Is","Value":true}""", """{"Field":"Played","Op":"Is","Value":false}""" },
    };

    [Theory]
    [MemberData(nameof(NegativePairs))]
    public void EveryNegativeOperator_IsNotThePositive_OverEveryItem(string positive, string negative)
    {
        // ...on the kinds the field applies to; on the others both are false.
        var pos = Compile(positive);
        var neg = Compile(negative);
        var kinds = Registry.Find(System.Text.Json.JsonDocument.Parse(positive).RootElement.GetProperty("Field").GetString()!)!.Kinds;
        Assert.All(Corpus(), row =>
        {
            if (kinds.Contains(row.Item.GetBaseItemKind()))
            {
                Assert.NotEqual(pos(row), neg(row));
            }
            else
            {
                Assert.False(pos(row) || neg(row));
            }
        });
    }

    private static IEnumerable<EvaluationRow> Corpus()
    {
        yield return Row(new Movie());
        yield return Row(new Movie { Name = "Amélie", OriginalTitle = "Le Fabuleux Destin", ProductionYear = 2001, Genres = ["Drama"], OfficialRating = "R", Width = 1920, PremiereDate = new DateTime(2001, 4, 25, 0, 0, 0, DateTimeKind.Utc), Overview = "A waitress." });
        yield return Row(new Movie { Name = "Zz", ProductionYear = 0, Genres = [" "], OfficialRating = string.Empty, Width = 0 }, new UserItemData { Key = "k", Played = true });
        yield return Row(new Episode { Name = "Pilot", ProductionYear = 9999, PremiereDate = Now.AddDays(3) });
        yield return Row(new Series { Name = "amelie", Genres = ["comedy"] });
    }

    private static EvaluationRow Row(BaseItem item, UserItemData? data = null) => new(item, data);

    private static TimeZoneInfo Zone(string name) => name switch { "West" => West, "East" => East, _ => TimeZoneInfo.Utc };

    private static Func<EvaluationRow, bool> Compile(string rule, EvaluationEnvironment? environment = null)
    {
        var json = """{"Scope":{"ItemKinds":["Movie","Series","Episode","MusicVideo","Video","BoxSet"]},"Where":{"Children":[""" + rule + "]}}";
        var read = QueryJson.Read(json);
        Assert.True(read.Definition is not null, rule);
        var validation = Validator.Validate(read.Definition!, TestCatalog.AdminWithRegex); // Matches is dormant, still tested
        Assert.True(validation.Valid, rule + " → " + string.Join(", ", validation.Errors.Select(e => e.Path + ":" + e.Code)));
        var node = read.Definition!.Where.Children![0];
        return Registry.EvaluatorOf(Registry.Find(node.Field!)!).Compile(node, environment ?? Environment);
    }
}
