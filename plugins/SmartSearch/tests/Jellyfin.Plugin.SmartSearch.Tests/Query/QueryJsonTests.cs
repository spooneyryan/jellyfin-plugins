using System.Linq;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Migration;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Query;

/// <summary>
/// The query's JSON: PascalCase, enums as names, the 64 KB limit, and unknown names sorted into
/// unsupported (a newer query's operator) or invalid.
/// </summary>
public sealed class QueryJsonTests
{
    private static string[] Codes(QueryReadResult read) => read.Errors.Select(e => e.Path + ":" + e.Code).ToArray();

    private static string TitleQuery(string value)
        => "{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[{\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"" + value + "\"}]}}";

    // A Title query whose written form is exactly the limit: as many characters of the given written size as fit, then
    // ASCII. (Over 200 characters, so not valid; the limit is the reader's, before validation.)
    private static string WrittenAtTheLimit(string character, int writtenBytes)
    {
        var empty = Encoding.UTF8.GetByteCount(QueryJson.Write(TestCatalog.Read(TitleQuery(string.Empty))));
        var room = QueryLimits.MaxBytes - empty;
        var value = string.Concat(Enumerable.Repeat(character, room / writtenBytes)) + new string('a', room % writtenBytes);
        var read = QueryJson.Read(TitleQuery(value));
        Assert.Empty(read.Errors);
        return QueryJson.Write(read.Definition!);
    }

    [Fact]
    public void AFullExample_ReadsIntoTheModel()
    {
        var query = TestCatalog.Read("""
            {
              "Version": 1,
              "Scope": { "ItemKinds": ["Movie"], "LibraryIds": ["f137a2dd21bbc1b99aa5c0f6bf02a805"], "IncludeMissing": false },
              "Where": { "Match": "All", "Negate": false, "Children": [
                  { "Field": "Genres", "Op": "AnyOf", "Values": ["Horror", "Thriller"] },
                  { "Match": "Any", "Children": [ { "Field": "CommunityRating", "Op": "Ge", "Value": 7 } ] },
                  { "Field": "People", "Op": "AnyOf", "Values": [ { "Id": "3c1f00000000000000000000000000aa", "Name": "John Carpenter" } ], "Role": "Director" },
                  { "Field": "DateAdded", "Op": "InLast", "Amount": 90, "Unit": "Days" } ] },
              "Sort": [ { "Key": "PremiereDate", "Order": "Descending" } ],
              "Limit": null
            }
            """);
        Assert.Equal(1, query.Version);
        Assert.Equal([BaseItemKind.Movie], query.Scope.ItemKinds);
        Assert.Equal("f137a2dd21bbc1b99aa5c0f6bf02a805", query.Scope.LibraryIds.Single().ToString("N"));
        Assert.True(query.Where.IsGroup);
        Assert.Equal(SmartSearchMatchMode.All, query.Where.Match);
        var children = query.Where.Children!;
        Assert.Equal(4, children.Count);
        Assert.Equal(("Genres", SmartSearchQueryOperator.AnyOf, 2), (children[0].Field, children[0].Op, children[0].Values!.Count));
        Assert.Equal(SmartSearchMatchMode.Any, children[1].Match);
        Assert.Equal(7, children[1].Children![0].Value!.Value.GetInt32());
        Assert.Equal("Director", children[2].Role);
        Assert.Equal((90, SmartSearchRelativeUnit.Days), (children[3].Amount, children[3].Unit));
        Assert.Equal(("PremiereDate", SortOrder.Descending), (query.Sort[0].Key, query.Sort[0].Order));
        Assert.Null(query.Limit);
    }

    [Fact]
    public void Defaults_AreAQueryWithAnEmptyAllRoot()
    {
        var query = TestCatalog.Read("{}");
        Assert.Equal(1, query.Version);
        Assert.Empty(query.Scope.ItemKinds);
        Assert.Equal(SmartSearchMatchMode.All, query.Where.Match);
        Assert.Empty(query.Where.Children!);
        Assert.Empty(query.Sort);
        Assert.Equal("{\"Version\":1,\"Scope\":{\"ItemKinds\":[],\"LibraryIds\":[],\"IncludeMissing\":false},\"Where\":{\"Match\":\"All\",\"Negate\":false,\"Children\":[]},\"Sort\":[]}", QueryJson.Write(query));
    }

