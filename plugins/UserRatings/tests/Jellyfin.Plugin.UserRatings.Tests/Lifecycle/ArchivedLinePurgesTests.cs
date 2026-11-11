using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// When the archive may hold a removed review's lines: an archive that can't be
/// reached, and one with lines it can't read, may hold any rating's, so the removal records the id and its lines go once
/// the archive can be read; a clean archive that never held the rating costs no rewrite.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ArchivedLinePurgesTests : IDisposable
{
    private readonly StoreFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task AnArchiveThatCantBeReached_IsUnknown_SoTheRemovedReviewsIdIsRecorded_WithAWarning()
    {
        var purges = new ArchivedLinePurges(_fx.Store, _fx.Logger, () => throw new IOException("Synthetic: the archive's folder can't be reached."));
        var id = Guid.NewGuid();

        var view = purges.Look();

        Assert.True(view.IsUnknown);
        Assert.True(view.Mentions(id));
        Assert.Equal(1, await _fx.Store.WriteAsync(w => ArchivedLinePurges.Record(w, view, [id]), Ct));
        Assert.Equal([id], ArchivedLinePurges.PendingOf(_fx.Store.GetMeta(ArchivedLinePurges.Key)));
        Assert.Single(_fx.Logger.At(LogLevel.Warning), e => e.Message.Contains("can't be reached", StringComparison.Ordinal));

        // No lifecycle (no archive at all) records nothing.
        var none = new ArchivedLinePurges(_fx.Store, _fx.Logger).Look();
        Assert.False(none.IsUnknown);
        Assert.False(none.Mentions(id));
    }

    [Theory]
    [InlineData("an invalid line")]
    [InlineData("a truncated last line")]
    [InlineData("a problem reading it")]
    public void AnArchiveWithLinesItCantRead_MayHoldAnyRating(string what)
    {
        var contents = what switch
        {
            "an invalid line" => new ArchiveContents { Exists = true, InvalidLines = 1 },
            "a truncated last line" => new ArchiveContents { Exists = true, TruncatedTail = true },
            _ => new ArchiveContents { Exists = true, Problem = "Synthetic: it couldn't be read." }
        };

        Assert.True(new ArchivedLinePurges.ArchiveView(contents, false).Mentions(Guid.NewGuid()), what);
    }

    [Fact]
    public void ACleanArchive_MentionsOnlyTheRatingsItHolds_AsARecordOrAsHistory()
    {
        var record = Guid.NewGuid();
        var history = Guid.NewGuid();
        var contents = new ArchiveContents
        {
            Exists = true,
            Records = [new ArchivedRecord { Id = record.ToString("D"), UserId = Guid.NewGuid(), Identity = new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "Synthetic" }, DetachedUtc = StoreFixture.T0, Record = [] }],
            HistoryIds = new[] { history.ToString("D") }.ToHashSet(StringComparer.Ordinal)
        };
        var view = new ArchivedLinePurges.ArchiveView(contents, false);

        Assert.True(view.Mentions(record));
        Assert.True(view.Mentions(history));
        Assert.False(view.Mentions(Guid.NewGuid()));
        Assert.False(new ArchivedLinePurges.ArchiveView(ArchiveContents.Empty, false).Mentions(record));
    }
}
