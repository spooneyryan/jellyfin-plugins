using System;
using System.Linq;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.Query.TestCatalog;

namespace Jellyfin.Plugin.SmartSearch.Tests.Query;

/// <summary>
/// Equal hashes for equivalent JSON (value order, whitespace, defaults), different hashes for
/// any semantic change; and the canonical text itself, pinned.
/// </summary>
public sealed class QueryCanonicalizerTests
{
    private const string Base = """
        {
          "Version": 1,
          "Scope": { "ItemKinds": ["Movie", "Series"], "LibraryIds": ["f137a2dd21bbc1b99aa5c0f6bf02a805"] },
          "Where": { "Children": [
              { "Field": "Genres", "Op": "AnyOf", "Values": ["Horror", "Thriller"] },
              { "Field": "Year", "Op": "Between", "From": 1980, "To": 1989 },
              { "Match": "Any", "Children": [
                  { "Field": "CommunityRating", "Op": "Ge", "Value": 7 },
                  { "Field": "Title", "Op": "Contains", "Value": "night" } ] },
              { "Field": "PremiereDate", "Op": "After", "Value": "2001-02-03" },
              { "Field": "Played", "Op": "Is", "Value": false } ] },
          "Sort": [ { "Key": "PremiereDate", "Order": "Descending" } ]
        }
        """;

