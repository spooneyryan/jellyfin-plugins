using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.Query.TestCatalog;

namespace Jellyfin.Plugin.SmartSearch.Tests.Query;

/// <summary>
/// Every limit of <see cref="QueryLimits"/> at and past its bound, field and operator compatibility, admin-only
/// fields, unknown fields (unsupported), regex compile errors, and the structure rules of the query model.
/// </summary>
public sealed class QueryValidatorTests
{
    private static string Rule(string body) => "{" + body + "}";

    private static string Rules(int count, string rule = "\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"a\"")
        => string.Join(",", Enumerable.Repeat(Rule(rule), count));

    private static string Nested(int depth)
    {
        // depth groups in all, counting the root: the innermost holds one rule.
        var inner = Rule("\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"a\"");
        for (var i = 1; i < depth; i++)
        {
            inner = "{\"Match\":\"Any\",\"Children\":[" + inner + "]}";
        }

        return inner;
    }

    private static string Values(int count) => string.Join(",", Enumerable.Range(0, count).Select(i => "\"g" + i + "\""));

    [Fact]
    public void AFullExample_IsValid()
    {
        var json = """
        {
          "Version": 1,
          "Scope": { "ItemKinds": ["Movie"], "LibraryIds": ["f137a2dd21bbc1b99aa5c0f6bf02a805"], "IncludeMissing": false },
          "Where": {
            "Match": "All",
            "Negate": false,
            "Children": [
              { "Field": "Genres", "Op": "AnyOf", "Values": ["Horror", "Thriller"] },
              { "Field": "Year", "Op": "Between", "From": 1980, "To": 1989 },
              { "Match": "Any", "Children": [
                  { "Field": "CommunityRating", "Op": "Ge", "Value": 7 },
                  { "Field": "Genres", "Op": "AnyOf", "Values": ["cult"] } ] },
              { "Field": "Title", "Op": "NotContains", "Value": "part", "IncludeOriginalTitle": false },
              { "Field": "Played", "Op": "Is", "Value": false },
              { "Field": "PremiereDate", "Op": "InLast", "Amount": 90, "Unit": "Days" }
            ]
          },
          "Sort": [ { "Key": "PremiereDate", "Order": "Descending" } ],
          "Limit": null
        }
        """;
        var result = Validate(json);
        Assert.Empty(result.Errors);
        Assert.True(result.Valid);
        Assert.True(result.Supported);
        Assert.True(result.Runnable);
        Assert.True(result.UsesPerUserFields);
        Assert.False(result.UsesAdminOnlyFields);
    }

