using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.Kit.Tests.Library.MoveJournalTests;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="MoveJournalReader"/> on the real file system: the journal's place, reading while
/// Library Maintenance holds it open for appending, the cache, a line being written, an unreadable file, and the consumer
/// catch-up with its two reset rules.
/// </summary>
public sealed class MoveJournalReaderTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly ListLogger _logger = new();

    private string JournalFile => _directory.Combine("data", "librarymaintenance", "moves.jsonl");

    [Fact]
    public void TheServersJournal_IsUnderLibraryMaintenancesDataFolder()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_directory.Combine("data"));

        var reader = new MoveJournalReader(paths.Object, _logger);

        Assert.Equal(JournalFile, reader.FilePath);
    }

    [Fact]
    public void NoFile_IsMissing()
    {
        var reader = CreateReader();

        var journal = reader.Read();

        Assert.Same(MoveJournal.Missing, journal);
        Assert.Equal(MoveJournalStatus.Missing, journal.Status);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void ReadsWhileTheWriterHoldsTheFileOpen_AndSeesItsAppends()
    {
        // Library Maintenance's writer opens the journal with FileShare.Read and appends with Flush(true).
        Directory.CreateDirectory(Path.GetDirectoryName(JournalFile)!);
        using var writer = new FileStream(JournalFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        Append(writer, Header() + "\n" + Line(1, A, B) + "\n");
        var reader = CreateReader();

        var first = reader.Read();
        Assert.Equal(MoveJournalStatus.Ok, first.Status);
        Assert.Single(first.Lines);
        Assert.Same(first, reader.Read());

        Append(writer, Line(2, B, C) + "\n");
        var second = reader.Read();
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Lines.Count);
        Assert.Equal(C, second.Chain(A).EndId);
    }

    [Fact]
    public void ALineBeingWritten_IsIgnoredUntilItIsComplete()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalFile)!);
        using var writer = new FileStream(JournalFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var line = Line(2, B, C);
        Append(writer, Header() + "\n" + Line(1, A, B) + "\n" + line[..25]);
        var reader = CreateReader();

        var torn = reader.Read();
        Assert.True(torn.TruncatedTail);
        Assert.Equal(1, torn.LastSeq);
        Assert.Null(torn.Problem);

        Append(writer, line[25..] + "\n");
        var complete = reader.Read();
        Assert.False(complete.TruncatedTail);
        Assert.Equal(2, complete.LastSeq);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void AFileThatCantBeRead_IsUnreadable_LoggedOnce_AndReadAgainLater()
    {
        Write(Header(), Line(1, A, B));
        var reader = CreateReader();

        using (new FileStream(JournalFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = reader.Read();
            Assert.Equal(MoveJournalStatus.Unreadable, locked.Status);
            Assert.Contains("couldn't be read", locked.Problem, StringComparison.Ordinal);
            Assert.Empty(locked.Lines);
            Assert.Equal(MoveJournalStatus.Unreadable, reader.Read().Status);
        }

        Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Equal(MoveJournalStatus.Ok, reader.Read().Status);
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("could be read again", StringComparison.Ordinal));
    }

    [Fact]
    public void AProblem_IsLoggedOnce_UntilItChanges()
    {
        Write(Header(), Line(1, A, B), "not json");
        var reader = CreateReader();

        Assert.Equal(1, reader.Read().InvalidLines);
        File.AppendAllText(JournalFile, Line(3, B, C) + "\n");
        Assert.Equal(2, reader.Read().Lines.Count);
        Assert.Single(_logger.At(LogLevel.Warning));

        File.AppendAllText(JournalFile, "still not json\n");
        Assert.Equal(2, reader.Read().InvalidLines);
        Assert.Equal(2, _logger.At(LogLevel.Warning).Count());
        Assert.Contains("2 line(s)", _logger.Entries[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedJournal_IsLogged()
    {
        Write("{\"V\":2,\"Header\":true,\"JournalId\":\"" + JournalId + "\"}", Line(1, A, B));

        var journal = CreateReader().Read();

        Assert.Equal(MoveJournalStatus.UnsupportedVersion, journal.Status);
        Assert.Contains("format version 2", Assert.Single(_logger.At(LogLevel.Warning)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CatchUp_FirstTime_GivesEveryLine()
    {
        Write(Header(), Line(1, A, B), Line(2, B, C, status: "Planned"), Line(3, C, D));

        var catchUp = CreateReader().CatchUp(null, 0);

        Assert.Equal(new long[] { 1, 3 }, catchUp.Lines.Select(l => l.Seq));
        Assert.Equal(Guid.Parse(JournalId), catchUp.JournalId);
        Assert.Equal(3, catchUp.LastMoveSeq);
        Assert.Equal(MoveJournalReset.None, catchUp.Reset);
        Assert.True(catchUp.HasChanges(null, 0));
    }

    [Fact]
    public void CatchUp_SameJournal_GivesTheNewLinesOnly()
    {
        Write(Header(), Line(1, A, B), Line(2, B, C), Line(3, C, D));
        var reader = CreateReader();

        var catchUp = reader.CatchUp(Guid.Parse(JournalId), 2);
        Assert.Equal(new long[] { 3 }, catchUp.Lines.Select(l => l.Seq));
        Assert.Equal(3, catchUp.LastMoveSeq);
        Assert.Equal(MoveJournalReset.None, catchUp.Reset);

        var upToDate = reader.CatchUp(Guid.Parse(JournalId), 3);
        Assert.Empty(upToDate.Lines);
        Assert.False(upToDate.HasChanges(Guid.Parse(JournalId), 3));
    }

    [Fact]
    public void CatchUp_ANewJournalId_ResetsAndReplaysEverything()
    {
        // Library Maintenance reinstalled, its data folder restored, or the journal rebuilt by an import.
        Write(Header(), Line(1, A, B), Line(2, B, C));

        var catchUp = CreateReader().CatchUp(Guid.NewGuid(), 57);

        Assert.Equal(MoveJournalReset.NewJournalId, catchUp.Reset);
        Assert.Equal(new long[] { 1, 2 }, catchUp.Lines.Select(l => l.Seq));
        Assert.Equal(Guid.Parse(JournalId), catchUp.JournalId);
        Assert.Equal(2, catchUp.LastMoveSeq);
    }

    [Fact]
    public void CatchUp_ALastSeqBelowTheStoredOne_ResetsAndReplaysEverything()
    {
        // A data folder restored from an older backup keeps its JournalId.
        Write(Header(), Line(1, A, B), Line(2, B, C));

        var catchUp = CreateReader().CatchUp(Guid.Parse(JournalId), 5);

        Assert.Equal(MoveJournalReset.SeqBelowStored, catchUp.Reset);
        Assert.Equal(new long[] { 1, 2 }, catchUp.Lines.Select(l => l.Seq));
        Assert.Equal(2, catchUp.LastMoveSeq);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"V\":1,\"Header\":tr\n")]
    [InlineData("{\"V\":3,\"Header\":true,\"JournalId\":\"" + JournalId + "\"}\n")]
    public void CatchUp_WithoutAUsableJournal_KeepsThePosition(string? content)
    {
        if (content is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalFile)!);
            File.WriteAllText(JournalFile, content);
        }

        var stored = Guid.NewGuid();
        var catchUp = CreateReader().CatchUp(stored, 12);

        Assert.Empty(catchUp.Lines);
        Assert.Equal(stored, catchUp.JournalId);
        Assert.Equal(12, catchUp.LastMoveSeq);
        Assert.Equal(MoveJournalReset.None, catchUp.Reset);
        Assert.False(catchUp.HasChanges(stored, 12));
    }

    [Fact]
    public void CatchUp_ReplayingTwice_GivesTheSameRecords()
    {
        // Idempotent because a line only moves records still on its OldId: a snapshot moved A→B→A by a Move
        // back ends on A whether the lines are applied once or replayed after a reset.
        Write(Header(), Line(1, A, B), Line(2, D, C), Line(3, B, A));
        var reader = CreateReader();
        var records = new[] { new IdentitySnapshot { ItemId = A }, new IdentitySnapshot { ItemId = D }, new IdentitySnapshot { ItemId = C } };

        var once = Apply(records, reader.CatchUp(null, 0));
        var twice = Apply(once, reader.CatchUp(Guid.NewGuid(), 3));

        Assert.Equal(new[] { A, C, C }, once.Select(r => r.ItemId));
        Assert.Equal(once.Select(r => r.ItemId), twice.Select(r => r.ItemId));
    }

    public void Dispose() => _directory.Dispose();

    private static IdentitySnapshot[] Apply(IdentitySnapshot[] records, MoveJournalCatchUp catchUp)
        => records.Select(r => catchUp.Lines.Aggregate(r, ItemIdentity.ApplyMove)).ToArray();

    private static void Append(FileStream writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes);
        writer.Flush(flushToDisk: true);
    }

    private MoveJournalReader CreateReader() => new(JournalFile, _logger);

    private void Write(params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalFile)!);
        File.WriteAllText(JournalFile, string.Join("\n", lines) + "\n");
    }
}