    private static string Edit(string from, string to)
    {
        Assert.Contains(from, Base, StringComparison.Ordinal);
        return Base.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCanonicalText_IsPinned()
    {
        var canonical = Canonicalizer.Canonicalize(Read(Base));
        Assert.Equal(
            "{\"Version\":1,\"Scope\":{\"ItemKinds\":[\"Movie\",\"Series\"],\"LibraryIds\":[\"f137a2dd21bbc1b99aa5c0f6bf02a805\"]},"
            + "\"Where\":{\"Children\":["
            + "{\"Field\":\"Genres\",\"Op\":\"AnyOf\",\"Values\":[\"Horror\",\"Thriller\"]},"
            + "{\"Field\":\"Played\",\"Op\":\"Is\",\"Value\":false},"
            + "{\"Field\":\"PremiereDate\",\"Op\":\"After\",\"Value\":\"2001-02-03\"},"
            + "{\"Field\":\"Year\",\"Op\":\"Between\",\"From\":1980,\"To\":1989},"
            + "{\"Match\":\"Any\",\"Children\":[{\"Field\":\"CommunityRating\",\"Op\":\"Ge\",\"Value\":7},{\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"night\"}]}"
            + "]},\"Sort\":[{\"Key\":\"PremiereDate\",\"Order\":\"Descending\"}]}",
            canonical.Json);
        Assert.Matches("^sha256:[0-9a-f]{64}$", canonical.Hash);
        Assert.Equal(canonical, Canonicalizer.Canonicalize(Read(canonical.Json))); // a fixed point
    }

    [Fact]
    public void AnEmptyQuery_HasTheShortestForm()
        => Assert.Equal("{\"Version\":1,\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[]}}", Canonical("{\"Scope\":{\"ItemKinds\":[\"Movie\"]}}"));

    // Equivalent JSON: the same hash.
    [Theory]
    [InlineData("whitespace", "\n", "")]
    [InlineData("value order", "[\"Horror\", \"Thriller\"]", "[\"Thriller\", \"Horror\"]")]
    [InlineData("repeated values", "[\"Horror\", \"Thriller\"]", "[\"Horror\", \"Thriller\", \"Horror\"]")]
    [InlineData("padded values", "[\"Horror\", \"Thriller\"]", "[\" Horror\", \"Thriller  \"]")]
    [InlineData("padded text", "\"night\"", "\"  night \"")]
    [InlineData("kind order", "[\"Movie\", \"Series\"]", "[\"Series\", \"Movie\"]")]
    [InlineData("repeated kinds", "[\"Movie\", \"Series\"]", "[\"Series\", \"Movie\", \"Series\"]")]
    [InlineData("kind spelling", "[\"Movie\", \"Series\"]", "[\"movie\", \"SERIES\"]")]
    [InlineData("library id format", "\"f137a2dd21bbc1b99aa5c0f6bf02a805\"", "\"F137A2DD-21BB-C1B9-9AA5-C0F6BF02A805\"")]
    [InlineData("IncludeMissing false", "\"LibraryIds\"", "\"IncludeMissing\": false, \"LibraryIds\"")]
    [InlineData("Match All", "\"Where\": { \"Children\"", "\"Where\": { \"Match\": \"All\", \"Negate\": false, \"Children\"")]
    [InlineData("Negate false on a nested group", "{ \"Match\": \"Any\", \"Children\"", "{ \"Match\": \"Any\", \"Negate\": false, \"Children\"")]
    [InlineData("IncludeOriginalTitle false", "\"Value\": \"night\"", "\"Value\": \"night\", \"IncludeOriginalTitle\": false")]
    [InlineData("an alias", "\"Field\": \"Title\"", "\"Field\": \"Name\"")]
    [InlineData("field and operator spelling", "\"Field\": \"Genres\", \"Op\": \"AnyOf\"", "\"field\": \"genres\", \"op\": \"anyof\"")]
    [InlineData("a whole number written as a decimal", "\"Value\": 7", "\"Value\": 7.0")]
    [InlineData("an exponent", "\"From\": 1980", "\"From\": 1.98e3")]
    [InlineData("an explicit Version", "\"Version\": 1,", "")]
    [InlineData("Limit null", "\"Sort\"", "\"Limit\": null, \"Sort\"")]
    [InlineData("unknown properties", "\"Version\": 1,", "\"Version\": 1, \"Note\": \"mine\",")]
    public void Equivalent_HasTheSameHash(string what, string from, string to)
    {
        Assert.NotNull(what);
        Assert.Equal(Hash(Base), Hash(Edit(from, to)));
    }

    [Fact]
    public void ChildOrder_DoesNotChangeTheHash()
    {
        var query = Read(Base);
        query.Where.Children!.Reverse();
        query.Where.Children[2].Children!.Reverse(); // the Any group, in the middle
        Assert.Equal(Hash(Base), Canonicalizer.Canonicalize(query).Hash);
    }

    [Fact]
    public void Ascending_IsTheDefaultOrder_OfAnyKey()
        => Assert.Equal(
            Hash("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[{\"Key\":\"PremiereDate\"}]}"),
            Hash("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[{\"Key\":\"premieredate\",\"Order\":\"Ascending\"}]}"));

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"Key\":\"SortName\"}]")]
    [InlineData("[{\"Key\":\"sortname\",\"Order\":\"Ascending\"}]")]
    public void TheDefaultSort_IsLeftOut(string sort)
        => Assert.Equal(Hash("{\"Scope\":{\"ItemKinds\":[\"Movie\"]}}"), Hash("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":" + sort + "}"));

    [Fact]
    public void ValuesOfEachKind_AreCanonical()
    {
        Assert.Contains(
            "\"Values\":[1980,1999,2004]",
            Canonical(WithChildren("{\"Field\":\"Year\",\"Op\":\"AnyOf\",\"Values\":[2004,1980.0,1999,1980]}")),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Values\":[\"Ended\",\"Unreleased\"]",
            Canonical(WithChildren("{\"Field\":\"SeriesStatus\",\"Op\":\"NoneOf\",\"Values\":[\"unreleased\",\"ENDED\",\"Ended\"]}", "\"Series\"")),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Values\":[\"Episode\",\"Movie\"]",
            Canonical(WithChildren("{\"Field\":\"ItemType\",\"Op\":\"AnyOf\",\"Values\":[\"movie\",\"Episode\",\"Movie\"]}")),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Values\":[{\"Id\":\"0000000000000000000000000000000a\",\"Name\":\"A show\"},{\"Id\":\"3c1f00000000000000000000000000aa\",\"Name\":\"Another\"}]",
            Canonical(WithChildren("{\"Field\":\"Series\",\"Op\":\"AnyOf\",\"Values\":[{\"name\":\" Another \",\"id\":\"3C1F0000-0000-0000-0000-0000000000AA\"},{\"Id\":\"0000000000000000000000000000000a\",\"Name\":\"A show\"},{\"Id\":\"3c1f00000000000000000000000000aa\",\"Name\":\"Another\"}]}", "\"Episode\"")),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Value\":\"2001-02-03T04:05:06.5Z\"",
            Canonical(WithChildren("{\"Field\":\"PremiereDate\",\"Op\":\"Before\",\"Value\":\"2001-02-03T06:05:06.500+02:00\"}")),
            StringComparison.Ordinal);
        Assert.Contains("\"Value\":7.25", Canonical(WithChildren("{\"Field\":\"CommunityRating\",\"Op\":\"Ge\",\"Value\":7.250}")), StringComparison.Ordinal);
        Assert.Contains("\"Value\":0}", Canonical(WithChildren("{\"Field\":\"CommunityRating\",\"Op\":\"Ge\",\"Value\":-0.0}")), StringComparison.Ordinal);
        Assert.Contains("\"Provider\":\"Imdb\"", Canonical(WithChildren("{\"Field\":\"HasProviderId\",\"Op\":\"Is\",\"Value\":true,\"Provider\":\" imdb\"}")), StringComparison.Ordinal);
    }

    [Fact]
    public void MatchesPatterns_KeepTheirSpaces()
        => Assert.NotEqual(
            Hash(WithChildren("{\"Field\":\"Path\",\"Op\":\"Matches\",\"Value\":\"a b\"}")),
            Hash(WithChildren("{\"Field\":\"Path\",\"Op\":\"Matches\",\"Value\":\" a b \"}")));

    // Any semantic change: a different hash.
    [Theory]
    [InlineData("\"AnyOf\", \"Values\": [\"Horror\"", "\"AllOf\", \"Values\": [\"Horror\"")]
    [InlineData("[\"Horror\", \"Thriller\"]", "[\"Horror\"]")]
    [InlineData("\"To\": 1989", "\"To\": 1990")]
    [InlineData("\"Value\": 7", "\"Value\": 7.5")]
    [InlineData("\"Op\": \"Ge\"", "\"Op\": \"Gt\"")]
    [InlineData("\"Field\": \"CommunityRating\"", "\"Field\": \"Year\"")]
    [InlineData("\"Value\": \"night\"", "\"Value\": \"nights\"")]
    [InlineData("\"Value\": \"night\"", "\"Value\": \"night\", \"IncludeOriginalTitle\": true")]
    [InlineData("\"Value\": false }", "\"Value\": true }")]
    [InlineData("\"2001-02-03\"", "\"2001-02-04\"")]
    [InlineData("\"2001-02-03\"", "\"2001-02-03T00:00:01Z\"")]
    [InlineData("\"2001-02-03\"", "\"2001-02-03T00:00:00Z\"")]
    [InlineData("\"2001-02-03\"", "\"2001-02-03T01:00:00+01:00\"")]
    [InlineData("{ \"Match\": \"Any\", \"Children\"", "{ \"Match\": \"All\", \"Children\"")]
    [InlineData("{ \"Match\": \"Any\", \"Children\"", "{ \"Match\": \"Any\", \"Negate\": true, \"Children\"")]
    [InlineData("\"Where\": { \"Children\"", "\"Where\": { \"Match\": \"Any\", \"Children\"")]
    [InlineData("\"Where\": { \"Children\"", "\"Where\": { \"Negate\": true, \"Children\"")]
    [InlineData("[\"Movie\", \"Series\"]", "[\"Movie\"]")]
    [InlineData("\"LibraryIds\": [\"f137a2dd21bbc1b99aa5c0f6bf02a805\"]", "\"LibraryIds\": []")]
    [InlineData("\"LibraryIds\"", "\"IncludeMissing\": true, \"LibraryIds\"")]
    [InlineData("\"Order\": \"Descending\"", "\"Order\": \"Ascending\"")]
    [InlineData("\"Key\": \"PremiereDate\"", "\"Key\": \"ProductionYear\"")]
    [InlineData("\"Order\": \"Descending\" }", "\"Order\": \"Descending\" }, { \"Key\": \"CommunityRating\" }")]
    [InlineData("\"Sort\"", "\"Limit\": 10, \"Sort\"")]
    [InlineData("{ \"Field\": \"Played\", \"Op\": \"Is\", \"Value\": false }", "{ \"Children\": [ { \"Field\": \"Played\", \"Op\": \"Is\", \"Value\": false } ], \"Match\": \"Any\", \"Negate\": true }")]
    public void ASemanticChange_HasADifferentHash(string from, string to)
        => Assert.NotEqual(Hash(Base), Hash(Edit(from, to)));

    // A day and an instant mean different things, so midnight UTC stays a time; instants
    // written with different offsets are the same instant.
    [Fact]
    public void Instants_AreWrittenInUtc_EvenAtMidnight()
    {
        var utc = Canonical(WithChildren("{\"Field\":\"PremiereDate\",\"Op\":\"Before\",\"Value\":\"2001-02-03T00:00:00Z\"}"));
        Assert.Contains("\"Value\":\"2001-02-03T00:00:00Z\"", utc, StringComparison.Ordinal);
        Assert.Equal(utc, Canonical(WithChildren("{\"Field\":\"PremiereDate\",\"Op\":\"Before\",\"Value\":\"2001-02-03T01:00:00+01:00\"}")));
        Assert.Contains("\"Value\":\"2001-02-03\"", Canonical(WithChildren("{\"Field\":\"PremiereDate\",\"Op\":\"Before\",\"Value\":\"2001-02-03\"}")), StringComparison.Ordinal);
    }

    [Fact]
    public void References_AreSortedById_NotByName()
    {
        var zuluFirst = Canonical(WithChildren("{\"Field\":\"Series\",\"Op\":\"AnyOf\",\"Values\":[{\"Id\":\"ff000000000000000000000000000001\",\"Name\":\"Alpha\"},{\"Id\":\"00000000000000000000000000000002\",\"Name\":\"Zulu\"}]}", "\"Episode\""));
        Assert.Contains("\"Values\":[{\"Id\":\"00000000000000000000000000000002\",\"Name\":\"Zulu\"},{\"Id\":\"ff000000000000000000000000000001\",\"Name\":\"Alpha\"}]", zuluFirst, StringComparison.Ordinal);
        Assert.Equal(zuluFirst, Canonical(WithChildren("{\"Field\":\"Series\",\"Op\":\"AnyOf\",\"Values\":[{\"Id\":\"00000000000000000000000000000002\",\"Name\":\"Zulu\"},{\"Id\":\"ff000000000000000000000000000001\",\"Name\":\"Alpha\"}]}", "\"Episode\"")));
    }

    [Fact]
    public void SortKeyOrder_IsSemantic()
        => Assert.NotEqual(
            Hash("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[{\"Key\":\"ProductionYear\"},{\"Key\":\"CommunityRating\"}]}"),
            Hash("{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Sort\":[{\"Key\":\"CommunityRating\"},{\"Key\":\"ProductionYear\"}]}"));

    [Fact]
    public void TheHash_IsSha256OfTheCanonicalUtf8()
    {
        var canonical = Canonicalizer.Canonicalize(Read(WithChildren("{\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"Amélie\"}")));
        var expected = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical.Json)));
        Assert.Equal(expected, canonical.Hash);
        Assert.Equal(64 + "sha256:".Length, canonical.Hash.Length);
        Assert.Single(new[] { canonical.Hash, Canonicalizer.Canonicalize(Read(WithChildren("{\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"Amélie\"}"))).Hash }.Distinct());
    }
}
