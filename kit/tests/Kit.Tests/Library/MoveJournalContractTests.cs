using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// The journal contract: <c>kit/testdata/move-journal/moves-v1.jsonl</c> holds every line
/// shape Library Maintenance writes (primary, version, part, extra, correction, move back) plus lines a later version might
/// write; the reader returns every Committed v1 line and chains them. Library Maintenance's writer tests are to produce
/// the same shapes.
/// </summary>
public sealed class MoveJournalContractTests
{
    private static readonly Guid MovieOld = Guid.Parse("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1");
    private static readonly Guid MoviePredicted = Guid.Parse("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2");
    private static readonly Guid MovieActual = Guid.Parse("c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3");

    private static string Fixture => Path.Combine(TestPaths.KitRoot, "testdata", "move-journal", "moves-v1.jsonl");

    [Fact]
    public void EveryLineShape_IsRead()
    {
        var journal = new MoveJournalReader(Fixture, new ListLogger()).Read();

        Assert.Equal(MoveJournalStatus.Ok, journal.Status);
        Assert.Equal(Guid.Parse("3f2a9c1e7b5d4e6f8a0b1c2d3e4f5a6b"), journal.JournalId);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), journal.CreatedUtc);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 7, 9 }, journal.Lines.Select(l => l.Seq));
        Assert.Equal(new[] { "Primary", "Version", "Part", "Extra", "Primary", "Primary", "Primary" }, journal.Lines.Select(l => l.Role));
        Assert.Equal(new[] { "Movie", "Movie", "Movie", "Trailer", "Movie", "Movie", "Episode" }, journal.Lines.Select(l => l.Kind));
        Assert.Equal(new[] { false, false, false, false, true, false, false }, journal.Lines.Select(l => l.Correction));
        Assert.All(journal.Lines, l => Assert.NotNull(l.JobId));
        Assert.All(journal.Lines, l => Assert.False(string.IsNullOrEmpty(l.OldPath) || string.IsNullOrEmpty(l.NewPath)));
        Assert.Equal(@"Q:\Video\Cult\Heat (1995)\Heat (1995) - 1080p.mkv", journal.Lines[0].NewPath);

        // Seq 6 ("Planned") and Seq 8 (V 2) are ignored; only the v1 line counts towards LastSeq.
        Assert.Equal(2, journal.IgnoredLines);
        Assert.Equal(0, journal.InvalidLines);
        Assert.False(journal.TruncatedTail);
        Assert.Equal(9, journal.LastSeq);
        Assert.Null(journal.Problem);
    }

    [Fact]
    public void ThePrimaryChain_GoesThroughTheCorrectionAndTheMoveBack()
    {
        var journal = new MoveJournalReader(Fixture, new ListLogger()).Read();

        // a1 → b2 (predicted) → c3 (correction) → a1 (Move back); the Planned line from c3 is not followed.
        var chain = journal.Chain(MovieOld);
        Assert.Equal(new long[] { 1, 5, 7 }, chain.Steps.Select(s => s.Seq));
        Assert.Equal(MovieOld, chain.EndId);
        Assert.True(chain.Revisits);

        Assert.Equal(MovieOld, journal.Chain(MoviePredicted).EndId);
        Assert.Equal(MovieOld, journal.Chain(MovieActual).EndId);

        // A consumer that caught up to Seq 5 before the Move back follows only line 7.
        Assert.Equal(new long[] { 7 }, journal.Chain(MovieActual, afterSeq: 5).Steps.Select(s => s.Seq));
    }
}