    [Fact]
    public void Names_MatchIgnoringCase_AndAreWrittenInTheirOwnSpelling()
    {
        var query = TestCatalog.Read("{\"scope\":{\"itemkinds\":[\"movie\"]},\"where\":{\"match\":\"any\",\"children\":[{\"field\":\"Title\",\"op\":\"contains\",\"value\":\"x\"}]},\"sort\":[{\"key\":\"SortName\",\"order\":\"descending\"}]}");
        Assert.Equal(SmartSearchMatchMode.Any, query.Where.Match);
        Assert.Equal(SmartSearchQueryOperator.Contains, query.Where.Children![0].Op);
        var written = QueryJson.Write(query);
        Assert.Contains("\"ItemKinds\":[\"Movie\"]", written, System.StringComparison.Ordinal);
        Assert.Contains("\"Match\":\"Any\"", written, System.StringComparison.Ordinal);
        Assert.Contains("\"Op\":\"Contains\"", written, System.StringComparison.Ordinal);
        Assert.Contains("\"Order\":\"Descending\"", written, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheSizeLimit_Is64KiBOfUtf8()
    {
        // Whitespace counts: the limit is on the bytes received.
        string Padded(int bytes) => "{" + new string(' ', bytes - 2) + "}";

        Assert.Empty(QueryJson.Read(Padded(QueryLimits.MaxBytes)).Errors);
        Assert.Equal([":TooLarge"], Codes(QueryJson.Read(Padded(QueryLimits.MaxBytes + 1))));

        // Bytes, not characters: 'é' is two bytes in UTF-8.
        var accented = "{\"Pad\":\"" + new string('é', 32_770) + "\"}";
        Assert.True(accented.Length < QueryLimits.MaxBytes);
        Assert.Equal([":TooLarge"], Codes(QueryJson.Read(accented)));
        Assert.Equal([":TooLarge"], Codes(QueryJson.Read(Encoding.UTF8.GetBytes(accented))));
    }

    [Theory]
    [InlineData("", ":InvalidJson")]
    [InlineData("{", ":InvalidJson")]
    [InlineData("{\"Version\":1,}", ":InvalidJson")]
    [InlineData("{\"Version\":1,\"version\":1}", ":InvalidJson")] // a repeated name, ignoring case
    [InlineData("[]", ":NotAnObject")]
    [InlineData("null", ":NotAnObject")]
    [InlineData("\"query\"", ":NotAnObject")]
    [InlineData("{\"Limit\":\"10\"}", "Limit:InvalidJson")]
    [InlineData("{\"Where\":{\"Children\":[{\"Negate\":\"yes\"}]}}", "Where.Children[0].Negate:InvalidJson")]
    [InlineData("{\"Where\":{\"Children\":[{\"Op\":3}]}}", "Where.Children[0].Op:InvalidJson")] // enums are names, never numbers
    [InlineData("{\"Scope\":{\"LibraryIds\":[\"Movies\"]}}", "Scope.LibraryIds[0]:InvalidJson")]
    [InlineData("{\"Where\":{\"Children\":[{\"Amount\":1.5}]}}", "Where.Children[0].Amount:InvalidJson")]
    public void Malformed_IsInvalid_WithAPath(string json, string expected)
    {
        var read = QueryJson.Read(json);
        Assert.Null(read.Definition);
        Assert.True(read.Supported);
        Assert.Equal([expected], Codes(read));
    }

    [Theory]
    [InlineData("{\"Where\":{\"Children\":[{\"Field\":\"Title\",\"Op\":\"SoundsLike\",\"Value\":\"x\"}]}}", "Where.Children[0].Op")]
    [InlineData("{\"Where\":{\"Match\":\"Exactly\",\"Children\":[]}}", "Where.Match")]
    [InlineData("{\"Where\":{\"Children\":[{\"Children\":[{\"Field\":\"DateAdded\",\"Op\":\"InLast\",\"Amount\":3,\"Unit\":\"Hours\"}]}]}}", "Where.Children[0].Children[0].Unit")]
    public void ANewerOperator_IsUnsupported(string json, string path)
    {
        var read = QueryJson.Read(json);
        Assert.Null(read.Definition);
        Assert.False(read.Supported);
        Assert.Equal([path + ":UnknownOperator"], Codes(read));
    }

    [Theory]
    [InlineData("{\"Scope\":{\"ItemKinds\":[\"Film\"]}}", "Scope.ItemKinds[0]:InvalidValue")]
    [InlineData("{\"Sort\":[{\"Key\":\"SortName\",\"Order\":\"Up\"}]}", "Sort[0].Order:InvalidValue")]
    [InlineData("{\"Where\":{\"Children\":[{\"Op\":\"Any, All\"}]}}", "Where.Children[0].Op:InvalidValue")]
    [InlineData("{\"Where\":{\"Children\":[{\"Op\":\"1\"}]}}", "Where.Children[0].Op:InvalidValue")]
    public void ANameThatIsNoneOfTheValues_IsInvalid(string json, string expected)
    {
        var read = QueryJson.Read(json);
        Assert.Null(read.Definition);
        Assert.True(read.Supported);
        Assert.Equal([expected], Codes(read));
    }

    // With the default encoder a query in Chinese was written as six bytes a character, so one
    // under the limit could not be read back once saved. Text is now written as it is.
    [Fact]
    public void AQueryAtTheLimit_InChinese_IsWrittenAsItIs_AndReadsBack()
    {
        var written = WrittenAtTheLimit("漢", 3);
        Assert.Contains("漢漢漢", written, System.StringComparison.Ordinal);
        Assert.Equal(QueryLimits.MaxBytes, Encoding.UTF8.GetByteCount(written));
        var again = QueryJson.Read(written);
        Assert.Empty(again.Errors);
        Assert.Equal(written, QueryJson.Write(again.Definition!));
    }

    // The encoder writes a character outside the BMP as two \u escapes (12 bytes for an emoji's 4), so the limit is
    // also checked on the written form: a query at the limit as written reads back, and one over it is refused when
    // it arrives, not after it was saved.
    [Fact]
    public void AnEmojiQuery_IsCheckedAsWritten_AtTheLimitItReadsBack_OverItIsTooLarge()
    {
        var written = WrittenAtTheLimit("😀", 12);
        Assert.Equal(QueryLimits.MaxBytes, Encoding.UTF8.GetByteCount(written));
        var again = QueryJson.Read(written);
        Assert.Empty(again.Errors);
        Assert.Equal(written, QueryJson.Write(again.Definition!));

        var over = TitleQuery(string.Concat(Enumerable.Repeat("😀", (QueryLimits.MaxBytes / 12) + 1)));
        Assert.True(Encoding.UTF8.GetByteCount(over) < QueryLimits.MaxBytes / 2);
        Assert.Equal([":TooLarge"], Codes(QueryJson.Read(over)));
    }

    // JsonNode notices a repeated name (ignoring case) only when it builds that object; Read builds them all, so one in
    // a reference inside Values is refused too (duplicate keys in a reference once passed).
    [Theory]
    [InlineData("""{"Scope":{"ItemKinds":["Episode"]},"Where":{"Children":[{"Field":"Series","Op":"AnyOf","Values":[{"Id":"3c1f00000000000000000000000000aa","id":"0000000000000000000000000000000b"}]}]}}""")]
    [InlineData("""{"Scope":{"ItemKinds":["Episode"]},"Where":{"Children":[{"Field":"Series","Op":"AnyOf","Values":[{"Name":"a","NAME":"b","Id":"3c1f00000000000000000000000000aa"}]}]}}""")]
    [InlineData("""{"Scope":{"ItemKinds":["Movie"]},"Where":{"Children":[{"Field":"Title","field":"Overview","Op":"Contains","Value":"x"}]}}""")]
    [InlineData("""{"Scope":{"ItemKinds":["Movie"],"itemKinds":["Series"]}}""")]
    [InlineData("""{"Scope":{"ItemKinds":["Movie"]},"Where":{"Children":[{"Field":"Title","Op":"Contains","Value":{"a":1,"A":2}}]}}""")]
    public void ARepeatedName_Anywhere_IsInvalidJson(string json)
        => Assert.Equal([":InvalidJson"], Codes(QueryJson.Read(json)));

    [Fact]
    public void AByteOrderMark_IsSkipped()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"Limit\":5}")).ToArray();
        Assert.Equal(5, QueryJson.Read(bytes).Definition!.Limit);
    }

    [Fact]
    public void ReadsReport_WhatTheMigratorDid()
    {
        Assert.Equal(QueryMigrationStatus.UpToDate, QueryJson.Read("{}").Migration);
        Assert.Equal(QueryMigrationStatus.UpToDate, QueryJson.Read("{\"Version\":1}").Migration);
        Assert.Null(QueryJson.Read("[]").Migration);
    }
}
