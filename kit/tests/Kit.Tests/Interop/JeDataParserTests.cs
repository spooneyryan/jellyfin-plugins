using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Interop;

/// <summary>
/// <see cref="JeDataParser"/> on the synthetic Jellyfin Enhanced files of <c>kit/testdata/je-data</c>: the value types
/// JE 12.8 to 12.11 write are accepted, an entry of another type is left out with its reason, and a file of another
/// shape is "unsupported JE format", never an exception.
/// </summary>
public sealed class JeDataParserTests
{
    private static readonly string DataRoot = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "testdata", "je-data");
    private static readonly Guid User1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid User2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void ACleanBookmarkFile_Parses_WithStringAndNumberIds()
    {
        var file = JeDataParser.ParseBookmarks(Read("00000000000000000000000000000001", "bookmark.json"), User1);

        Assert.Equal(JeDataFormat.Bookmarks1, file.Format);
        Assert.Null(file.Problem);
        Assert.Empty(file.Invalid);
        Assert.Equal(3, file.Entries.Count);

        var first = file.Entries[0];
        Assert.Equal("Bm_1772390400000_a1b2c3", first.Key);
        Assert.Equal("11111111111111111111111111111111", first.ItemId);
        Assert.Equal("550", first.TmdbId);
        Assert.Equal(string.Empty, first.TvdbId);
        Assert.Equal(62.5, first.Timestamp);
        Assert.Equal("Bank job", first.Label);
        Assert.Equal("2026-03-01T20:00:00.000Z", first.CreatedAt);
        Assert.Null(first.SeasonNumber);

        // A number where JE usually writes text, and null for a missing provider id.
        Assert.Equal("550", file.Entries[1].TmdbId);
        Assert.Null(file.Entries[1].TvdbId);
        Assert.Equal(string.Empty, file.Entries[1].Label);

        var episode = file.Entries[2];
        Assert.Equal("tv", episode.MediaType);
        Assert.Equal("81189", episode.TvdbId);
        Assert.Equal(1, episode.SeasonNumber);
        Assert.Equal(2, episode.EpisodeNumber);
        Assert.Equal(600, episode.Timestamp);
    }

    [Fact]
    public void OddEntries_AreLeftOut_WithTheirReason_AndARepeatedKey_KeepsItsLastValue()
    {
        var file = JeDataParser.ParseBookmarks(Read("00000000000000000000000000000002", "bookmark.json"), User2);

        Assert.Equal(JeDataFormat.Bookmarks1, file.Format);
        Assert.Equal(["Bm_1772400000000_good01", "Bm_1772400400000_twice", "Bm_1772400500000_synced"], file.Entries.Select(e => e.Key));
        Assert.Equal("Second of two with this key: the one kept", file.Entries[1].Label);
        Assert.Equal(20, file.Entries[1].Timestamp);
        Assert.Equal("11111111111111111111111111111111", file.Entries[2].SyncedFrom);

        Assert.Equal(
            [
                ("Bm_1772400100000_badts", "Timestamp is a text, not a number of seconds."),
                ("Bm_1772400200000_noitem", "ItemId is missing."),
                ("Bm_1772400300000_negative", "Timestamp is negative."),
                ("Bm_1772400600000_list", "Not an object.")
            ],
            file.Invalid.Select(i => (i.Key, i.Reason)));
    }

    [Fact]
    public void ReviewsParse_WithNullRatings_NumberIds_AndTheOddOnesLeftOut()
    {
        var file = JeDataParser.ParseReviews(Read(null, "reviews.json"));

        Assert.Equal(JeDataFormat.Reviews1, file.Format);
        Assert.Null(file.Problem);
        Assert.Equal(4, file.Entries.Count);
        Assert.Equal("00000000000000000000000000000001:movie:550", file.Entries[0].Key);
        Assert.Equal(4.5, file.Entries[0].Rating);
        Assert.Equal(string.Empty, file.Entries[1].Content);
        Assert.Equal(5, file.Entries[1].Rating);
        Assert.Equal("550", file.Entries[2].TmdbId);
        Assert.Null(file.Entries[2].Rating);
        Assert.Equal("00000000000000000000000000000002:tv:1396:s1:e1", file.Entries[3].Key);
        Assert.Equal(
            [("00000000000000000000000000000002:movie:bad-rating", "Rating is a text, not a number."), ("00000000000000000000000000000002:movie:no-tmdb", "TmdbId is missing.")],
            file.Invalid.Select(i => (i.Key, i.Reason)));

        // The field names seen, in the order first seen, names only (User Ratings' SourceFields).
        Assert.Equal(["UserId", "TmdbId", "MediaType", "Content", "Rating", "CreatedAt", "UpdatedAt"], file.Fields);
    }

    [Fact]
    public void ReviewsFields_AreNamesOnly_FirstSeenFirst_AndCapped()
    {
        var many = string.Join(",", Enumerable.Range(0, 60).Select(i => "\"F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\": 1"));
        var json = "{\"Reviews\": {\"a\": {\"TmdbId\": \"1\", \"Rating\": 3, \"Secret words\": \"never listed\"}, \"b\": {\"TmdbId\": \"2\", \"Content\": \"x\", " + many + "}}}";

        var file = JeDataParser.ParseReviews(Encoding.UTF8.GetBytes(json));

        Assert.Equal(JeReviewsFile.MaxFields, file.Fields.Count);
        Assert.Equal(["TmdbId", "Rating", "Secret words", "Content", "F0"], file.Fields.Take(5));
        Assert.DoesNotContain("never listed", file.Fields);
    }

    [Fact]
    public void AUsersShortcuts_KeepNameAndKey_WithNullAsOff_AndSkipEntriesWithoutEither()
    {
        Assert.True(JeDataParser.TryParseUserShortcuts(Read("00000000000000000000000000000001", "shortcuts.json"), out var shortcuts, out var problem));

        Assert.Null(problem);
        Assert.Equal(
            [("BookmarkCurrentTime", "Shift+Alt+B"), ("SubtitleMenu", ""), ("OpenSearch", "")],
            shortcuts.Select(s => (s.Name, s.Key)));
        Assert.Equal("Bookmark Current Time", shortcuts[0].Label);
        Assert.Equal("Player", shortcuts[0].Category);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"Bookmarks\": []}")]
    [InlineData("{\"bookmarks\": {\"a\": {\"Label\": \"x\"}}}")]
    public void AnotherShape_IsUnsupported_NotAnException(string json)
    {
        var bookmarks = JeDataParser.ParseBookmarks(Encoding.UTF8.GetBytes(json), User1);
        Assert.Equal(JeDataFormat.Unsupported, bookmarks.Format);
        Assert.StartsWith("Unsupported Jellyfin Enhanced format", bookmarks.Problem, StringComparison.Ordinal);
        Assert.Empty(bookmarks.Entries);

        var reviews = JeDataParser.ParseReviews(Encoding.UTF8.GetBytes(json));
        Assert.Equal(JeDataFormat.Unsupported, reviews.Format);

        Assert.False(JeDataParser.TryParseUserShortcuts(Encoding.UTF8.GetBytes(json), out var shortcuts, out var problem));
        Assert.Empty(shortcuts);
        Assert.StartsWith("Unsupported Jellyfin Enhanced format", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullItemId_OrANullTmdbIdOfAReview_IsMissing_NotAnEntryWithoutOne()
    {
        // "ItemId": null used to pass as a bookmark. A null where an optional id sits stays null.
        var bookmarks = JeDataParser.ParseBookmarks(
            Encoding.UTF8.GetBytes("{\"Bookmarks\": {\"a\": {\"ItemId\": null, \"Timestamp\": 1}, \"b\": {\"ItemId\": \"x\", \"Timestamp\": 2, \"TmdbId\": null}}}"),
            User1);
        Assert.Equal(JeDataFormat.Bookmarks1, bookmarks.Format);
        var kept = Assert.Single(bookmarks.Entries);
        Assert.Equal("b", kept.Key);
        Assert.Null(kept.TmdbId);
        Assert.Equal([("a", "ItemId is missing.")], bookmarks.Invalid.Select(i => (i.Key, i.Reason)));

        var reviews = JeDataParser.ParseReviews(Encoding.UTF8.GetBytes("{\"Reviews\": {\"r\": {\"TmdbId\": null, \"Rating\": 3}, \"s\": {\"TmdbId\": \"7\", \"Rating\": null}}}"));
        Assert.Equal(JeDataFormat.Reviews1, reviews.Format);
        Assert.Equal(["s"], reviews.Entries.Select(e => e.Key));
        Assert.Equal([("r", "TmdbId is missing.")], reviews.Invalid.Select(i => (i.Key, i.Reason)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"Bookmarks\": {\"a\": {\"ItemId\": \"x\", \"Timestamp\": 1")]
    [InlineData("﻿{\"Bookmarks\": {\"a\": {\"ItemId\": \"x\", \"Timestamp\": ")]
    public void AFileBeingWritten_IsUnreadable_WithTheReason(string json)
    {
        var file = JeDataParser.ParseBookmarks(Encoding.UTF8.GetBytes(json), User1);

        Assert.Equal(JeDataFormat.Unreadable, file.Format);
        Assert.StartsWith("Not valid JSON", file.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldNames_MatchIgnoringCase_AsNewtonsoftDoes_AndABomIsDropped()
    {
        var json = "﻿{\"bookmarks\": {\"k\": {\"itemId\": \"1\", \"timestamp\": 2.5, \"label\": \"L\", \"TMDBID\": 7}}}";

        var file = JeDataParser.ParseBookmarks(Encoding.UTF8.GetBytes(json), User1);

        Assert.Equal(JeDataFormat.Bookmarks1, file.Format);
        var entry = Assert.Single(file.Entries);
        Assert.Equal("1", entry.ItemId);
        Assert.Equal(2.5, entry.Timestamp);
        Assert.Equal("L", entry.Label);
        Assert.Equal("7", entry.TmdbId);
    }

    // Bytes that aren't UTF-8 in a value, a key or a field name made GetString throw (the import answered 500); every
    // one of the three files is Unreadable now, with a plain reason, never an exception.
    [Theory]
    [InlineData("value")]
    [InlineData("key")]
    [InlineData("field")]
    public void BytesThatArentUtf8_AreUnreadable_InEveryFile_NeverAnException(string where)
    {
        static byte[] With(string json)
        {
            // "@@" marks where the bytes go: 0xC3 0x28 is a lead byte without its continuation.
            var at = json.IndexOf("@@", StringComparison.Ordinal);
            return [.. Encoding.UTF8.GetBytes(json[..at]), 0xC3, 0x28, .. Encoding.UTF8.GetBytes(json[(at + 2)..])];
        }

        var reviews = where switch
        {
            "value" => "{\"Reviews\": {\"k\": {\"UserId\": \"u\", \"TmdbId\": \"603\", \"MediaType\": \"movie\", \"Content\": \"bad @@ text\", \"Rating\": 4}}}",
            "key" => "{\"Reviews\": {\"k@@\": {\"UserId\": \"u\", \"TmdbId\": \"603\", \"MediaType\": \"movie\", \"Content\": \"text\", \"Rating\": 4}}}",
            _ => "{\"Reviews\": {\"k\": {\"UserId\": \"u\", \"TmdbId\": \"603\", \"Content\": \"text\", \"Rating\": 4, \"Odd@@\": 1}}}"
        };

        var file = JeDataParser.ParseReviews(With(reviews));
        Assert.Equal((JeDataFormat.Unreadable, JeDataParser.NotUtf8), (file.Format, file.Problem));
        Assert.Empty(file.Entries);

        var bookmarks = JeDataParser.ParseBookmarks(With("{\"Bookmarks\": {\"a\": {\"ItemId\": \"x\", \"Timestamp\": 1, \"Label\": \"@@\"}}}"), User1);
        Assert.Equal((JeDataFormat.Unreadable, JeDataParser.NotUtf8), (bookmarks.Format, bookmarks.Problem));

        Assert.False(JeDataParser.TryParseUserShortcuts(With("{\"Shortcuts\": [{\"Name\": \"BookmarkCurrentTime\", \"Key\": \"@@\"}]}"), out var shortcuts, out var problem));
        Assert.Empty(shortcuts);
        Assert.Equal(JeDataParser.NotUtf8, problem);
    }

    // An escaped half of a surrogate pair alone: .NET 10's GetString throws on it (.NET 9's returns it), so the parser finds
    // it itself and the file is Unreadable on both; a whole escaped pair (an emoji) is text.
    [Theory]
    [InlineData("{\"Reviews\": {\"k\": {\"UserId\": \"u\", \"TmdbId\": \"603\", \"Content\": \"lone \\ud800 high\", \"Rating\": 4}}}")]
    [InlineData("{\"Reviews\": {\"k\": {\"UserId\": \"u\", \"TmdbId\": \"603\", \"Content\": \"lone \\udc00 low\", \"Rating\": 4}}}")]
    [InlineData("{\"Reviews\": {\"k\\ud800\": {\"UserId\": \"u\", \"TmdbId\": \"603\", \"Content\": \"in the key\", \"Rating\": 4}}}")]
    [InlineData("{\"Reviews\": {\"k\": {\"UserId\": \"u\", \"TmdbId\": \"603\", \"Content\": \"text\", \"Rating\": 4, \"F\\udbff\": 1}}}")]
    [InlineData("{\"Reviews\": {\"k\": {\"UserId\": \"u\", \"TmdbId\": \"603\", \"Content\": \"reversed \\udc00\\ud800\", \"Rating\": 4}}}")]
    public void AnEscapedLoneSurrogate_IsUnreadable_NeverAnException(string json)
    {
        var file = JeDataParser.ParseReviews(Encoding.UTF8.GetBytes(json));

        Assert.Equal((JeDataFormat.Unreadable, JeDataParser.NotText), (file.Format, file.Problem));
        Assert.Equal(JeDataFormat.Unreadable, JeDataParser.ParseBookmarks(Encoding.UTF8.GetBytes(json.Replace("Reviews", "Bookmarks", StringComparison.Ordinal)), User1).Format);
    }

    [Fact]
    public void AWholeEscapedSurrogatePair_IsText()
    {
        var file = JeDataParser.ParseReviews(Encoding.UTF8.GetBytes("{\"Reviews\": {\"k\": {\"UserId\": \"u\", \"TmdbId\": \"603\", \"Content\": \"a film \\ud83c\\udfac\", \"Rating\": 4}}}"));

        Assert.Equal(JeDataFormat.Reviews1, file.Format);
        Assert.Equal("a film \U0001F3AC", Assert.Single(file.Entries).Content);
    }

    private static ReadOnlyMemory<byte> Read(string? user, string file)
        => File.ReadAllBytes(user is null ? Path.Combine(DataRoot, file) : Path.Combine(DataRoot, user, file));
}
