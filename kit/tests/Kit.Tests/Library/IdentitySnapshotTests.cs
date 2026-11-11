using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="IdentitySnapshot"/> in the kit's store format: every field round-trips, nulls are
/// omitted, provider-id maps are normalised and compared by content, and fields of a newer kit survive.
/// </summary>
public sealed class IdentitySnapshotTests
{
    private static readonly Guid ItemId = Guid.Parse("0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0");

    [Fact]
    public void EveryField_RoundTrips_InTheStoreFormat()
    {
        var snapshot = Episode();

        var json = JsonSerializer.Serialize(snapshot, JsonStoreFormat.Options);
        var back = JsonSerializer.Deserialize<IdentitySnapshot>(json, JsonStoreFormat.Options)!;

        Assert.Equal(snapshot, back);
        Assert.Equal("81189", back.SeriesProviderIds!["Tvdb"]);
        Assert.Equal(3, back.Season);
        Assert.Equal(7, back.Episode);
        Assert.Equal(TimeSpan.FromMinutes(47).Ticks, back.RunTimeTicks);

        var node = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(
            new[] { "ItemId", "Kind", "Name", "Year", "ProviderIds", "SeriesProviderIds", "SeriesName", "Season", "Episode", "SeriesId", "SeasonId", "RunTimeTicks", "Path" },
            node.Select(p => p.Key));
    }

    [Fact]
    public void NullFields_AreOmitted_AndAMissingMapReadsAsEmpty()
    {
        var snapshot = new IdentitySnapshot { ItemId = ItemId, Kind = "Movie", Name = "Heat" };

        var json = JsonSerializer.Serialize(snapshot, JsonStoreFormat.Options);

        var node = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(new[] { "ItemId", "Kind", "Name", "ProviderIds" }, node.Select(p => p.Key));
        Assert.Empty(node["ProviderIds"]!.AsObject());

        var bare = JsonSerializer.Deserialize<IdentitySnapshot>("""{"ItemId":"0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0"}""", JsonStoreFormat.Options)!;
        Assert.NotNull(bare.ProviderIds);
        Assert.Empty(bare.ProviderIds);
        Assert.Null(bare.SeriesProviderIds);
        Assert.Equal(string.Empty, bare.Kind);
        Assert.Equal(string.Empty, bare.Name);

        var nulls = JsonSerializer.Deserialize<IdentitySnapshot>("""{"Kind":"Movie","ProviderIds":null,"SeriesProviderIds":null}""", JsonStoreFormat.Options)!;
        Assert.Empty(nulls.ProviderIds);
        Assert.Null(nulls.SeriesProviderIds);
    }

    [Fact]
    public void TheShapeOfBookmarksMigration_Deserializes()
    {
        // Bookmarks' upgrade builds { ItemId, Kind, Name, ProviderIds } from a 1.0 record.
        const string Json = """{"ItemId":"0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0","Kind":"Video","Name":"Heat (1995)","ProviderIds":{"Tmdb":"949","Imdb":"tt0113277"}}""";

        var snapshot = JsonSerializer.Deserialize<IdentitySnapshot>(Json, JsonStoreFormat.Options)!;

        Assert.Equal(ItemId, snapshot.ItemId);
        Assert.Equal("Video", snapshot.Kind);
        Assert.Equal("949", snapshot.ProviderIds["tmdb"]);
        Assert.Null(snapshot.Path);
    }

