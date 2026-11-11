using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="DetachedArchive"/>: the append-only JSON Lines file, its folding
/// (archived, reattached, superseded, checked), forward compatibility and torn lines, the round-robin reattach order, and
/// <i>Purge</i> as its only rewrite, after a copy.
/// </summary>
public sealed class DetachedArchiveTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _directory = new();
    private readonly ListLogger _logger = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Monday));
    private readonly DetachedArchive _archive;

    public DetachedArchiveTests()
    {
        _archive = new DetachedArchive(_directory.Combine("archive"), "bookmarks", _logger, _clock);
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheFile_IsArchiveStoreArchivedJsonl_AndMissingReadsEmpty()
    {
        Assert.Equal(Path.Combine(_directory.Path, "archive", "bookmarks-archived.jsonl"), _archive.FilePath);
        Assert.Same(ArchiveContents.Empty, _archive.Read());
        Assert.Empty(_archive.NextToCheck(10));
    }

    [Fact]
    public void Append_WritesOneLinePerRecord_WithSeqAndTime_AndReadsThemBack()
    {
        var written = _archive.Append([Record("a", "first"), Record("b", "second")]);

        Assert.Equal(new long[] { 1, 2 }, written.Select(r => r.Seq));
        Assert.All(written, r => Assert.Equal(Monday, r.ArchivedUtc));
        var lines = File.ReadAllLines(_archive.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("{\"V\":1,\"Op\":\"Archived\",\"Seq\":1,", lines[0], StringComparison.Ordinal);

        var contents = _archive.Read();
        Assert.Equal(new[] { "a", "b" }, contents.Records.Select(r => r.Id));
        Assert.Equal("first", contents.Records[0].Record["Note"]!.GetValue<string>());
        Assert.Equal("Heat", contents.Records[0].Identity.Name);
        Assert.Equal(Monday.AddDays(-100), contents.Records[0].DetachedUtc);
        Assert.Equal(3, contents.NextSeq);
    }

    [Fact]
    public void ReattachedAndSuperseded_TakeRecordsOut_AndALaterArchivedLineReplacesAnEarlierOne()
    {
        _archive.Append([Record("a", "first"), Record("b", "b"), Record("c", "c")]);
        _archive.MarkReattached(["a"]);
        _archive.MarkSuperseded(["b"]);
        _archive.Append([Record("a", "archived again")]);

        var contents = _archive.Read();

        Assert.Equal(new[] { "c", "a" }, contents.Records.Select(r => r.Id));
        Assert.Equal("archived again", contents.Records[1].Record["Note"]!.GetValue<string>());
        Assert.Equal(7, contents.NextSeq);

        // The ids of the lines that are no longer records (put back, superseded, archived again).
        Assert.Equal(new[] { "a", "b" }, contents.HistoryIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HistoryIds_NameOnlyTheLinesNoLongerRecords_AndAPurgeOfHistoryEmptiesThem()
    {
        _archive.Append([Record("a", "first"), Record("b", "b")]);
        Assert.Empty(_archive.Read().HistoryIds);

        _archive.MarkReattached(["a", "unknown"]);
        Assert.Equal(new[] { "a" }, _archive.Read().HistoryIds);
        Assert.Equal(new[] { "b" }, _archive.Read().Records.Select(r => r.Id));

        _archive.Purge(r => r.Id == "a", snapshotDirectory: null, includeHistory: true);

        Assert.Empty(_archive.Read().HistoryIds);
        Assert.Equal(new[] { "b" }, _archive.Read().Records.Select(r => r.Id));
        Assert.DoesNotContain("first", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);
        Assert.Empty(ArchiveContents.Empty.HistoryIds);
    }

    [Fact]
    public void Checked_MovesTheRoundRobin_LeastRecentlyCheckedFirst()
    {
        var written = _archive.Append(Enumerable.Range(0, 5).Select(i => Record("r" + i, null)).ToList());

        Assert.Equal(new[] { "r0", "r1" }, _archive.NextToCheck(2).Select(r => r.Id));
        _archive.MarkChecked(written[1].Seq);
        Assert.Equal(new[] { "r2", "r3", "r4" }, _archive.NextToCheck(3).Select(r => r.Id));
        _archive.MarkChecked(written[4].Seq);
        Assert.Equal(new[] { "r0", "r1", "r2" }, _archive.NextToCheck(3).Select(r => r.Id));
        Assert.Equal(written[4].Seq, _archive.Read().LastCheckedSeq);
    }

    // A run that leaves off where the last one did (one record that never comes back) writes no line, so the file
    // doesn't grow daily.
    [Fact]
    public void Checked_WhereItLeftOffAlready_WritesNothing()
    {
        var written = _archive.Append([Record("only", null)]);
        _archive.MarkChecked(written[0].Seq);
        var lines = File.ReadAllLines(_archive.FilePath).Length;

        for (var day = 0; day < 3; day++)
        {
            _archive.MarkChecked(_archive.NextToCheck(10)[^1].Seq);
        }

        Assert.Equal(lines, File.ReadAllLines(_archive.FilePath).Length);
        Assert.Equal(written[0].Seq, _archive.Read().LastCheckedSeq);
    }

    [Fact]
    public void LinesOfAnotherVersionOrOperation_AreIgnored_ATornLastLineToo_AndBrokenLinesCounted()
    {
        _archive.Append([Record("a", null)]);
        File.AppendAllText(_archive.FilePath, "{\"V\":2,\"Op\":\"Archived\",\"Seq\":2,\"Id\":\"x\"}\n{\"V\":1,\"Op\":\"Compacted\",\"Seq\":3}\nnot json\n{\"V\":1,\"Op\":\"Reattached\",\"Seq\":5}\n{\"V\":1,\"Op\":\"Arch");

        var contents = _archive.Read();

        Assert.Equal(new[] { "a" }, contents.Records.Select(r => r.Id));
        Assert.Equal(2, contents.IgnoredLines);
        Assert.Equal(2, contents.InvalidLines);
        Assert.True(contents.TruncatedTail);
        Assert.Equal(6, contents.NextSeq);
        Assert.Single(_logger.At(Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    [Fact]
    public void AnAppendAfterATornLine_StartsOnALineOfItsOwn()
    {
        _archive.Append([Record("a", null)]);
        File.AppendAllText(_archive.FilePath, "{\"V\":1,\"Op\":\"Arch");

        _archive.Append([Record("b", null)]);

        Assert.Equal(new[] { "a", "b" }, _archive.Read().Records.Select(r => r.Id));
        Assert.Equal(3, File.ReadAllLines(_archive.FilePath).Length);
    }

    [Fact]
    public void AByteOrderMarkAndCrLf_AreRead()
    {
        _archive.Append([Record("a", null)]);
        var text = File.ReadAllText(_archive.FilePath).Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(_archive.FilePath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(new[] { "a" }, _archive.Read().Records.Select(r => r.Id));
    }

    [Fact]
    public void Purge_RewritesTheFileWithoutTheRecords_AfterACopyInThePreOpClass()
    {
        var snapshots = _directory.Combine("snapshots");
        _archive.Append([Record("a", "secret note"), Record("b", "kept")]);
        _archive.MarkChecked(1);

        var (purged, copy) = _archive.Purge(r => r.Id == "a", snapshots);

        Assert.Equal(1, purged);
        Assert.Equal(new[] { "b" }, _archive.Read().Records.Select(r => r.Id));
        Assert.Equal(1, _archive.Read().LastCheckedSeq);
        Assert.DoesNotContain("secret note", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);
        Assert.False(File.Exists(_archive.FilePath + ".tmp"));
        Assert.NotNull(copy);
        Assert.StartsWith("bookmarks-archived-purge-", copy, StringComparison.Ordinal);
        Assert.Contains("secret note", File.ReadAllText(Path.Combine(snapshots, "pre-op", copy!)), StringComparison.Ordinal);

        Assert.Equal((0, (string?)null), _archive.Purge(r => r.Id == "nothing", snapshots));
    }

    [Fact]
    public void Purge_IncludeHistory_RewritesForAnEarlierLineItPicks_NamesItsCopy_AndCountsOnlyRecords()
    {
        // Opt-in: a deleted user's record archived, reattached (live again) and archived again under another id leaves
        // its text in the earlier lines; a purge of that user's records takes them too.
        var snapshots = _directory.Combine("snapshots");
        _archive.Append([Record("a", "secret note"), Record("b", "kept")]);
        _archive.MarkReattached(["a"]);
        Assert.Contains("secret note", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);

        // Without includeHistory nothing matches a record: no rewrite, no copy.
        Assert.Equal((0, (string?)null), _archive.Purge(r => r.Id == "a", snapshots));
        Assert.Contains("secret note", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);

        var (purged, copy) = _archive.Purge(r => r.Id == "a", snapshots, SnapshotOperations.UserDeleted, includeHistory: true);

        Assert.Equal(0, purged);
        Assert.StartsWith("bookmarks-archived-user-deleted-", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("secret note", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);
        Assert.Equal(new[] { "b" }, _archive.Read().Records.Select(r => r.Id));

        // An earlier line replaced by a later one of the same id (archived again) is history too, and nothing that matches
        // neither a record nor a line rewrites the file.
        _archive.Append([Record("b", "second text")]);
        Assert.Equal((0, (string?)null), _archive.Purge(r => r.Id == "nothing", snapshots, SnapshotOperations.UserDeleted, includeHistory: true));
        var (none, again) = _archive.Purge(r => r.Id == "b" && r.Record["Note"]!.GetValue<string>() == "kept", snapshots, SnapshotOperations.UserDeleted, includeHistory: true);
        Assert.Equal(0, none);
        Assert.NotNull(again);
        Assert.DoesNotContain("\"kept\"", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);
        Assert.Equal("second text", _archive.Read().Records.Single().Record["Note"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => _archive.Purge(_ => true, snapshots, "no-such-operation"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APurge_DropsTornLines_WithTheirText_AndAPurgeOfHistoryRewritesForThemAlone(bool tornInTheMiddle)
    {
        // A line torn by a crash mid-write can't be parsed, so whose text it holds can't be told; any rewrite drops it,
        // and a purge of a user's records with includeHistory rewrites for it even when nothing else matches.
        var snapshots = _directory.Combine("snapshots");
        _archive.Append([Record("a", "kept")]);
        File.AppendAllText(_archive.FilePath, "{\"V\":1,\"Op\":\"Archived\",\"Seq\":2,\"Id\":\"torn\",\"Record\":{\"Note\":\"torn secret of a deleted us");
        if (tornInTheMiddle)
        {
            _archive.Append([Record("b", "kept too")]);
        }

        var before = _archive.Read();
        Assert.Equal(tornInTheMiddle ? (1, false) : (0, true), (before.InvalidLines, before.TruncatedTail));
        Assert.Contains("torn secret", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);

        // Without includeHistory and nothing to purge: no rewrite.
        Assert.Equal((0, (string?)null), _archive.Purge(r => r.Id == "nothing", snapshots));
        Assert.Contains("torn secret", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);

        var (purged, copy) = _archive.Purge(r => r.Id == "nothing", snapshots, SnapshotOperations.UserDeleted, includeHistory: true);

        Assert.Equal(0, purged);
        Assert.NotNull(copy);
        Assert.DoesNotContain("torn secret", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);
        var after = _archive.Read();
        Assert.Equal(tornInTheMiddle ? ["a", "b"] : ["a"], after.Records.Select(r => r.Id).ToArray());
        Assert.Equal((0, false), (after.InvalidLines, after.TruncatedTail));
        Assert.Contains(_logger.At(Microsoft.Extensions.Logging.LogLevel.Warning), e => e.Message.Contains("dropped 1 lines that couldn't be read", StringComparison.Ordinal));

        // A purge that matches a record drops a torn line too.
        File.AppendAllText(_archive.FilePath, "{\"V\":1,\"Op\":\"Archived\",\"Seq\":9,\"Record\":{\"Note\":\"another torn secret");
        Assert.Equal(1, _archive.Purge(r => r.Id == "a", snapshots).Purged);
        Assert.DoesNotContain("torn secret", File.ReadAllText(_archive.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOwnersReason_IsWrittenOnItsRecordsLine_AndReadBack_AndADetachedRecordsLineHasNone()
    {
        // A record its owner archived for a reason of its own (Bookmarks' ClipMissing) carries it; a detached record's
        // line is as before (no Reason), so a line written before reasons existed reads as a detached record.
        _archive.Append([Record("detached", null), Record("missing", "kept clip") with { Reason = "ClipMissing" }]);

        var lines = File.ReadAllLines(_archive.FilePath);
        Assert.DoesNotContain("Reason", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(",\"Reason\":\"ClipMissing\"}", lines[1], StringComparison.Ordinal);
        var contents = _archive.Read();
        Assert.Equal(new string?[] { null, "ClipMissing" }, contents.Records.Select(r => r.Reason));

        // An empty reason (a hand edit) is no reason.
        File.AppendAllText(_archive.FilePath, "{\"V\":1,\"Op\":\"Archived\",\"Seq\":3,\"Utc\":\"2026-10-05T12:00:00Z\",\"Id\":\"blank\",\"Identity\":{},\"DetachedUtc\":\"2026-10-01T00:00:00Z\",\"Record\":{},\"Reason\":\"\"}\n");
        Assert.Null(_archive.Read().Records.Single(r => r.Id == "blank").Reason);
    }

    [Fact]
    public void Purge_WithTheClockBehindTheOtherCopies_KeepsItsCopy_AndThoseDatedAhead()
    {
        // The JSON store hardening: the purge's copy is the only one of the records about to go, but its
        // rotation was given neither now nor the copy to spare, so with the clock behind the five copies taken before it was
        // the oldest by name and deleted at once.
        var snapshots = _directory.Combine("snapshots");
        var preOp = Path.Combine(snapshots, "pre-op");
        Directory.CreateDirectory(preOp);
        for (var day = 10; day < 15; day++)
        {
            File.WriteAllText(Path.Combine(preOp, $"bookmarks-archived-purge-202610{day}-120000.jsonl"), "{}\n");
        }

        _archive.Append([Record("a", "only copy"), Record("b", "kept"), Record("c", "second only copy"), Record("d", "third only copy")]);
        var (_, first) = _archive.Purge(r => r.Id == "a", snapshots);

        Assert.True(File.Exists(Path.Combine(preOp, first!)), first + " was deleted by its own rotation.");
        Assert.Contains("only copy", File.ReadAllText(Path.Combine(preOp, first!)), StringComparison.Ordinal);
        Assert.Equal(6, Directory.GetFiles(preOp).Length);

        // A second purge: the copies dated more than a day ahead neither count nor go, so the first purge's copy stays too.
        var (_, second) = _archive.Purge(r => r.Id == "c", snapshots);
        Assert.True(File.Exists(Path.Combine(preOp, second!)) && File.Exists(Path.Combine(preOp, first!)), "A purge's copy was rotated out.");
        Assert.Equal(7, Directory.GetFiles(preOp).Length);

        // Behind by less than a day, the others count, and the one just made is the oldest by name: it is never the one to go.
        var near = _directory.Combine("snapshots-near");
        var nearPreOp = Path.Combine(near, "pre-op");
        Directory.CreateDirectory(nearPreOp);
        for (var hour = 13; hour < 19; hour++)
        {
            File.WriteAllText(Path.Combine(nearPreOp, $"bookmarks-archived-purge-20261005-{hour}0000.jsonl"), "{}\n");
        }

        var (_, third) = _archive.Purge(r => r.Id == "d", near);
        Assert.True(File.Exists(Path.Combine(nearPreOp, third!)), third + " was deleted by its own rotation.");
        Assert.Contains("third only copy", File.ReadAllText(Path.Combine(nearPreOp, third!)), StringComparison.Ordinal);
        Assert.Equal(6, Directory.GetFiles(nearPreOp).Length);
        Assert.False(File.Exists(Path.Combine(nearPreOp, "bookmarks-archived-purge-20261005-130000.jsonl")));
    }

    [Fact]
    public void Purge_KeepsTheReasonsOfTheRecordsItKeeps()
    {
        _archive.Append([Record("a", null) with { Reason = "ClipMissing" }, Record("b", null), Record("c", null) with { Reason = "ClipMissing" }]);

        Assert.Equal(1, _archive.Purge(r => r.Id == "a", snapshotDirectory: null).Purged);

        Assert.Equal(new[] { ("b", (string?)null), ("c", "ClipMissing") }, _archive.Read().Records.Select(r => (r.Id, r.Reason)));
    }

    [Fact]
    public void NextToCheck_GoesRoundTheRecordsItIsGiven_OnlyThem()
    {
        // The reattach takes the records without a reason: those with one neither come up nor use up its run.
        var written = _archive.Append([Record("r0", null), Record("m1", null) with { Reason = "ClipMissing" }, Record("r2", null), Record("m3", null) with { Reason = "ClipMissing" }, Record("r4", null)]);
        static bool Detached(ArchivedRecord r) => r.Reason is null;

        Assert.Equal(new[] { "r0", "r2" }, _archive.NextToCheck(2, Detached).Select(r => r.Id));
        _archive.MarkChecked(written[2].Seq);
        Assert.Equal(new[] { "r4", "r0", "r2" }, _archive.NextToCheck(5, Detached).Select(r => r.Id));
        Assert.Equal(new[] { "m3", "r4", "r0" }, _archive.NextToCheck(3).Select(r => r.Id));
    }

    [Fact]
    public void AnArchiveThatCantBeRead_RefusesToWrite()
    {
        _archive.Append([Record("a", null)]);
        using (new FileStream(_archive.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.NotNull(_archive.Read().Problem);
            Assert.Throws<IOException>(() => _archive.Append([Record("b", null)]));
        }

        Assert.Equal(new[] { "a" }, _archive.Read().Records.Select(r => r.Id));
    }

    [Fact]
    public void CopyToSnapshots_CopiesTheArchiveAsItIs_AnEmptyOneWithoutAFile_AndRotatesKeepingIt()
    {
        var snapshots = _directory.Combine("snapshots");
        var none = _archive.CopyToSnapshots(snapshots, SnapshotOperations.Import);
        Assert.StartsWith("bookmarks-archived-import-", none, StringComparison.Ordinal);
        Assert.Equal(0, new FileInfo(Path.Combine(snapshots, "pre-op", none)).Length);

        _archive.Append([Record("a", "then")]);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var copy = _archive.CopyToSnapshots(snapshots, SnapshotOperations.Import);
        Assert.Equal(File.ReadAllBytes(_archive.FilePath), File.ReadAllBytes(Path.Combine(snapshots, "pre-op", copy)));
        Assert.Throws<ArgumentException>(() => _archive.CopyToSnapshots(snapshots, "nonsense"));
    }

    [Fact]
    public void RestoreFrom_PutsTheCopyBack_KeepingTheArchiveAsARestoreCopy_AndAFailureLeavesTheArchiveAsItWas()
    {
        var snapshots = _directory.Combine("snapshots");
        _archive.Append([Record("a", "then")]);
        var copy = Path.Combine(snapshots, "pre-op", _archive.CopyToSnapshots(snapshots, SnapshotOperations.Import));
        _archive.Append([Record("b", "since")]);
        Assert.Equal(2, _archive.Read().Records.Count);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var saved = _archive.RestoreFrom(copy, snapshots);
        Assert.Equal(new[] { "a" }, _archive.Read().Records.Select(r => r.Id));
        Assert.StartsWith("bookmarks-archived-restore-", saved, StringComparison.Ordinal);
        Assert.Contains("since", File.ReadAllText(Path.Combine(snapshots, "pre-op", saved!)), StringComparison.Ordinal);
        Assert.False(File.Exists(_archive.FilePath + ".restore-tmp"));

        var before = File.ReadAllBytes(_archive.FilePath);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Throws<FileNotFoundException>(() => _archive.RestoreFrom(Path.Combine(snapshots, "pre-op", "missing.jsonl"), snapshots));
        Assert.Equal(before, File.ReadAllBytes(_archive.FilePath));

        // Without an archive there is nothing to keep.
        File.Delete(_archive.FilePath);
        Assert.Null(_archive.RestoreFrom(copy, snapshots));
        Assert.Equal(new[] { "a" }, _archive.Read().Records.Select(r => r.Id));
    }

    [Fact]
    public void ReplaceRecords_MakesTheArchiveThoseRecordsOnly_FromSeqOne_WithoutTheEarlierLinesText()
    {
        _archive.Append([Record("a", "secret note"), Record("b", "old b")]);
        _archive.MarkReattached(["b"]);
        _archive.MarkChecked(2);
        _clock.Advance(TimeSpan.FromDays(1));

        var written = _archive.ReplaceRecords([Record("c", "from the file"), Record("b", "b from the file"), Record("c", "a duplicate")]);
        Assert.Equal(new[] { ("c", 1L), ("b", 2L) }, written.Select(r => (r.Id, r.Seq)));
        var contents = _archive.Read();
        Assert.Equal(new[] { "c", "b" }, contents.Records.Select(r => r.Id));
        Assert.All(contents.Records, r => Assert.Equal(Monday.AddDays(1), r.ArchivedUtc));
        Assert.Equal(0, contents.LastCheckedSeq);
        Assert.Empty(contents.HistoryIds);
        var text = File.ReadAllText(_archive.FilePath);
        Assert.DoesNotContain("secret note", text, StringComparison.Ordinal);
        Assert.DoesNotContain("old b", text, StringComparison.Ordinal);
        Assert.False(File.Exists(_archive.FilePath + ".tmp"));

        Assert.Empty(_archive.ReplaceRecords([]));
        Assert.Empty(_archive.Read().Records);
    }

    [Fact]
    public void AStoreNameThatIsNoFileName_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new DetachedArchive(_directory.Path, "a/b", _logger));
    }

    private static ArchivedRecord Record(string id, string? note) => new()
    {
        Id = id,
        UserId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Identity = new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "Heat", Year = 1995 },
        AttachedUtc = Monday.AddDays(-200),
        DetachedUtc = Monday.AddDays(-100),
        Record = new JsonObject { ["Id"] = id, ["Note"] = note }
    };
}
