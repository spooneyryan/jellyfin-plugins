using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Kit.Library;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="MoveJournal"/> (format v1): the header, Committed lines only, the Seq order, invalid and
/// truncated lines, chaining in Seq order with the cycle stop, and pending relinks.
/// </summary>
public sealed class MoveJournalTests
{
    internal const string JournalId = "3f2a9c1e7b5d4e6f8a0b1c2d3e4f5a6b";

    internal static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    internal static readonly Guid C = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    internal static readonly Guid D = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HeaderAndCommittedLines_AreRead()
    {
        var journal = Parse(Header(), Line(1, A, B, extra: ",\"Kind\":\"Movie\",\"OldPath\":\"Q:\\\\a.mkv\",\"NewPath\":\"Q:\\\\b.mkv\",\"JobId\":\"9d8c7b6a5f4e4d3c2b1a0f9e8d7c6b5a\",\"Role\":\"Primary\""));

        Assert.Equal(MoveJournalStatus.Ok, journal.Status);
        Assert.Equal(Guid.Parse(JournalId), journal.JournalId);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), journal.CreatedUtc);
        var line = Assert.Single(journal.Lines);
        Assert.Equal(1, line.Seq);
        Assert.Equal(A, line.OldId);
        Assert.Equal(B, line.NewId);
        Assert.Equal(Monday, line.Utc);
        Assert.Equal(DateTimeKind.Utc, line.Utc.Kind);
        Assert.Equal("Movie", line.Kind);
        Assert.Equal(@"Q:\a.mkv", line.OldPath);
        Assert.Equal(@"Q:\b.mkv", line.NewPath);
        Assert.Equal(Guid.Parse("9d8c7b6a5f4e4d3c2b1a0f9e8d7c6b5a"), line.JobId);
        Assert.Equal("Primary", line.Role);
        Assert.False(line.Correction);
        Assert.Equal(1, journal.LastSeq);
        Assert.Equal(0, journal.InvalidLines);
        Assert.Null(journal.Problem);
    }

    [Fact]
    public void BlankTexts_ReadAsAbsent()
    {
        // A blank NewPath must never reach FindByPath, which on 10.11.5 then answers with the newest item.
        var journal = Parse(Header(), Line(1, A, B, extra: ",\"Kind\":\" \",\"OldPath\":\"\",\"NewPath\":\" \\t \",\"Role\":\"\\r\\n\""));

        var line = Assert.Single(journal.Lines);
        Assert.Null(line.Kind);
        Assert.Null(line.OldPath);
        Assert.Null(line.NewPath);
        Assert.Null(line.Role);
    }

    [Fact]
    public void OnlyCommittedLines_AreMoves_ButEveryLineInOrderCountsTowardsLastSeq()
    {
        var journal = Parse(Header(), Line(1, A, B), Line(2, B, C, status: "Planned"), Line(3, B, D, status: "RolledBack"));

        Assert.Equal(new long[] { 1 }, journal.Lines.Select(l => l.Seq));
        Assert.Equal(2, journal.IgnoredLines);
        Assert.Equal(3, journal.LastSeq);
        Assert.Equal(B, journal.Chain(A).EndId);
    }

    [Fact]
    public void ALineOfAnotherFormatVersion_IsIgnored_AndDoesNotCountTowardsLastSeq()
    {
        var journal = Parse(Header(), Line(1, A, B), Line(2, B, C, version: 2), Line(3, B, D));

        Assert.Equal(new long[] { 1, 3 }, journal.Lines.Select(l => l.Seq));
        Assert.Equal(1, journal.IgnoredLines);
        Assert.Equal(3, journal.LastSeq);
    }

    [Theory]
    [InlineData("{\"V\":1,\"JournalId\":\"" + JournalId + "\",\"Seq\":2,\"Status\":\"Committed\",\"Utc\":\"2026-10-05T12:00:00Z\",\"OldId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}")] // no NewId
    [InlineData("{\"V\":1,\"JournalId\":\"" + JournalId + "\",\"Seq\":2,\"Status\":\"Committed\",\"Utc\":\"2026-10-05T12:00:00Z\",\"OldId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"NewId\":\"00000000000000000000000000000000\"}")] // empty NewId
    [InlineData("{\"V\":1,\"JournalId\":\"" + JournalId + "\",\"Seq\":2,\"Status\":\"Committed\",\"Utc\":\"yesterday\",\"OldId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"NewId\":\"cccccccccccccccccccccccccccccccc\"}")] // bad Utc
    [InlineData("{\"V\":1,\"JournalId\":\"0123456789abcdef0123456789abcdef\",\"Seq\":2,\"Status\":\"Committed\",\"Utc\":\"2026-10-05T12:00:00Z\",\"OldId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"NewId\":\"cccccccccccccccccccccccccccccccc\"}")] // another journal
    [InlineData("{\"V\":1,\"JournalId\":\"" + JournalId + "\",\"Seq\":1,\"Status\":\"Committed\",\"Utc\":\"2026-10-05T12:00:00Z\",\"OldId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"NewId\":\"cccccccccccccccccccccccccccccccc\"}")] // Seq repeated
    [InlineData("{\"V\":1,\"JournalId\":\"" + JournalId + "\",\"Seq\":\"2\",\"Status\":\"Committed\",\"Utc\":\"2026-10-05T12:00:00Z\",\"OldId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"NewId\":\"cccccccccccccccccccccccccccccccc\"}")] // Seq as text
    [InlineData("{\"V\":1,\"JournalId\":\"" + JournalId + "\",\"Seq\":2,\"Utc\":\"2026-10-05T12:00:00Z\",\"OldId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"NewId\":\"cccccccccccccccccccccccccccccccc\"}")] // no Status
    [InlineData("{\"V\":1,\"Header\":true,\"JournalId\":\"" + JournalId + "\",\"CreatedUtc\":\"2026-10-01T12:00:00Z\"}")] // a second header
    [InlineData("{\"JournalId\":\"" + JournalId + "\",\"Seq\":2,\"Status\":\"Committed\",\"Utc\":\"2026-10-05T12:00:00Z\",\"OldId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"NewId\":\"cccccccccccccccccccccccccccccccc\"}")] // no V
    [InlineData("[1,2,3]")]
    [InlineData("{\"V\":1,\"JournalId\":")] // torn, but not the last line
    public void InvalidLines_AreSkipped_AndTheRestIsRead(string bad)
    {
        var journal = Parse(Header(), Line(1, A, B), bad, Line(3, B, C));

        Assert.Equal(MoveJournalStatus.Ok, journal.Status);
        Assert.Equal(new long[] { 1, 3 }, journal.Lines.Select(l => l.Seq));
        Assert.Equal(1, journal.InvalidLines);
        Assert.Equal("1 line(s) of the move journal are invalid and were skipped.", journal.Problem);
        Assert.Equal(C, journal.Chain(A).EndId);
    }

    [Fact]
    public void LinesOutOfOrder_AreInvalid()
    {
        var journal = Parse(Header(), Line(5, A, B), Line(3, B, C), Line(4, B, D), Line(6, B, C));

        Assert.Equal(new long[] { 5, 6 }, journal.Lines.Select(l => l.Seq));
        Assert.Equal(2, journal.InvalidLines);
        Assert.Equal(6, journal.LastSeq);
    }

    [Fact]
    public void AnIncompleteLastLine_IsIgnored_ACompleteOneWithoutItsLineFeedIsRead()
    {
        var torn = Parse(Encoding.UTF8.GetBytes(Header() + "\n" + Line(1, A, B) + "\n" + Line(2, B, C)[..40]));
        Assert.True(torn.TruncatedTail);
        Assert.Equal(0, torn.InvalidLines);
        Assert.Equal(1, torn.LastSeq);
        Assert.Single(torn.Lines);

        var complete = Parse(Encoding.UTF8.GetBytes(Header() + "\n" + Line(1, A, B) + "\n" + Line(2, B, C)));
        Assert.False(complete.TruncatedTail);
        Assert.Equal(2, complete.Lines.Count);
    }

    [Fact]
    public void ByteOrderMark_CrLf_AndBlankLines_AreAccepted()
    {
        var text = "\r\n" + Header() + "\r\n\r\n" + Line(1, A, B) + "\r\n   \r\n" + Line(2, B, C) + "\r\n";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

        var journal = Parse(bytes);

        Assert.Equal(MoveJournalStatus.Ok, journal.Status);
        Assert.Equal(2, journal.Lines.Count);
        Assert.Equal(0, journal.InvalidLines);
    }

    [Fact]
    public void GuidsInEitherFormat_AreRead()
    {
        var journal = Parse(Header(), Line(1, A, B).Replace("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", A.ToString("D"), StringComparison.Ordinal));

        Assert.Equal(A, Assert.Single(journal.Lines).OldId);
    }

    [Theory]
    [InlineData("", "Empty")]
    [InlineData("\n\n  \n", "Empty")]
    [InlineData("{\"V\":1,\"Header\":tr", "Empty")] // the header is being written
    [InlineData("{\"V\":1,\"Header\":tr\n", "InvalidHeader")]
    [InlineData("{\"V\":1,\"JournalId\":\"" + JournalId + "\",\"Seq\":1}\n", "InvalidHeader")] // no Header flag
    [InlineData("{\"V\":1,\"Header\":true,\"JournalId\":\"nope\"}\n", "InvalidHeader")]
    [InlineData("{\"Header\":true,\"JournalId\":\"" + JournalId + "\"}\n", "InvalidHeader")]
    [InlineData("[]\n", "InvalidHeader")]
    [InlineData("{\"V\":2,\"Header\":true,\"JournalId\":\"" + JournalId + "\"}\n", "UnsupportedVersion")]
    public void Header_States(string text, string expected)
    {
        var status = Enum.Parse<MoveJournalStatus>(expected);
        var journal = Parse(Encoding.UTF8.GetBytes(text + (status == MoveJournalStatus.UnsupportedVersion ? Line(1, A, B) + "\n" : string.Empty)));

        Assert.Equal(status, journal.Status);
        Assert.Null(journal.JournalId);
        Assert.Empty(journal.Lines);
        Assert.Equal(status is MoveJournalStatus.InvalidHeader or MoveJournalStatus.UnsupportedVersion, journal.Problem is not null);
    }

    [Fact]
    public void AHeaderWithoutCreatedUtc_IsStillAHeader()
    {
        var journal = Parse("{\"V\":1,\"Header\":true,\"JournalId\":\"" + JournalId + "\"}", Line(1, A, B));

        Assert.Equal(MoveJournalStatus.Ok, journal.Status);
        Assert.Null(journal.CreatedUtc);
    }

    [Fact]
    public void Chain_FollowsCommittedLinesInSeqOrder()
    {
        // A predicted id, then its correction (consumers apply it like any other line, through chaining).
        var journal = Parse(Header(), Line(1, A, B), Line(2, D, C), Line(3, B, C, extra: ",\"Correction\":true"));

        var chain = journal.Chain(A);

        Assert.Equal(A, chain.StartId);
        Assert.Equal(C, chain.EndId);
        Assert.Equal(new long[] { 1, 3 }, chain.Steps.Select(s => s.Seq));
        Assert.True(chain.Last!.Correction);
        Assert.False(chain.Revisits);
        Assert.False(chain.IsEmpty);
    }

    [Fact]
    public void Chain_OnlyFollowsLaterLines()
    {
        // B moved to C before A moved to B: the item that was A is at B, not at C.
        var journal = Parse(Header(), Line(1, B, C), Line(2, A, B));

        Assert.Equal(B, journal.Chain(A).EndId);
        Assert.Equal(C, journal.Chain(B).EndId);
        Assert.Equal(B, journal.Chain(B, afterSeq: 1).EndId);
        Assert.True(journal.Chain(B, afterSeq: 1).IsEmpty);
    }

    [Fact]
    public void Chain_AfterATime_SkipsTheLinesCommittedAtOrBeforeIt()
    {
        // A→B, then a new file at A's old path gives the id A again; a record attached to that A at noon + 1 h must not
        // follow the old line, only a later one (A→C at noon + 2 h), and every step of the chain obeys the same rule.
        var journal = Parse(
            Header(),
            Line(1, A, B, utc: Monday),
            Line(2, B, D, utc: Monday.AddMinutes(30)),
            Line(3, A, C, utc: Monday.AddHours(2)),
            Line(4, C, D, utc: Monday.AddHours(3)));

        Assert.Equal(D, journal.Chain(A).EndId);
        Assert.Equal(new long[] { 1, 2 }, journal.Chain(A).Steps.Select(s => s.Seq));

        var attached = journal.Chain(A, afterUtc: Monday.AddHours(1));
        Assert.Equal(new long[] { 3, 4 }, attached.Steps.Select(s => s.Seq));
        Assert.Equal(D, attached.EndId);

        // At the line's own time the line is skipped; a second earlier it is followed.
        Assert.Equal(new long[] { 3, 4 }, journal.Chain(A, afterUtc: Monday).Steps.Select(s => s.Seq));
        Assert.Equal(new long[] { 1, 2 }, journal.Chain(A, afterUtc: Monday.AddSeconds(-1)).Steps.Select(s => s.Seq));
        Assert.True(journal.Chain(A, afterUtc: Monday.AddHours(3)).IsEmpty);

        // A local time is the same instant, and afterSeq still applies with it.
        Assert.Equal(new long[] { 3, 4 }, journal.Chain(A, afterUtc: Monday.AddHours(1).ToLocalTime()).Steps.Select(s => s.Seq));
        Assert.Equal(new long[] { 4 }, journal.Chain(C, afterSeq: 1, afterUtc: Monday).Steps.Select(s => s.Seq));
    }

    [Fact]
    public void Chain_AfterATime_ALineWhoseTimeIsOutOfOrder_DoesNotHideALaterOne()
    {
        var journal = Parse(Header(), Line(1, A, B, utc: Monday.AddHours(2)), Line(2, A, C, utc: Monday), Line(3, A, D, utc: Monday.AddHours(3)));

        Assert.Equal(B, journal.Chain(A, afterUtc: Monday.AddHours(1)).EndId);
        Assert.Equal(D, journal.Chain(A, afterUtc: Monday.AddHours(2)).EndId);
    }

    [Fact]
    public void Chain_AMoveBack_EndsWhereItStarted_AndALaterMoveContinuesIt()
    {
        // Move back writes A→B, then B→A; a cycle can't loop because every step takes a later line.
        var back = Parse(Header(), Line(1, A, B), Line(2, B, A));
        var chain = back.Chain(A);
        Assert.Equal(A, chain.EndId);
        Assert.Equal(2, chain.Steps.Count);
        Assert.True(chain.Revisits);
        Assert.Equal(A, back.Chain(B).EndId);

        var again = Parse(Header(), Line(1, A, B), Line(2, B, A), Line(3, A, C));
        Assert.Equal(C, again.Chain(A).EndId);
        Assert.Equal(3, again.Chain(A).Steps.Count);

        var pingPong = Parse(Header(), Line(1, A, B), Line(2, B, A), Line(3, A, B), Line(4, B, A), Line(5, A, B));
        Assert.Equal(B, pingPong.Chain(A).EndId);
        Assert.Equal(5, pingPong.Chain(A).Steps.Count);
    }

    [Fact]
    public void Chain_OfAnIdTheJournalDoesntMove_IsEmpty()
    {
        var journal = Parse(Header(), Line(1, A, B));

        var chain = journal.Chain(D);

        Assert.True(chain.IsEmpty);
        Assert.Equal(D, chain.EndId);
        Assert.Null(chain.Last);
        Assert.True(MoveJournal.Missing.Chain(A).IsEmpty);
    }

    [Fact]
    public void PendingRelinks_TheCurrentIdOfAMovedItem_ForSevenDays()
    {
        var journal = Parse(Header(), Line(1, A, B), Line(2, C, D));

        Assert.True(journal.IsPendingRelink(B, Monday));
        Assert.True(journal.IsPendingRelink(B, Monday.AddDays(7).AddTicks(-1)));
        Assert.False(journal.IsPendingRelink(B, Monday.AddDays(7)));
        Assert.False(journal.IsPendingRelink(A, Monday));
        Assert.Equal(new[] { B, D }, journal.PendingRelinks(Monday).Select(l => l.NewId));
        Assert.Empty(journal.PendingRelinks(Monday.AddDays(8)));
    }

    [Fact]
    public void PendingRelinks_AnIdMovedOnSince_IsNotPending()
    {
        // B was a predicted id that a correction line replaced by C.
        var journal = Parse(Header(), Line(1, A, B), Line(2, B, C, extra: ",\"Correction\":true"));

        Assert.False(journal.IsPendingRelink(B, Monday));
        Assert.True(journal.IsPendingRelink(C, Monday));
        Assert.Null(journal.ArrivalOf(B));
        Assert.Equal(2, journal.ArrivalOf(C)!.Seq);

        // After a move back the original id is the current one again.
        var back = Parse(Header(), Line(1, A, B), Line(2, B, A));
        Assert.True(back.IsPendingRelink(A, Monday));
        Assert.False(back.IsPendingRelink(B, Monday));
    }

    [Fact]
    public void After_GivesTheLinesAfterAPosition()
    {
        var journal = Parse(Header(), Line(1, A, B), Line(2, B, C), Line(3, C, D));

        Assert.Equal(new long[] { 1, 2, 3 }, journal.After(0).Select(l => l.Seq));
        Assert.Equal(new long[] { 3 }, journal.After(2).Select(l => l.Seq));
        Assert.Empty(journal.After(3));
    }

    internal static string Header(string journalId = JournalId)
        => "{\"V\":1,\"Header\":true,\"JournalId\":\"" + journalId + "\",\"CreatedUtc\":\"2026-10-01T12:00:00Z\"}";

    internal static string Line(long seq, Guid oldId, Guid newId, string status = "Committed", int version = 1, string journalId = JournalId, DateTime? utc = null, string extra = "")
        => "{\"V\":" + version + ",\"JournalId\":\"" + journalId + "\",\"Seq\":" + seq + ",\"Status\":\"" + status + "\",\"Utc\":\""
            + (utc ?? Monday).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) + "\",\"OldId\":\"" + oldId.ToString("N")
            + "\",\"NewId\":\"" + newId.ToString("N") + "\"" + extra + "}";

    internal static MoveJournal Parse(params string[] lines) => MoveJournal.Parse(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));

    private static MoveJournal Parse(byte[] bytes) => MoveJournal.Parse(bytes);
}