    [Fact]
    public void ProviderIds_AreNormalised()
    {
        var snapshot = new IdentitySnapshot
        {
            ProviderIds = new Dictionary<string, string>
            {
                ["Tmdb"] = " 603 ",
                ["Imdb"] = "tt0133093",
                ["Tvdb"] = "  ",
                [" "] = "x",
                ["tmdb"] = "999"
            }
        };

        Assert.Equal(2, snapshot.ProviderIds.Count);
        Assert.Equal("603", snapshot.ProviderIds["Tmdb"]);
        Assert.Equal("603", snapshot.ProviderIds["TMDB"]);
        Assert.Equal("tt0133093", snapshot.ProviderIds["imdb"]);
        Assert.False(snapshot.ProviderIds.ContainsKey("Tvdb"));

        // A map with nothing usable is null for the series ids, empty for the item's own.
        var empty = new IdentitySnapshot { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "" }, SeriesProviderIds = new Dictionary<string, string>() };
        Assert.Empty(empty.ProviderIds);
        Assert.Null(empty.SeriesProviderIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" \r\n ")]
    public void ABlankPath_IsStoredAsNull(string path)
    {
        // 10.11.5's FindByPath of a blank path answers with the newest item of the whole library.
        Assert.Null(new IdentitySnapshot { Path = path }.Path);
        Assert.Null((new IdentitySnapshot { Path = @"Q:\a.mkv" } with { Path = path }).Path);

        var json = """{"Kind":"Movie","Path":""" + JsonSerializer.Serialize(path) + "}";
        var read = JsonSerializer.Deserialize<IdentitySnapshot>(json, JsonStoreFormat.Options)!;
        Assert.Null(read.Path);
        Assert.DoesNotContain("Path", JsonSerializer.Serialize(read, JsonStoreFormat.Options), StringComparison.Ordinal);
        Assert.Equal(new IdentitySnapshot { Kind = "Movie" }, read);

        // A path is kept as it is otherwise, spaces included.
        Assert.Equal(@" Q:\a b.mkv ", new IdentitySnapshot { Path = @" Q:\a b.mkv " }.Path);
    }

    [Fact]
    public void ProviderIds_AreWrittenInOrdinalOrder()
    {
        var snapshot = new IdentitySnapshot { ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "3", ["Imdb"] = "tt1", ["Tmdb"] = "2" } };

        var json = JsonSerializer.Serialize(snapshot, JsonStoreFormat.Options);

        Assert.Equal(new[] { "Imdb", "Tmdb", "Tvdb" }, JsonNode.Parse(json)!["ProviderIds"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void Equality_ComparesMapsByContent_AndIgnoresExtra()
    {
        var a = Episode();
        var b = Episode() with
        {
            ProviderIds = new Dictionary<string, string> { ["tvdb"] = "4000007", ["IMDB"] = "tt0000007" },
            Extra = new Dictionary<string, JsonElement> { ["Future"] = JsonSerializer.SerializeToElement(1) }
        };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { RunTimeTicks = 1 });
        Assert.NotEqual(a, a with { SeriesProviderIds = null });
        Assert.NotEqual(a, a with { ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "4000008", ["Imdb"] = "tt0000007" } });
        Assert.NotEqual(a, a with { Path = null });
        Assert.NotEqual(a, a with { SeasonId = Guid.NewGuid() });
    }

    [Fact]
    public void UnknownFields_SurviveARoundTrip()
    {
        const string Json = """{"ItemId":"0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0","Kind":"Movie","Name":"Heat","Edition":"Director's Cut","Scores":[1,2]}""";

        var snapshot = JsonSerializer.Deserialize<IdentitySnapshot>(Json, JsonStoreFormat.Options)!;
        var written = JsonNode.Parse(JsonSerializer.Serialize(snapshot, JsonStoreFormat.Options))!;

        Assert.Equal("Director's Cut", written["Edition"]!.GetValue<string>());
        Assert.Equal(2, written["Scores"]!.AsArray().Count);
    }

    internal static IdentitySnapshot Episode() => new()
    {
        ItemId = ItemId,
        Kind = "Episode",
        Name = "The One",
        Year = 2004,
        ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "4000007", ["Imdb"] = "tt0000007" },
        SeriesProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189", ["Tmdb"] = "1396" },
        SeriesName = "Some Show",
        Season = 3,
        Episode = 7,
        SeriesId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        SeasonId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"),
        RunTimeTicks = TimeSpan.FromMinutes(47).Ticks,
        Path = @"Q:\TV\Some Show\Season 03\Some Show S03E07.mkv"
    };
}
