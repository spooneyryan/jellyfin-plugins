using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Storage;

/// <summary>
/// <see cref="SqliteRatingsStore"/> on a real <c>userratings.db</c>: ratings with their provider
/// keys and container ids, the per-user summary rows, deletes with tombstones, the moderation log, the import ledger and
/// its rows, meta values, the generation, and the store's validation (growable enums in code).
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SqliteRatingsStoreTests : IDisposable
{
    private static readonly Guid Alex = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Sam = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly Guid Matrix = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private readonly StoreFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Upsert_StoresEveryColumn_AndReadsItBack()
    {
        var record = StoreFixture.Movie(Alex, Matrix, 7, "Still great.") with { IsSpoiler = true, EditCount = 2, Origin = RatingValues.Origin.JellyfinEnhanced, MirroredValue = 7 };

        await _fx.Store.UpsertAsync(record, Ct);

        var read = _fx.Store.Get(record.Id)!;
        Assert.Equal(record, read);
        Assert.Equal(record.Identity, read.Identity);
        Assert.Equal(DateTimeKind.Utc, read.CreatedUtc.Kind);
        Assert.True(read.IsAttached);
        Assert.Equal(record, _fx.Store.GetAttached(Alex, Matrix));
        Assert.Equal([record], _fx.Store.GetAttachedForItem(Matrix));

        // As the schema stores it: "N" GUIDs, ISO-8601 round-trip UTC text, provider ids as JSON in ordinal order.
        var raw = _fx.Database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT Id, CreatedUtc, ProviderIds, ItemPath, Stars2 FROM Ratings;";
            using var reader = command.ExecuteReader();
            reader.Read();
            return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4));
        });
        Assert.Equal((record.Id.ToString("N"), "2026-10-19T03:20:00.0000000Z", "{\"Imdb\":\"tt0133093\",\"Tmdb\":\"603\"}", record.Identity.Path, 7), raw);
    }

    [Fact]
    public async Task Upsert_WritesTheProviderKeys_AndReplacesThemOnAnUpdate()
    {
        var record = StoreFixture.Movie(Alex, Matrix);
        await _fx.Store.UpsertAsync(record, Ct);

        Assert.Equal(["Movie|Imdb|tt0133093", "Movie|Tmdb|603"], _fx.Store.GetKeys(record.Id));

        var renamed = record with { Identity = record.Identity with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "604" } }, Stars2 = 9 };
        await _fx.Store.UpsertAsync(renamed, Ct);

        Assert.Equal(["Movie|Tmdb|604"], _fx.Store.GetKeys(record.Id));
        Assert.Equal(9, _fx.Store.Get(record.Id)!.Stars2);
        Assert.Equal(1, _fx.Count("Ratings"));
        Assert.Equal(1, _fx.Count("RatingKeys"));
        Assert.Equal([renamed], _fx.Store.FindByKeys(["Movie|Tmdb|604"], detached: false));
        Assert.Empty(_fx.Store.FindByKeys(["Movie|Tmdb|603"], detached: false));
        Assert.Empty(_fx.Store.FindByKeys(["Movie|Tmdb|604"], detached: true));
    }

    [Fact]
    public async Task ContainerIds_AreWritten_ForEpisodesSeasonsAndTracks()
    {
        // A removed series raises one event, so rows keep SeriesId, SeasonId and AlbumId.
        var series = Guid.NewGuid();
        var season = Guid.NewGuid();
        var album = Guid.NewGuid();
        var episode = StoreFixture.Episode(Alex, Guid.NewGuid(), series, season, 1, 2);
        var track = StoreFixture.Track(Alex, Guid.NewGuid(), album);
        await _fx.Store.UpsertManyAsync([episode, track], Ct);

        var columns = _fx.Database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT ItemKind, SeriesId, SeasonId, AlbumId FROM Ratings ORDER BY ItemKind;";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(string.Join('|', Enumerable.Range(0, 4).Select(i => reader.IsDBNull(i) ? "null" : reader.GetString(i))));
            }

            return rows;
        });
        Assert.Equal([$"Audio|null|null|{album:N}", $"Episode|{series:N}|{season:N}|null"], columns);

        // Episode keys: its own strong id and the series' ids with the numbers (RatingKeys).
        Assert.Equal(["Episode|Tmdb|1396|S01E02", "Episode|Tvdb|349232", "Episode|Tvdb|81189|S01E02"], _fx.Store.GetKeys(episode.Id));
        Assert.Empty(_fx.Store.GetKeys(track.Id));
        Assert.Equal(episode.Identity, _fx.Store.Get(episode.Id)!.Identity);
    }

    [Fact]
    public async Task TheUniquePartialIndex_AllowsOneAttachedRatingPerMemberAndItem_AndAnyNumberDetached()
    {
        var first = StoreFixture.Movie(Alex, Matrix);
        await _fx.Store.UpsertAsync(first, Ct);

        var second = StoreFixture.Movie(Alex, Matrix, 3);
        var conflict = await Assert.ThrowsAsync<PluginException>(() => _fx.Store.UpsertAsync(second, Ct));
        Assert.Equal((PluginErrorKind.Conflict, "This member already has a rating of this item."), (conflict.Kind, conflict.Message));
        Assert.Equal(1, _fx.Count("Ratings"));
        Assert.Empty(_fx.Store.GetKeys(second.Id));

        // A detached row of the same member and item is no conflict (UX_Ratings_User_Item WHERE DetachedUtc IS NULL).
        var detached = second with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Ambiguous };
        await _fx.Store.UpsertAsync(detached, Ct);
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Sam, Matrix), Ct);

        Assert.Equal(3, _fx.Count("Ratings"));
        Assert.Equal(first, _fx.Store.GetAttached(Alex, Matrix));
        Assert.Equal([detached], _fx.Store.FindByKeys(["Movie|Tmdb|603"], detached: true));
        Assert.Equal(2, _fx.Store.FindByKeys(["Movie|Tmdb|603", "Movie|Imdb|tt0133093"], detached: false).Count);

        // The copies' hint: the attached rows' item ids only, each once, never the detached row's.
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Alex, Other, text: "Not read for the hint."), Ct);
        Assert.Equal([Matrix, Other], _fx.Store.FindItemIdsByKeys(["Movie|Tmdb|603", "Movie|Imdb|tt0133093"]));
        Assert.Empty(_fx.Store.FindItemIdsByKeys(["Movie|Tmdb|999"]));
        Assert.Empty(_fx.Store.FindItemIdsByKeys([]));
        await _fx.Store.UpsertAsync(detached with { Id = Guid.NewGuid(), Identity = detached.Identity with { ItemId = Guid.NewGuid() } }, Ct);
        Assert.Equal([Matrix, Other], _fx.Store.FindItemIdsByKeys(["Movie|Tmdb|603"]));
    }

    [Fact]
    public async Task SummaryRows_AreThePerUserContributionsOfAttachedRatings()
    {
        await _fx.Store.UpsertManyAsync(
            [
                StoreFixture.Movie(Alex, Matrix, 7, "Good."),
                StoreFixture.Movie(Sam, Matrix, null, "No stars, just words."),
                StoreFixture.Movie(Alex, Other, 10),
                StoreFixture.Movie(Sam, Other, 2) with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Removed }
            ],
            Ct);

        var rows = _fx.Store.GetSummaryRows([Matrix, Other, Guid.NewGuid()]).OrderBy(r => r.ItemId).ThenBy(r => r.UserId).ToList();

        Assert.Equal(
            [new SummaryRow(Matrix, Alex, 7, true), new SummaryRow(Matrix, Sam, null, true), new SummaryRow(Other, Alex, 10, false)],
            rows);
        Assert.Empty(_fx.Store.GetSummaryRows([]));
    }

    [Fact]
    public async Task SummaryRows_SpanTheInListsChunks()
    {
        var items = Enumerable.Range(0, RatingsSql.InChunk + 37).Select(_ => Guid.NewGuid()).ToList();
        await _fx.Store.UpsertManyAsync(items.Select(i => StoreFixture.Movie(Alex, i, 6, tmdb: i.ToString("N"))).ToList(), Ct);

        Assert.Equal(items.Count, _fx.Store.GetSummaryRows(items).Count);
    }

    [Fact]
    public async Task Delete_RemovesTheRowAndItsKeys_AndWritesATombstoneInTheSameTransaction()
    {
        var record = StoreFixture.Movie(Alex, Matrix, 7, "Words.");
        await _fx.Store.UpsertAsync(record, Ct);
        var at = StoreFixture.T0.AddDays(1);

        Assert.True(await _fx.Store.DeleteAsync(record.Id, RatingValues.DeletedBy.User, at, Ct));

        Assert.Null(_fx.Store.Get(record.Id));
        Assert.Equal(0, _fx.Count("RatingKeys"));
        Assert.Equal(new TombstoneRecord(record.Id, Alex, Matrix, at, RatingValues.DeletedBy.User), _fx.Store.GetTombstone(record.Id));

        // Ids and times only: no stars, no text.
        Assert.Equal(["RatingId", "UserId", "ItemId", "DeletedUtc", "DeletedBy", "Origin"], _fx.Database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('Tombstones') ORDER BY cid;";
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }));
        Assert.False(await _fx.Store.DeleteAsync(record.Id, RatingValues.DeletedBy.User, at, Ct));
        Assert.Null(_fx.Store.GetTombstone(Guid.NewGuid()));
    }

    [Fact]
    public async Task TheModerationLog_IsAppendOnly_NewestFirst()
    {
        var first = await _fx.Store.AppendModerationLogAsync(new ModerationLogRecord { AtUtc = StoreFixture.T0, Action = RatingValues.ModerationAction.RemoveText, ActorUserId = Alex, TargetUserId = Sam, ItemId = Matrix, Reason = "spoilers", TextSnapshot = "the text", SnapshotExpiresUtc = StoreFixture.T0.AddDays(30) }, Ct);
        var second = await _fx.Store.AppendModerationLogAsync(new ModerationLogRecord { AtUtc = StoreFixture.T0.AddHours(1), Action = RatingValues.ModerationAction.ImportJe, Details = "{\"Created\":3}" }, Ct);

        Assert.Equal((1L, 2L), (first, second));
        var page = _fx.Store.GetModerationLog(0, 10);
        Assert.Equal([2L, 1L], page.Select(e => e.Id));
        Assert.Equal(new ModerationLogRecord { Id = 1, AtUtc = StoreFixture.T0, Action = RatingValues.ModerationAction.RemoveText, ActorUserId = Alex, TargetUserId = Sam, ItemId = Matrix, Reason = "spoilers", TextSnapshot = "the text", SnapshotExpiresUtc = StoreFixture.T0.AddDays(30) }, page[1]);
        Assert.Null(page[0].ActorUserId);
        Assert.Equal([1L], _fx.Store.GetModerationLog(1, 10).Select(e => e.Id));
    }

    [Fact]
    public async Task TheImportLedger_KeepsEachEntryWithEveryRowItProduced()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var entry = new ImportLedgerRecord("JE", "abc:movie:603", StoreFixture.T0, StoreFixture.T0.AddDays(2), [a, b]);

        await _fx.Store.RecordImportAsync(entry, Ct);

        var read = _fx.Store.GetImport("JE", "abc:movie:603")!;
        Assert.Equal((entry.Source, entry.SourceKey, entry.SourceUpdatedUtc, entry.ImportedUtc), (read.Source, read.SourceKey, read.SourceUpdatedUtc, read.ImportedUtc));
        Assert.Equal(new[] { a, b }.Order(), read.RatingIds.Order());

        // A newer copy replaces the entry and its rows ("Updated").
        await _fx.Store.RecordImportAsync(entry with { SourceUpdatedUtc = StoreFixture.T0.AddDays(5), RatingIds = [b] }, Ct);
        Assert.Equal([b], _fx.Store.GetImport("JE", "abc:movie:603")!.RatingIds);
        Assert.Equal(1, _fx.Count("ImportLedgerRows"));
        Assert.Null(_fx.Store.GetImport("JE", "other"));
    }

    [Fact]
    public async Task MetaValues_RoundTrip()
    {
        Assert.Null(_fx.Store.GetMeta("LastMoveSeq"));
        await _fx.Store.SetMetaAsync("LastMoveSeq", "41", Ct);
        await _fx.Store.SetMetaAsync("LastMoveSeq", "42", Ct);

        Assert.Equal("42", _fx.Store.GetMeta("LastMoveSeq"));
        Assert.Equal(1, _fx.Count("Meta"));
    }

    [Fact]
    public async Task TheGeneration_RisesWithEveryCommittedWrite_AndNotWithReadsOrRefusedWrites()
    {
        var start = _fx.Store.Generation;
        await _fx.Store.UpsertAsync(StoreFixture.Movie(Alex, Matrix), Ct);
        await _fx.Store.SetMetaAsync("k", "v", Ct);
        _fx.Store.GetSummaryRows([Matrix]);

        Assert.Equal(start + 2, _fx.Store.Generation);

        await Assert.ThrowsAsync<PluginException>(() => _fx.Store.UpsertAsync(StoreFixture.Movie(Alex, Matrix), Ct));
        Assert.Equal(start + 2, _fx.Store.Generation);
    }

    [Fact]
    public async Task Counts_AreWhatTheTablesHold()
    {
        var gone = StoreFixture.Movie(Sam, Other, 4);
        await _fx.Store.UpsertManyAsync([StoreFixture.Movie(Alex, Matrix, 7, "x"), StoreFixture.Movie(Sam, Matrix, 5), gone, StoreFixture.Movie(Alex, Other, 2) with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Missing }], Ct);
        await _fx.Store.DeleteAsync(gone.Id, RatingValues.DeletedBy.Moderator, StoreFixture.T0, Ct);
        await _fx.Store.AppendModerationLogAsync(new ModerationLogRecord { AtUtc = StoreFixture.T0, Action = RatingValues.ModerationAction.RemoveEntry }, Ct);

        Assert.Equal(new RatingsStoreCounts(2, 1, 1, 2, 1, 1, 0), _fx.Store.Count());
    }

    [Fact]
    public async Task Counts_TheDetachedJeRatingsWhoseTmdbIdKeepsASeasonOrEpisodeSuffix_AsOnly0321KeptThem()
    {
        // The 0.3.21 sanity check: 0.3.21 kept JE 12.14's season and episode reviews detached under JE's whole TMDb
        // key ("1399:s1:e2") as the series' id, which never attaches; since 0.3.22 the series' id alone, with the numbers.
        Assert.Equal(0, _fx.Store.Count().DetachedJeWithKeySuffix);
        RatingRecord Je(string kind, Dictionary<string, string>? ids, Dictionary<string, string>? seriesIds, int? season = null, int? episode = null, string origin = RatingValues.Origin.JellyfinEnhanced)
            => StoreFixture.Movie(Alex, Guid.NewGuid()) with
            {
                Origin = origin,
                DetachedUtc = StoreFixture.T0,
                DetachReason = RatingValues.DetachReason.Missing,
                Identity = new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = kind, Name = "TMDb 1399", ProviderIds = ids ?? [], SeriesProviderIds = seriesIds, Season = season, Episode = episode }
            };

        await _fx.Store.UpsertManyAsync(
        [
            Je("Series", new() { ["Tmdb"] = "1399:s1:e2" }, null), // 0.3.21's episode
            Je("Series", new() { ["tmdb"] = "1399:S2" }, null), // 0.3.21's season, any case
            Je("Episode", null, new() { ["Tmdb"] = "1399:s1:e3" }, 1, 3), // in the series' ids
            Je("Episode", null, new() { ["Tmdb"] = "1399" }, 1, 2), // as 0.3.22 keeps it
            Je("Season", null, new() { ["Tmdb"] = "1399" }, 0), // Specials
            Je("Movie", new() { ["Tmdb"] = "603", ["Imdb"] = "tt0133093:s1" }, null), // only the Tmdb id counts
            Je("Series", new() { ["Tmdb"] = "1399:s1:e9" }, null, origin: RatingValues.Origin.User), // not JE's
            StoreFixture.Movie(Alex, Matrix, tmdb: "1399:s1") with { Origin = RatingValues.Origin.JellyfinEnhanced }, // attached
        ], Ct);

        Assert.Equal(3, _fx.Store.Count().DetachedJeWithKeySuffix);
        Assert.Equal(7, _fx.Store.Count().Detached);
        using var services = new ServiceCollection().AddSingleton<IRatingsStore>(_fx.Store).BuildServiceProvider();
        Assert.Equal(3L, PluginServiceRegistrator.DescribeRecords(services)["DetachedJeWithKeySuffix"]);
    }

    // --------------------------------------------------------------------------------------------------------- validation

    public static TheoryData<string> InvalidRecords => new()
    {
        "stars 0", "stars 11", "neither stars nor text", "empty text", "text over 20000", "unknown origin", "unknown detach reason",
        "detached without a reason", "a reason without a time", "no id", "no user", "no item", "no kind", "negative edit count",
        "a NUL in the text", "a NUL first in the text"
    };

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public async Task Invalid_Records_AreRefused_BeforeAnythingIsWritten(string what)
    {
        var good = StoreFixture.Movie(Alex, Matrix, 7, "text");
        var bad = what switch
        {
            "stars 0" => good with { Stars2 = 0 },
            "stars 11" => good with { Stars2 = 11 },
            "neither stars nor text" => good with { Stars2 = null, ReviewText = null },
            "empty text" => good with { ReviewText = string.Empty },
            "text over 20000" => good with { ReviewText = new string('x', 20001) },
            "unknown origin" => good with { Origin = "Trakt" },
            "unknown detach reason" => good with { DetachedUtc = StoreFixture.T0, DetachReason = "Lost" },
            "detached without a reason" => good with { DetachedUtc = StoreFixture.T0 },
            "a reason without a time" => good with { DetachReason = RatingValues.DetachReason.Removed },
            "no id" => good with { Id = Guid.Empty },
            "no user" => good with { UserId = Guid.Empty },
            "no item" => good with { Identity = good.Identity with { ItemId = Guid.Empty } },
            "no kind" => good with { Identity = good.Identity with { Kind = string.Empty } },
            "negative edit count" => good with { EditCount = -1 },
            "a NUL in the text" => good with { ReviewText = "a\0b" },
            "a NUL first in the text" => good with { ReviewText = "\0abc" },
            _ => throw new ArgumentException(what)
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.UpsertAsync(bad, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.UpsertManyAsync([StoreFixture.Movie(Sam, Other), bad], Ct));
        Assert.Equal(0, _fx.Count("Ratings"));
    }

    [Fact]
    public async Task UnknownEnumValues_AreRefused_OnEveryWrite()
    {
        var record = StoreFixture.Movie(Alex, Matrix);
        await _fx.Store.UpsertAsync(record, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.DeleteAsync(record.Id, "Robot", StoreFixture.T0, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _fx.Store.AppendModerationLogAsync(new ModerationLogRecord { AtUtc = StoreFixture.T0, Action = "Shadowban" }, Ct));

        Assert.NotNull(_fx.Store.Get(record.Id));
        Assert.Equal(0, _fx.Count("Tombstones"));
        Assert.Equal(0, _fx.Count("ModerationLog"));
    }

    [Fact]
    public async Task EveryKnownValue_IsAccepted()
    {
        foreach (var origin in RatingValues.Origin.All)
        {
            await _fx.Store.UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), Matrix) with { Origin = origin }, Ct);
        }

        foreach (var reason in RatingValues.DetachReason.All)
        {
            await _fx.Store.UpsertAsync(StoreFixture.Movie(Alex, Matrix) with { DetachedUtc = StoreFixture.T0, DetachReason = reason }, Ct);
        }

        foreach (var action in RatingValues.ModerationAction.All)
        {
            await _fx.Store.AppendModerationLogAsync(new ModerationLogRecord { AtUtc = StoreFixture.T0, Action = action }, Ct);
        }

        foreach (var by in RatingValues.DeletedBy.All)
        {
            var record = StoreFixture.Movie(Guid.NewGuid(), Other);
            await _fx.Store.UpsertAsync(record, Ct);
            Assert.True(await _fx.Store.DeleteAsync(record.Id, by, StoreFixture.T0, Ct));
        }

        Assert.Equal(RatingValues.Origin.All.Count + RatingValues.DetachReason.All.Count, _fx.Count("Ratings"));
        Assert.Equal(16, RatingValues.ModerationAction.All.Count);
        Assert.Equal(RatingValues.DeletedBy.All.Count, _fx.Count("Tombstones"));
    }

    // --------------------------------------------------------------------------- only the unique index is the member's 409

    [Fact]
    public async Task AConstraintOtherThanTheSecondRating_IsAFault_NeverTheMembersConflict()
    {
        // Past the store's validation (RatingsSql directly): a CHECK the schema has. It used to be reported as "This member
        // already has a rating of this item." (409), as every constraint was.
        var bad = StoreFixture.Movie(Alex, Matrix) with { Stars2 = 11 };
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _fx.Database.WriteAsync(
            (c, t) =>
            {
                RatingsSql.Upsert(c, t, bad);
                return 0;
            },
            Ct));

        Assert.Contains("CHECK constraint failed", refused.Message, StringComparison.Ordinal);
        Assert.Contains(bad.Id.ToString("N"), refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("already has a rating", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, _fx.Count("Ratings"));
    }

    [Fact]
    public async Task AWriteTheDatabaseRefuses_ThroughTheStore_IsAnErrorLoggedAndA500_NotA409()
    {
        // A refusal the store's validation can't foresee (here a trigger standing in for a future constraint): a 500-class
        // InvalidOperationException, logged once at Error with no value of the row.
        await _fx.Database.WriteAsync(
            (c, t) =>
            {
                StoreFixture.Execute(c, t, "CREATE TRIGGER RefuseAll BEFORE INSERT ON Ratings BEGIN SELECT RAISE(ABORT, 'refused by the test'); END;");
                return 0;
            },
            Ct);

        var record = StoreFixture.Movie(Alex, Matrix, 7, "A review the log must not hold.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _fx.Store.UpsertAsync(record, Ct));

        var error = Assert.Single(_fx.StoreLogger.At(LogLevel.Error));
        Assert.DoesNotContain("A review the log must not hold.", error.Message + error.Exception, StringComparison.Ordinal);
        Assert.Equal(0, _fx.Count("Ratings"));
    }

    // ------------------------------------------------------------------- provider ids the store didn't write, read tolerantly

    [Fact]
    public async Task ProviderIdsTheStoreDidntWrite_AreReadTolerantly_WithOneWarningPerRowAndColumn()
    {
        var numeric = StoreFixture.Movie(Alex, Matrix);
        var broken = StoreFixture.Movie(Sam, Matrix);
        var episode = StoreFixture.Episode(Alex, Other, Guid.NewGuid(), Guid.NewGuid());
        await _fx.Store.UpsertManyAsync([numeric, broken, episode], Ct);
        await _fx.Database.WriteAsync(
            (c, t) =>
            {
                StoreFixture.Execute(c, t, "UPDATE Ratings SET ProviderIds = '{\"Tmdb\": 603, \"Imdb\": \"tt0133093\", \"Tvdb\": null}' WHERE Id = '" + numeric.Id.ToString("N") + "';");
                StoreFixture.Execute(c, t, "UPDATE Ratings SET ProviderIds = 'not json at all' WHERE Id = '" + broken.Id.ToString("N") + "';");
                StoreFixture.Execute(c, t, "UPDATE Ratings SET SeriesProviderIds = '[1, 2]' WHERE Id = '" + episode.Id.ToString("N") + "';");
                return 0;
            },
            Ct);

        // Every read of those rows works (each used to be a raw JsonException, a 500), as often as they are read.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(new Dictionary<string, string> { ["Imdb"] = "tt0133093", ["Tmdb"] = "603" }, _fx.Store.Get(numeric.Id)!.Identity.ProviderIds);
            Assert.Empty(_fx.Store.Get(broken.Id)!.Identity.ProviderIds);
            Assert.Equal(2, _fx.Store.GetAttachedForItem(Matrix).Count);
            Assert.True(_fx.Store.GetAttached(Alex, Other)!.Identity.SeriesProviderIds is null or { Count: 0 });
        }

        var warnings = _fx.StoreLogger.At(LogLevel.Warning).Select(w => w.Message).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains(numeric.Id.ToString(), StringComparison.Ordinal) && w.Contains("ProviderIds has a number for Tmdb", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(broken.Id.ToString(), StringComparison.Ordinal) && w.Contains("isn't valid JSON", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(episode.Id.ToString(), StringComparison.Ordinal) && w.Contains("SeriesProviderIds is JSON but not an object", StringComparison.Ordinal));

        // The next save writes the map back as the store writes it.
        await _fx.Store.UpsertAsync(_fx.Store.Get(numeric.Id)!, Ct);
        Assert.Equal("{\"Imdb\":\"tt0133093\",\"Tmdb\":\"603\"}", _fx.Database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT ProviderIds FROM Ratings WHERE Id = '" + numeric.Id.ToString("N") + "';";
            return (string)command.ExecuteScalar()!;
        }));
    }

    [Theory]
    [InlineData("{\"Tmdb\":\"603\"}", "Tmdb=603", false)]
    [InlineData("{\"Tmdb\":603}", "Tmdb=603", true)]
    [InlineData("{\"Tmdb\":6.03e2}", "Tmdb=6.03e2", true)]
    [InlineData("{\"Tmdb\":\"603\",\"Bad\":{\"x\":1},\"Also\":[1],\"Null\":null,\"Flag\":true}", "Tmdb=603", true)]
    [InlineData("{}", "", false)]
    [InlineData("[\"603\"]", "", true)]
    [InlineData("\"603\"", "", true)]
    [InlineData("{\"Tmdb\":", "", true)]
    [InlineData("", "", true)]
    public void ProviderIdsJson_KeepsWhatItCanRead(string json, string expected, bool problem)
    {
        var ids = ProviderIdsJson.Read(json, out var found);

        Assert.Equal(expected, string.Join(';', ids.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)));
        Assert.Equal(problem, found is not null);
    }
}