    [Fact]
    public void AnEmptyRoot_IsValid_AndMatchesEverythingInScope()
    {
        Assert.Empty(Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]}}"));
        Assert.Empty(Errors(WithChildren(string.Empty)));
        Assert.Empty(Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Match\":\"All\",\"Negate\":false,\"Children\":[]}}"));
    }

    // "Any of nothing" and "not everything" would match nothing; refused rather than given a meaning.
    [Theory]
    [InlineData("{\"Match\":\"Any\",\"Children\":[]}")]
    [InlineData("{\"Negate\":true,\"Children\":[]}")]
    [InlineData("{\"Match\":\"Any\",\"Negate\":true,\"Children\":[]}")]
    public void AnEmptyRoot_ThatIsAnyOrNegated_IsAnEmptyGroup(string where)
        => Assert.Equal(["Where:EmptyGroup"], Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":" + where + "}"));

    // The limits, at the bound (valid) and one past it.
    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void Depth_IsAtMost4(int depth, bool valid)
    {
        var errors = Errors(WithChildren(Nested(depth)));
        Assert.Equal(valid, errors.Count == 0);
        if (!valid)
        {
            Assert.Equal(["Where.Children[0].Children[0].Children[0].Children[0]:TooDeep"], errors);
        }
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void Rules_AreAtMost50_AcrossGroups(int count, bool valid)
    {
        // Spread over two groups so no group passes 50 children.
        var first = count / 2;
        var json = WithChildren("{\"Children\":[" + Rules(first) + "]},{\"Children\":[" + Rules(count - first) + "]}");
        Assert.Equal(valid ? [] : ["Where:TooManyRules"], Errors(json));
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void ChildrenPerGroup_AreAtMost50(int count, bool valid)
    {
        var errors = Errors(WithChildren("{\"Match\":\"Any\",\"Children\":[" + Rules(count, "\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":true") + "]}"));
        Assert.Equal(valid, !errors.Contains("Where.Children[0]:TooManyChildren"));
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void ValuesPerRule_AreAtMost100(int count, bool valid)
        => Assert.Equal(valid ? [] : ["Where.Children[0].Values:TooManyValues"], Errors(WithChildren(Rule("\"Field\":\"Genres\",\"Op\":\"AnyOf\",\"Values\":[" + Values(count) + "]"))));

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void TextValues_AreAtMost200Characters(int length, bool valid)
    {
        var text = new string('x', length);
        Assert.Equal(valid ? [] : ["Where.Children[0].Value:TextTooLong"], Errors(WithChildren(Rule("\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"  " + text + " \""))));
        Assert.Equal(valid ? [] : ["Where.Children[0].Values[0]:TextTooLong"], Errors(WithChildren(Rule("\"Field\":\"Genres\",\"Op\":\"AnyOf\",\"Values\":[\"" + text + "\"]"))));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void TextValues_AreNotEmptyAfterTrimming(string value)
        => Assert.Equal(["Where.Children[0].Value:EmptyText"], Errors(WithChildren(Rule("\"Field\":\"Title\",\"Op\":\"Eq\",\"Value\":" + value))));

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void MatchesPatterns_AreAtMost200Characters(int length, bool valid)
        => Assert.Equal(
            valid ? [] : ["Where.Children[0].Value:TextTooLong"],
            Errors(WithChildren(Rule("\"Field\":\"Path\",\"Op\":\"Matches\",\"Value\":\"" + new string('a', length) + "\"")), TestCatalog.AdminWithRegex));

    [Theory]
    [InlineData("(unclosed")]
    [InlineData("[z-a]")]
    [InlineData("(a)\\\\1")] // a back reference: NonBacktracking doesn't support it
    [InlineData("(?=a)b")] // a lookahead: the same
    public void MatchesPatterns_ThatDontCompile_AreInvalidRegex(string pattern)
        => Assert.Equal(
            ["Where.Children[0].Value:InvalidRegex"],
            Errors(WithChildren(Rule("\"Field\":\"Path\",\"Op\":\"Matches\",\"Value\":\"" + pattern + "\"")), TestCatalog.AdminWithRegex));

    [Fact]
    public void MatchesPatterns_CompileNonBacktrackingIgnoringCase_WithTheTimeout()
    {
        Assert.Empty(Errors(WithChildren(Rule("\"Field\":\"Path\",\"Op\":\"Matches\",\"Value\":\"^(a+)+$\"")), TestCatalog.AdminWithRegex));
        var regex = QueryValidator.CompilePattern("^(a+)+$");
        Assert.Equal(System.Text.RegularExpressions.RegexOptions.NonBacktracking | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, regex.Options);
        Assert.Equal(TimeSpan.FromMilliseconds(250), regex.MatchTimeout);
        Assert.Matches(regex, "AAAA");
        Assert.DoesNotMatch(regex, new string('a', 5000) + "!"); // linear time: no catastrophic backtracking
    }

    [Fact]
    public void Matches_WithEnableRegexOff_IsRegexDisabled()
        => Assert.Equal(
            ["Where.Children[0].Op:RegexDisabled"],
            Errors(WithChildren(Rule("\"Field\":\"Path\",\"Op\":\"Matches\",\"Value\":\"a.b\"")), new QueryValidationContext(true, false, [BaseItemKind.Movie])));

    // Matches is not offered yet: regex is off by default, and a pattern is refused before it is read, so a
    // pattern that doesn't compile (or one too long) reports only RegexDisabled.
    [Theory]
    [InlineData("(unclosed")]
    [InlineData("(?=a)b")]
    [InlineData("a.b")]
    public void Matches_IsOffByDefault_AndRefusedWithoutCompiling(string pattern)
    {
        Assert.False(QueryValidationContext.User.EnableRegex);
        Assert.False(QueryValidationContext.Admin.EnableRegex);
        var rule = WithChildren(Rule("\"Field\":\"Path\",\"Op\":\"Matches\",\"Value\":\"" + pattern + "\""));
        Assert.Equal(["Where.Children[0].Op:RegexDisabled"], Errors(rule, QueryValidationContext.Admin));
        Assert.Equal(["Where.Children[0].Op:RegexDisabled"], Errors(WithChildren(Rule("\"Field\":\"Path\",\"Op\":\"Matches\",\"Value\":\"" + new string('(', 300) + "\"")), QueryValidationContext.Admin));
    }

    [Theory]
    [InlineData("\"Op\":\"Eq\",\"Value\":1800", null)]
    [InlineData("\"Op\":\"Eq\",\"Value\":2100", null)]
    [InlineData("\"Op\":\"Eq\",\"Value\":1799", "Where.Children[0].Value:OutOfRange")]
    [InlineData("\"Op\":\"Ge\",\"Value\":2101", "Where.Children[0].Value:OutOfRange")]
    [InlineData("\"Op\":\"AnyOf\",\"Values\":[1800,2100]", null)]
    [InlineData("\"Op\":\"AnyOf\",\"Values\":[1980,0]", "Where.Children[0].Values[1]:OutOfRange")]
    [InlineData("\"Op\":\"AnyOf\",\"Values\":[1980.5]", "Where.Children[0].Values[0]:InvalidValue")]
    [InlineData("\"Op\":\"Eq\",\"Value\":1980.5", "Where.Children[0].Value:InvalidValue")]
    [InlineData("\"Op\":\"Between\",\"From\":1800,\"To\":2000", null)]
    [InlineData("\"Op\":\"Between\",\"From\":1800,\"To\":2001", "Where.Children[0]:InvalidRange")]
    [InlineData("\"Op\":\"Between\",\"From\":1990,\"To\":1980", "Where.Children[0]:InvalidRange")]
    [InlineData("\"Op\":\"Between\",\"From\":1980,\"To\":1980", null)]
    [InlineData("\"Op\":\"Between\",\"From\":1980", "Where.Children[0].To:Required")]
    public void Years_Are1800To2100_WithBetweenSpansOfAtMost200(string body, string? error)
        => Assert.Equal(error is null ? [] : [error], Errors(WithChildren(Rule("\"Field\":\"Year\"," + body))));

    [Theory]
    [InlineData("\"Op\":\"Before\",\"Value\":\"1800-01-01\"", null)]
    [InlineData("\"Op\":\"After\",\"Value\":\"2100-12-31T23:59:59Z\"", null)]
    [InlineData("\"Op\":\"After\",\"Value\":\"2020-06-01T12:00:00+02:00\"", null)]
    [InlineData("\"Op\":\"Before\",\"Value\":\"1799-12-31\"", "Where.Children[0].Value:OutOfRange")]
    [InlineData("\"Op\":\"Before\",\"Value\":\"2020-06-01T12:00:00\"", "Where.Children[0].Value:InvalidValue")] // no zone
    [InlineData("\"Op\":\"Before\",\"Value\":\"June 1, 2020\"", "Where.Children[0].Value:InvalidValue")]
    [InlineData("\"Op\":\"Before\",\"Value\":20200601", "Where.Children[0].Value:InvalidValue")]
    [InlineData("\"Op\":\"Between\",\"From\":\"2020-02-01\",\"To\":\"2020-01-01\"", "Where.Children[0]:InvalidRange")]
    [InlineData("\"Op\":\"Between\",\"From\":\"2020-01-01T00:00:00Z\",\"To\":\"2020-01-01\"", null)]
    public void Dates_AreIsoInTheYears1800To2100(string body, string? error)
        => Assert.Equal(error is null ? [] : [error], Errors(WithChildren(Rule("\"Field\":\"PremiereDate\"," + body))));

    [Theory]
    [InlineData(3650, "Days", true)]
    [InlineData(3651, "Days", false)]
    [InlineData(521, "Weeks", true)]
    [InlineData(522, "Weeks", false)]
    [InlineData(120, "Months", true)]
    [InlineData(121, "Months", false)]
    [InlineData(10, "Years", true)]
    [InlineData(11, "Years", false)]
    [InlineData(1, "Days", true)]
    [InlineData(0, "Days", false)]
    [InlineData(-5, "Years", false)]
    public void InLast_IsAtMost3650DaysOrTheSame(int amount, string unit, bool valid)
    {
        foreach (var op in new[] { "InLast", "NotInLast" })
        {
            Assert.Equal(
                valid ? [] : ["Where.Children[0].Amount:OutOfRange"],
                Errors(WithChildren(Rule("\"Field\":\"PremiereDate\",\"Op\":\"" + op + "\",\"Amount\":" + amount + ",\"Unit\":\"" + unit + "\""))));
        }
    }

    [Fact]
    public void InLast_NeedsAmountAndUnit()
    {
        Assert.Equal(["Where.Children[0].Unit:Required"], Errors(WithChildren(Rule("\"Field\":\"PremiereDate\",\"Op\":\"InLast\",\"Amount\":5"))));
        Assert.Equal(["Where.Children[0].Amount:Required"], Errors(WithChildren(Rule("\"Field\":\"PremiereDate\",\"Op\":\"InLast\",\"Unit\":\"Days\""))));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Sort_HasAtMost3Keys(int count, bool valid)
    {
        var keys = new[] { "SortName", "ProductionYear", "PremiereDate", "CommunityRating" }.Take(count).Select(k => "{\"Key\":\"" + k + "\"}");
        var json = "{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[" + string.Join(",", keys) + "]}";
        Assert.Equal(valid ? [] : ["Sort:TooManySortKeys"], Errors(json));
    }

    [Fact]
    public void Sort_KeysAreKnown_AndUsedOnce()
    {
        var unknown = Validate("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[{\"Key\":\"Loudness\"}]}");
        Assert.Equal(["Sort[0].Key:UnknownSortKey"], unknown.Errors.Select(e => e.Path + ":" + e.Code));
        Assert.False(unknown.Supported); // a newer build's key, like a newer field

        Assert.Equal(["Sort[1].Key:DuplicateSortKey"], Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[{\"Key\":\"sortname\"},{\"Key\":\"SortName\",\"Order\":\"Descending\"}]}"));
        Assert.Empty(Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[{\"Key\":\"productionyear\",\"Order\":\"descending\"}]}"));
        Assert.Equal(["Sort[0].Key:Required"], Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[{\"Key\":\" \"}]}"));
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("1", true)]
    [InlineData("10000", true)]
    [InlineData("0", false)]
    [InlineData("10001", false)]
    [InlineData("-1", false)]
    public void Limit_IsNullOr1To10000(string limit, bool valid)
        => Assert.Equal(valid ? [] : ["Limit:LimitOutOfRange"], Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Limit\":" + limit + "}"));

    [Fact]
    public void ItemKinds_AreAtLeastOne_FromMSEVHB_AndTheAllowedSetting()
    {
        Assert.Equal(["Scope.ItemKinds:NoItemKinds"], Errors("{\"Scope\":{\"ItemKinds\":[]}}"));
        Assert.Equal(["Scope.ItemKinds:NoItemKinds"], Errors("{\"Scope\":{}}"));
        Assert.Empty(Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\",\"Series\",\"Episode\",\"MusicVideo\",\"Video\",\"BoxSet\"]}}"));
        Assert.Equal(["Scope.ItemKinds[1]:ItemKindNotAllowed"], Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\",\"Audio\"]}}"));
        Assert.Equal(["Scope.ItemKinds[0]:ItemKindNotAllowed"], Errors("{\"Scope\":{\"ItemKinds\":[\"Person\"]}}"));

        var noSeries = new QueryValidationContext(false, true, [BaseItemKind.Movie, BaseItemKind.Episode]);
        Assert.Equal(["Scope.ItemKinds[1]:ItemKindNotAllowed"], Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\",\"Series\"]}}", noSeries));

        // Kinds outside QueryLimits' stay refused even when the setting lists them.
        var tooWide = new QueryValidationContext(false, true, [BaseItemKind.Movie, BaseItemKind.Audio]);
        Assert.Equal(["Scope.ItemKinds[0]:ItemKindNotAllowed"], Errors("{\"Scope\":{\"ItemKinds\":[\"Audio\"]}}", tooWide));
    }

    [Fact]
    public void LibraryIds_AreNotTheEmptyId()
        => Assert.Equal(
            ["Scope.LibraryIds[1]:InvalidLibraryId"],
            Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"],\"LibraryIds\":[\"f137a2dd21bbc1b99aa5c0f6bf02a805\",\"00000000-0000-0000-0000-000000000000\"]}}"));

    // Field and operator compatibility.
    [Theory]
    [InlineData("\"Field\":\"Overview\",\"Op\":\"Eq\",\"Value\":\"x\"", "Where.Children[0].Op:OperatorNotAllowed")]
    [InlineData("\"Field\":\"Genres\",\"Op\":\"Contains\",\"Value\":\"x\"", "Where.Children[0].Op:OperatorNotAllowed")]
    [InlineData("\"Field\":\"Played\",\"Op\":\"Eq\",\"Value\":true", "Where.Children[0].Op:OperatorNotAllowed")]
    [InlineData("\"Field\":\"Title\",\"Op\":\"Between\",\"From\":\"a\",\"To\":\"b\"", "Where.Children[0].Op:OperatorNotAllowed")]
    [InlineData("\"Field\":\"SeriesStatus\",\"Op\":\"AllOf\",\"Values\":[\"Ended\"]", "Where.Children[0].Op:OperatorNotAllowed")]
    [InlineData("\"Field\":\"Title\"", "Where.Children[0].Op:Required")]
    [InlineData("\"Field\":\"Title\",\"Op\":\"Contains\"", "Where.Children[0].Value:Required")]
    [InlineData("\"Field\":\"Genres\",\"Op\":\"AnyOf\"", "Where.Children[0].Values:Required")]
    [InlineData("\"Field\":\"Genres\",\"Op\":\"AnyOf\",\"Values\":[]", "Where.Children[0].Values:Required")]
    [InlineData("\"Field\":\"Title\",\"Op\":\"IsEmpty\",\"Value\":\"x\"", "Where.Children[0].Value:UnexpectedProperty")]
    [InlineData("\"Field\":\"Title\",\"Op\":\"Eq\",\"Value\":\"x\",\"Values\":[\"y\"]", "Where.Children[0].Values:UnexpectedProperty")]
    [InlineData("\"Field\":\"Year\",\"Op\":\"Eq\",\"Value\":1990,\"From\":1980", "Where.Children[0].From:UnexpectedProperty")]
    [InlineData("\"Field\":\"PremiereDate\",\"Op\":\"Before\",\"Value\":\"2020-01-01\",\"Amount\":3", "Where.Children[0].Amount:UnexpectedProperty")]
    [InlineData("\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":5", "Where.Children[0].Value:InvalidValue")]
    [InlineData("\"Field\":\"Year\",\"Op\":\"Eq\",\"Value\":\"1990\"", "Where.Children[0].Value:InvalidValue")]
    [InlineData("\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":\"true\"", "Where.Children[0].Value:InvalidValue")]
    [InlineData("\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":1", "Where.Children[0].Value:InvalidValue")]
    [InlineData("\"Field\":\"CommunityRating\",\"Op\":\"Ge\",\"Value\":10.5", "Where.Children[0].Value:OutOfRange")]
    [InlineData("\"Field\":\"CommunityRating\",\"Op\":\"Ge\",\"Value\":7.5", null)]
    [InlineData("\"Field\":\"Genres\",\"Op\":\"AnyOf\",\"Values\":[\"Horror\",3]", "Where.Children[0].Values[1]:InvalidValue")]
    [InlineData("\"Field\":\"SeriesStatus\",\"Op\":\"AnyOf\",\"Values\":[\"ended\",\"Cancelled\"]", "Where.Children[0].Values[1]:InvalidValue")]
    [InlineData("\"Field\":\"Library\",\"Op\":\"NoneOf\",\"Values\":[\"f137a2dd21bbc1b99aa5c0f6bf02a805\",\"Movies\"]", "Where.Children[0].Values[1]:InvalidValue")]
    [InlineData("\"Field\":\"Library\",\"Op\":\"AnyOf\",\"Values\":[\"00000000000000000000000000000000\"]", "Where.Children[0].Values[0]:InvalidValue")]
    [InlineData("\"Field\":\"ItemType\",\"Op\":\"AnyOf\",\"Values\":[\"movie\",\"Series\"]", null)]
    [InlineData("\"Field\":\"ItemType\",\"Op\":\"AnyOf\",\"Values\":[\"Audio\"]", "Where.Children[0].Values[0]:ItemKindNotAllowed")]
    [InlineData("\"Field\":\"ItemType\",\"Op\":\"AnyOf\",\"Values\":[\"Film\"]", "Where.Children[0].Values[0]:InvalidValue")]
    [InlineData("\"Field\":\"ItemType\",\"Op\":\"AnyOf\",\"Values\":[\"5\"]", "Where.Children[0].Values[0]:InvalidValue")]
    [InlineData("\"Field\":\"OfficialRating\",\"Op\":\"Le\",\"Value\":\"PG-13\"", null)]
    [InlineData("\"Field\":\"OfficialRating\",\"Op\":\"Le\",\"Value\":13", "Where.Children[0].Value:InvalidValue")]
    public void FieldAndOperator_Compatibility(string body, string? error)
        => Assert.Equal(error is null ? [] : [error], Errors(WithChildren(Rule(body), "\"Movie\",\"Series\"")));

    [Theory]
    [InlineData("{\"Id\":\"3c1f00000000000000000000000000aa\",\"Name\":\"Show\"}", null)]
    [InlineData("{\"id\":\"3c1f0000-0000-0000-0000-0000000000aa\"}", null)]
    [InlineData("{\"Name\":\"Show\"}", "Where.Children[0].Values[0].Id:InvalidValue")]
    [InlineData("{\"Id\":\"nope\",\"Name\":\"Show\"}", "Where.Children[0].Values[0].Id:InvalidValue")]
    [InlineData("{\"Id\":\"3c1f00000000000000000000000000aa\",\"Name\":7}", "Where.Children[0].Values[0].Name:InvalidValue")]
    [InlineData("\"3c1f00000000000000000000000000aa\"", "Where.Children[0].Values[0]:InvalidValue")]
    [InlineData("{\"Id\":\"3c1f00000000000000000000000000aa\",\"Name\":\"Show\",\"Year\":1999}", "Where.Children[0].Values[0].Year:InvalidValue")]
    [InlineData("{\"Id\":\"3c1f00000000000000000000000000aa\",\"Name\":null}", null)]
    public void References_HaveAnIdAndAName(string reference, string? error)
        => Assert.Equal(error is null ? [] : [error], Errors(WithChildren(Rule("\"Field\":\"Series\",\"Op\":\"AnyOf\",\"Values\":[" + reference + "]"), "\"Episode\"")));

    // A definition built in code (not read) can carry a repeated name in a reference; the validator, the canonicalizer and
    // the evaluators read references one way (QueryReference), so it is refused, never read as one id here and another there.
    [Fact]
    public void AReferenceNamingIdTwice_IsInvalid_WhereverItComesFrom()
    {
        var query = Read(WithChildren(Rule("\"Field\":\"Series\",\"Op\":\"AnyOf\",\"Values\":[{\"Id\":\"3c1f00000000000000000000000000aa\"}]"), "\"Episode\""));
        query.Where.Children![0].Values = [System.Text.Json.JsonDocument.Parse("{\"Id\":\"3c1f00000000000000000000000000aa\",\"id\":\"0000000000000000000000000000000b\"}").RootElement];
        Assert.Equal(["Where.Children[0].Values[0].id:InvalidValue"], Validator.Validate(query, QueryValidationContext.User).Errors.Select(e => e.Path + ":" + e.Code));
        Assert.False(Jellyfin.Plugin.SmartSearch.Query.QueryReference.Read(query.Where.Children[0].Values![0]).Valid);
    }

    [Theory]
    [InlineData("{\"Note\":1,\"Scope\":{\"ItemKinds\":[\"Movie\"]}}", "Note:UnknownProperty")]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Movie\"],\"Libraries\":[]}}", "Scope.Libraries:UnknownProperty")]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[{\"Key\":\"SortName\",\"Nulls\":\"Last\"}]}", "Sort[0].Nulls:UnknownProperty")]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[],\"Collapsed\":true}}", "Where.Collapsed:UnknownProperty")]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[{\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"x\",\"CaseSensitive\":true}]}}", "Where.Children[0].CaseSensitive:UnknownProperty")]
    public void UnknownProperties_AreRefused_InAQueryOfThisVersion(string json, string error)
        => Assert.Equal([error], Errors(json));

    [Fact]
    public void AFieldForNoneOfTheKinds_IsFieldNotForKinds()
    {
        Assert.Equal(["Where.Children[0].Field:FieldNotForKinds"], Errors(WithChildren(Rule("\"Field\":\"SeriesStatus\",\"Op\":\"AnyOf\",\"Values\":[\"Ended\"]"))));
        Assert.Empty(Errors(WithChildren(Rule("\"Field\":\"SeriesStatus\",\"Op\":\"AnyOf\",\"Values\":[\"Ended\"]"), "\"Movie\",\"Series\"")));
        Assert.Equal(["Where.Children[0].Field:FieldNotForKinds"], Errors(WithChildren(Rule("\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":true"), "\"Series\"")));
    }

    [Fact]
    public void AdminOnlyFields_AreRefusedToUsers_AndReported()
    {
        var json = WithChildren(Rule("\"Field\":\"Path\",\"Op\":\"Contains\",\"Value\":\"/mnt\""));
        var user = Validate(json, QueryValidationContext.User);
        Assert.Equal(["Where.Children[0].Field:AdminOnlyField"], user.Errors.Select(e => e.Path + ":" + e.Code));
        Assert.True(user.UsesAdminOnlyFields);
        Assert.True(user.Supported);

        var admin = Validate(json, QueryValidationContext.Admin);
        Assert.Empty(admin.Errors);
        Assert.True(admin.UsesAdminOnlyFields); // a query that uses it can't be shared
    }

    [Fact]
    public void UnknownFields_AreUnsupported_NotJustInvalid()
    {
        var result = Validate(WithChildren(Rule("\"Field\":\"Loudness\",\"Op\":\"Ge\",\"Value\":3")));
        Assert.Equal(["Where.Children[0].Field:UnknownField"], result.Errors.Select(e => e.Path + ":" + e.Code));
        Assert.False(result.Valid);
        Assert.False(result.Supported);
        Assert.False(result.Runnable);
    }

    [Fact]
    public void Aliases_AndAnyCase_FindTheField()
    {
        Assert.Empty(Errors(WithChildren(Rule("\"Field\":\"Name\",\"Op\":\"Contains\",\"Value\":\"x\""))));
        Assert.Empty(Errors(WithChildren(Rule("\"Field\":\"genres\",\"Op\":\"AnyOf\",\"Values\":[\"x\"]"))));
        Assert.Empty(Errors(WithChildren(Rule("\"Field\":\" Title \",\"Op\":\"Contains\",\"Value\":\"x\""))));
    }

    [Fact]
    public void UnavailableFields_SaveButDontRun()
    {
        var result = Validate(WithChildren(Rule("\"Field\":\"MemberRating\",\"Op\":\"Ge\",\"Value\":4")));
        Assert.True(result.Valid);
        Assert.True(result.Supported);
        Assert.False(result.Runnable);
        Assert.Equal(["MemberRating"], result.UnavailableFields);
    }

    [Theory]
    [InlineData("\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"x\",\"IncludeOriginalTitle\":true", null)]
    [InlineData("\"Field\":\"Genres\",\"Op\":\"AnyOf\",\"Values\":[\"x\"],\"IncludeOriginalTitle\":false", "Where.Children[0].IncludeOriginalTitle:OptionNotAllowed")]
    [InlineData("\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"x\",\"Role\":\"Director\"", "Where.Children[0].Role:OptionNotAllowed")]
    [InlineData("\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"x\",\"Provider\":\"\"", "Where.Children[0].Provider:OptionNotAllowed")]
    [InlineData("\"Field\":\"HasProviderId\",\"Op\":\"Is\",\"Value\":true,\"Provider\":\"imdb\"", null)]
    [InlineData("\"Field\":\"HasProviderId\",\"Op\":\"Is\",\"Value\":true", "Where.Children[0].Provider:MissingOption")]
    [InlineData("\"Field\":\"HasProviderId\",\"Op\":\"Is\",\"Value\":true,\"Provider\":\"\"", "Where.Children[0].Provider:MissingOption")]
    [InlineData("\"Field\":\"HasProviderId\",\"Op\":\"Is\",\"Value\":true,\"Provider\":\"Anidb\"", "Where.Children[0].Provider:InvalidOption")]
    public void Options_AreTheFields(string body, string? error)
        => Assert.Equal(error is null ? [] : [error], Errors(WithChildren(Rule(body))));

    // Structure: a node is a group or a rule.
    [Theory]
    [InlineData("{\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"x\",\"Children\":[{\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":true}]}", "Where.Children[0]:GroupAndRule")]
    [InlineData("{\"Op\":\"Contains\",\"Value\":\"x\",\"Children\":[{\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":true}]}", "Where.Children[0].Op|Where.Children[0].Value:UnexpectedProperty")]
    [InlineData("{}", "Where.Children[0]:EmptyNode")]
    [InlineData("{\"Match\":\"Any\"}", "Where.Children[0]:EmptyNode")]
    [InlineData("null", "Where.Children[0]:EmptyNode")]
    [InlineData("{\"Op\":\"Contains\",\"Value\":\"x\"}", "Where.Children[0].Field:Required")]
    [InlineData("{\"Field\":\"\",\"Op\":\"Contains\",\"Value\":\"x\"}", "Where.Children[0].Field:Required")]
    [InlineData("{\"Children\":[]}", "Where.Children[0]:EmptyGroup")]
    [InlineData("{\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":true,\"Negate\":true}", "Where.Children[0].Negate:UnexpectedProperty")]
    [InlineData("{\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":true,\"Match\":\"Any\"}", "Where.Children[0].Match:UnexpectedProperty")]
    public void Structure(string node, string error)
    {
        var expected = error.Split(':') is [var paths, var code] ? paths.Split('|').Select(p => p + ":" + code).ToList() : [];
        Assert.Equal(expected, Errors(WithChildren(node)));
    }

    [Fact]
    public void TheRoot_IsAGroup_AndTheQueryHasAScopeAndATree()
    {
        Assert.Equal(["Where:RootNotGroup"], Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":true}}"));
        Assert.Equal(["Where:Required"], Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":null}"));
        Assert.Equal(["Scope:Required"], Errors("{\"Scope\":null}"));
        Assert.Equal(["Scope.ItemKinds:Required"], Errors("{\"Scope\":{\"ItemKinds\":null}}"));
        Assert.Equal(["Sort:Required"], Errors("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":null}"));
    }

    [Fact]
    public void ErrorsAreCapped_At50()
    {
        var json = WithChildren(string.Join(",", Enumerable.Repeat("{}", 60)));
        Assert.Equal(50, Validate(json).Errors.Count);
    }

    [Fact]
    public void TheVersion_IsTheCurrentOne()
    {
        var query = Read("{\"Scope\":{\"ItemKinds\":[\"Movie\"]}}");
        query.Version = 2;
        Assert.Equal(["Version:UnsupportedVersion"], Validator.Validate(query, QueryValidationContext.User).Errors.Select(e => e.Path + ":" + e.Code));
        query.Version = 0;
        Assert.Equal(["Version:InvalidVersion"], Validator.Validate(query, QueryValidationContext.User).Errors.Select(e => e.Path + ":" + e.Code));
    }
}
