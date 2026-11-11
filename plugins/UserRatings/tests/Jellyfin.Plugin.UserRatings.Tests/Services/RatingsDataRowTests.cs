using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// An import checks each row of the file against the store's rules, leaves a bad one out with
/// its reason, row by row in the dry run and the result, and imports the rest; a review gets a member's normalisation.
/// </summary>
public sealed partial class RatingsDataTests
{
    [Theory]
    [InlineData("Origin", "\"Bogus\"", "Its Origin 'Bogus' isn't one this version knows.")]
    [InlineData("DetachReason", "\"Bogus\"", "Its DetachReason 'Bogus' isn't one this version knows.")]
    [InlineData("Stars", "7", "Its stars aren't 0.5 to 5.")]
    [InlineData("EditCount", "-1", "Its EditCount is negative.")]
    [InlineData("UserId", "\"00000000-0000-0000-0000-000000000000\"", "It has no UserId.")]
    [InlineData("ReviewText", "\"\\u0000 \\u0007\\r\\n\"", "It has neither stars nor a review.")]
    [InlineData("CreatedUtc", "\"not a time\"", "A value can't be read ($.CreatedUtc).")]
    public async Task OneBadRow_IsLeftOutWithItsReason_AndTheOthersAreImported(string field, string value, string reason)
    {
        // Turned round: the store's Validate used to throw at the write and the whole import failed.
        var (file, good, bad, index) = await TwoRowFileAsync(row =>
        {
            row[field] = JsonNode.Parse(value);
            if (field == "ReviewText")
            {
                row.Remove("Stars");
            }
        });

        var dry = await ImportAsync(file, "Merge", dryRun: true);
        Assert.Equal(1, dry["Unusable"]);
        Assert.Equal(new ImportUnusableRows.Entry("Ratings", index + 1, bad.ToString("D", CultureInfo.InvariantCulture), reason), Assert.Single(Unusable(dry)));
        Assert.Equal(1, Outcome(dry, "Added"));

        var report = await ImportAsync(file, "Merge", dryRun: false);
        Assert.Equal(1, report["Unusable"]);
        Assert.Equal(reason, Assert.Single(Unusable(report)).Reason);
        Assert.Equal(1, Outcome(report, "Added"));
        Assert.Equal("Good row.", Store.Get(good)!.ReviewText);
        Assert.Null(Store.Get(bad));
    }

    [Fact]
    public async Task AnImportedReview_IsNormalisedAsAMembersWriteIs_LiveAndArchived_AndAnItemsNameLosesItsControlCharacters()
    {
        // A NUL in the review used to fail the import, and imported text skipped the normalisation.
        var archived = await ArchivedRatingAsync("Archived.");
        var (file, good, bad, _) = await TwoRowFileAsync(row => row["ReviewText"] = "  A NUL\u0000 here,\r\nCRLF there.\u0007  ");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        var entry = json["Archive"]!.AsArray().Single()!.AsObject();
        entry["ReviewText"] = "Archived\u0000 again.\r";
        entry["Identity"]!["Name"] = "Gone\u0001";
        await File.WriteAllTextAsync(file, json.ToJsonString(), Ct);

        var report = await ImportAsync(file, "Replace", dryRun: false);
        Assert.Equal(0, report["Unusable"]);
        Assert.Empty(Unusable(report));
        Assert.Equal("Good row.", Store.Get(good)!.ReviewText);
        Assert.Equal("A NUL here,\nCRLF there.", Store.Get(bad)!.ReviewText);
        var line = Assert.Single(_detached.ArchiveOf(_consumer).Read().Records);
        Assert.Equal(archived.ToString("D", CultureInfo.InvariantCulture), line.Id);
        Assert.Equal("Archived again.", (string?)line.Record["ReviewText"]);
        Assert.Equal("Gone", line.Identity.Name);
    }

    [Fact]
    public async Task ABadRowOfAnyTable_IsLeftOutWithItsReason_AndReplaceImportsTheRest()
    {
        // Replace imports the log rows, so a log row with an unknown action failed the whole import too; a tombstone with an
        // unknown DeletedBy did in both modes.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var kept = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Kept."), Ct);
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        var data = json["Data"]!.AsObject();
        JsonArray Table(string name) => data[name] as JsonArray ?? (JsonArray)(data[name] = new JsonArray());
        var twin = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var former = Guid.NewGuid();
        var at = StoreFixture.T0.ToString("O", CultureInfo.InvariantCulture);
        Table("Ratings").Add(null);
        Table("Ratings").Add(5);
        Table("FormerUsers").Add(new JsonObject { ["Id"] = twin, ["OriginalUserId"] = twin, ["DisplayName"] = "Twin", ["DeletedUtc"] = at });
        var twinRow = Table("FormerUsers").Count;
        Table("FormerUsers").Add(new JsonObject { ["Id"] = former.ToString("D", CultureInfo.InvariantCulture), ["OriginalUserId"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), ["DisplayName"] = "Former\u0000 member\tB", ["DeletedUtc"] = at });
        Table("Tombstones").Add(new JsonObject { ["RatingId"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), ["UserId"] = _s.Viewer.Id.ToString("D", CultureInfo.InvariantCulture), ["ItemId"] = movie.Id.ToString("D", CultureInfo.InvariantCulture), ["DeletedUtc"] = at, ["DeletedBy"] = "Bogus" });
        Table("ModerationLog").Add(new JsonObject { ["Id"] = 99, ["AtUtc"] = at, ["Action"] = "Bogus" });
        Table("ImportLedger").Add(new JsonObject { ["Source"] = string.Empty, ["SourceKey"] = "k", ["SourceUpdatedUtc"] = at, ["ImportedUtc"] = at });
        var crafted = Path.Combine(_s.Store.Directory.Path, "crafted-tables.json");
        await File.WriteAllTextAsync(crafted, json.ToJsonString(), Ct);
        var ratings = Table("Ratings").Count;

        var report = await ImportAsync(crafted, "Replace", dryRun: false);
        Assert.Equal(6, report["Unusable"]);
        Assert.Equal(
            new[]
            {
                ("Ratings", ratings - 1, "The row is empty."),
                ("Ratings", ratings, "A value can't be read ($)."),
                ("FormerUsers", twinRow, "Its Id is its deleted user's id."),
                ("Tombstones", Table("Tombstones").Count, "Its DeletedBy 'Bogus' isn't one this version knows."),
                ("ModerationLog", Table("ModerationLog").Count, "Its Action 'Bogus' isn't one this version knows."),
                ("ImportLedger", Table("ImportLedger").Count, "It has no Source or no SourceKey.")
            },
            Unusable(report).Select(u => (u.Table, u.Row, u.Reason)));
        Assert.Equal(1, Outcome(report, "Unchanged") + Outcome(report, "Added"));
        Assert.Equal("Kept.", Store.Get(kept.Id)!.ReviewText);
        Assert.Equal("Former member B", Store.GetFormerUsers()[former]);
        Assert.DoesNotContain(Store.GetModerationLog(0, 1000), l => l.Action == "Bogus");
    }

    [Fact]
    public async Task AFileOfManyBadRows_CountsThemAll_AndListsTheFirst100()
    {
        var (file, _, _, badIndex) = await TwoRowFileAsync(row => row["Origin"] = "Bogus");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        var ratings = json["Data"]!["Ratings"]!.AsArray();
        for (var i = 0; i < 150; i++)
        {
            var copy = ratings[badIndex]!.DeepClone().AsObject();
            copy["Id"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            copy["Origin"] = "\u0000" + new string('x', 200);
            ratings.Add(copy);
        }

        await File.WriteAllTextAsync(file, json.ToJsonString(), Ct);
        var report = await ImportAsync(file, "Merge", dryRun: true);
        Assert.Equal(151, report["Unusable"]);
        Assert.Equal(ImportUnusableRows.MaxListed, Unusable(report).Count);
        Assert.Equal(1, Outcome(report, "Added"));
        Assert.All(Unusable(report).Skip(1), u => Assert.Equal("Its Origin '" + new string('x', 40) + "…' isn't one this version knows.", u.Reason));
    }

    private static IReadOnlyList<ImportUnusableRows.Entry> Unusable(IDictionary<string, object?> report)
        => (IReadOnlyList<ImportUnusableRows.Entry>)report["UnusableRows"]!;

    // An export of two ratings under new ids, "Good row." and "Bad row." (changed by breakIt), without tombstones; the
    // ratings it was made from deleted here, so both would be added.
    private async Task<(string File, Guid Good, Guid Bad, int BadIndex)> TwoRowFileAsync(Action<JsonObject> breakIt)
    {
        var a = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var b = _s.AddMovie(_s.Movies, "Heat", "949");
        var first = await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "Good row."), Ct);
        var second = await Store.UpsertAsync(Rating(_s.Viewer.Id, b.Id, "Bad row."), Ct);
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        var ratings = json["Data"]!["Ratings"]!.AsArray();
        Guid good = Guid.NewGuid(), bad = Guid.NewGuid();
        var badIndex = -1;
        for (var i = 0; i < ratings.Count; i++)
        {
            var row = ratings[i]!.AsObject();
            var isBad = (string?)row["ReviewText"] == "Bad row.";
            row["Id"] = (isBad ? bad : good).ToString("D", CultureInfo.InvariantCulture);
            if (isBad)
            {
                badIndex = i;
                breakIt(row);
            }
        }

        json["Data"]!["Tombstones"] = new JsonArray();
        var crafted = Path.Combine(_s.Store.Directory.Path, "two-rows-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(crafted, json.ToJsonString(), Ct);
        await Store.DeleteAsync(first.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddMinutes(1), Ct);
        await Store.DeleteAsync(second.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddMinutes(1), Ct);
        return (crafted, good, bad, badIndex);
    }
}
