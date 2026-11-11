using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Audit;

/// <summary>
/// The audit log: one JSON line per entry in <c>audit/audit-&lt;yyyyMM&gt;.jsonl</c>, numbered across months,
/// read newest first with paging and filters, exported as written, a cut-short line skipped, a failed write counted, and
/// the retention by months and by size.
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private static readonly Guid Admin = new("5a1e0c5e-7d2a-4c38-9a8e-1f0d2b3c4d5e");
    private static readonly Guid Kid = new("a1c4e5f6-0718-4293-a4b5-c6d7e8f90a1b");

    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 31, 23, 59, 0, TimeSpan.Zero));
    private readonly ListLogger _logger = new();

    [Fact]
    public async Task Lines_AreOneJsonObjectEach_InTheMonthsFile_NumberedAcrossMonths()
    {
        using var log = Create();

        var october = await log.AppendAsync([Line("RoleCreated", role: Guid.NewGuid(), name: "Kid"), Line("AssignmentChanged", user: Kid, name: "kid")], TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var november = await log.AppendAsync([Line("DefaultRolesChanged")], TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L], october.Select(e => e.Seq));
        Assert.Equal(3, Assert.Single(november).Seq);
        var lines = FileLines("202610");
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[1]);
        Assert.Equal(1, first.RootElement.GetProperty("V").GetInt32());
        Assert.Equal(2, first.RootElement.GetProperty("Seq").GetInt64());
        Assert.Equal("Admin", first.RootElement.GetProperty("Actor").GetProperty("Kind").GetString());
        Assert.Equal(Admin.ToString("N"), first.RootElement.GetProperty("Actor").GetProperty("UserId").GetString());
        Assert.Equal(Kid.ToString("N"), first.RootElement.GetProperty("TargetUserId").GetString());
        Assert.False(first.RootElement.TryGetProperty("TargetRoleId", out _));
        Assert.Single(FileLines("202611"));

        // A new instance (a restart) goes on from the last number.
        using var again = Create();
        Assert.Equal(4, Assert.Single(await again.AppendAsync([Line("RoleDeleted")], TestContext.Current.CancellationToken)).Seq);
    }

    [Fact]
    public async Task Read_IsNewestFirst_WithPaging_AndFiltersByUserActionAndTime()
    {
        using var log = Create();
        for (var i = 0; i < 5; i++)
        {
            await log.AppendAsync([Line(i % 2 == 0 ? "AssignmentChanged" : "RoleUpdated", user: i == 3 ? Kid : null)], TestContext.Current.CancellationToken);
            _clock.Advance(TimeSpan.FromHours(1));
        }

        var page = log.Read(1, 2, null, null, null);
        Assert.Equal(5, page.TotalRecordCount);
        Assert.Equal([4L, 3L], page.Items.Select(e => e.Seq));
        Assert.Equal([4L], log.Read(0, 10, Kid, null, null).Items.Select(e => e.Seq));
        Assert.Equal(5, log.Read(0, 10, Admin, null, null).TotalRecordCount);
        Assert.Equal([5L, 3L, 1L], log.Read(0, 10, null, "assignmentchanged", null).Items.Select(e => e.Seq));
        Assert.Equal([5L, 4L], log.Read(0, 10, null, null, _clock.GetUtcNow().UtcDateTime.AddHours(-2)).Items.Select(e => e.Seq));
    }

    [Fact]
    public async Task Read_FiltersByRule_ARuleNameJoinedWithPlusByEachOfItsRulesAndAsItIs_AndByAnEndTime()
    {
        // The filter times an action with more than one hardening rule, and counts a failure of its rules, under their
        // names joined with + (ServerHardening.RuleNames), so a line's Rule can be "A+B".
        using var log = Create();
        AuditRecord Blocked(string rule) => new(AuditActor.System, AuditActions.RequestsBlocked, null, null, null, new Dictionary<string, object?> { ["Rule"] = rule, ["Count"] = 3 });
        await log.AppendAsync([Blocked("Settings.Profile")], TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(1));
        await log.AppendAsync([Blocked("LibraryVisibility+ItemIds"), Line("RoleUpdated")], TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(1));
        await log.AppendAsync([Blocked("ItemIds")], TestContext.Current.CancellationToken);

        Assert.Equal([2L], log.Read(0, 10, null, null, null, rule: "libraryvisibility").Items.Select(e => e.Seq));
        Assert.Equal([4L, 2L], log.Read(0, 10, null, null, null, rule: "ItemIds").Items.Select(e => e.Seq));
        Assert.Equal([2L], log.Read(0, 10, null, null, null, rule: "LibraryVisibility+ItemIds").Items.Select(e => e.Seq));
        Assert.Equal([1L], log.Read(0, 10, null, null, null, rule: "Settings.Profile").Items.Select(e => e.Seq));
        Assert.Empty(log.Read(0, 10, null, null, null, rule: "Library").Items);
        Assert.Equal([2L, 1L], log.Read(0, 10, null, AuditActions.RequestsBlocked, null, _clock.GetUtcNow().UtcDateTime.AddMinutes(-30)).Items.Select(e => e.Seq));
    }

    [Fact]
    public async Task ALineCutShort_IsSkipped_AndTheNextWriteStartsOnANewLine()
    {
        using var log = Create();
        await log.AppendAsync([Line("RoleCreated")], TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(Path.Combine(_directory.Path, "audit-202610.jsonl"), "{\"V\":1,\"Seq\":2,\"Utc\":\"2026-10-", TestContext.Current.CancellationToken);

        using var again = Create();
        var next = Assert.Single(await again.AppendAsync([Line("RoleUpdated")], TestContext.Current.CancellationToken));

        Assert.Equal(2, next.Seq);
        Assert.Equal([2L, 1L], again.Read(0, 10, null, null, null).Items.Select(e => e.Seq));
        Assert.Equal(3, FileLines("202610").Length);
    }

    [Fact]
    public async Task ReadLines_GivesTheLinesOfARange_AsWritten_OldestFirst()
    {
        using var log = Create();
        await log.AppendAsync([Line("RoleCreated")], TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(1));
        var middle = _clock.GetUtcNow().UtcDateTime;
        await log.AppendAsync([Line("RoleUpdated"), Line("RoleDeleted")], TestContext.Current.CancellationToken);

        var all = log.ReadLines(null, null);
        var since = log.ReadLines(middle, null);
        var before = log.ReadLines(null, middle);

        Assert.Equal(3, all.Count);
        Assert.Contains("\"Action\":\"RoleCreated\"", all[0], StringComparison.Ordinal);
        Assert.Equal(2, since.Count);
        Assert.Single(before);
        Assert.Equal(FileLines("202610").Concat(FileLines("202611")), all);
    }

    [Fact]
    public async Task AFailedWrite_IsLoggedAndCounted_AndChangesNothingElse()
    {
        // The audit folder's name is taken by a file: the write fails.
        var blocked = Path.Combine(_directory.Path, "blocked");
        await File.WriteAllTextAsync(blocked, "x", TestContext.Current.CancellationToken);
        using var log = new AccessRolesAuditLog(blocked, () => 12, _logger, _clock);

        var written = await log.AppendAsync([Line("RoleCreated")], TestContext.Current.CancellationToken);

        Assert.Empty(written);
        Assert.Single(_logger.At(LogLevel.Warning));
        Assert.Equal(1, log.Describe()["WriteErrors"]);
    }

    [Fact]
    public async Task Retention_RemovesMonthsOlderThanTheSetting_AndNeverTheCurrentOne()
    {
        using var log = Create(months: 2);
        foreach (var month in new[] { "202606", "202607", "202608", "202609" })
        {
            await File.WriteAllTextAsync(Path.Combine(_directory.Path, "audit-" + month + ".jsonl"), string.Empty, TestContext.Current.CancellationToken);
        }

        await log.AppendAsync([Line("RoleCreated")], TestContext.Current.CancellationToken);

        var result = await log.ApplyRetentionAsync(TestContext.Current.CancellationToken);

        // Now is 31 October: entries younger than two months are kept, so August (ended 1 September) is still in.
        Assert.Equal(["audit-202606.jsonl", "audit-202607.jsonl"], (IEnumerable<string>)result["Deleted"]!);
        Assert.Equal(["audit-202608.jsonl", "audit-202609.jsonl", "audit-202610.jsonl"], Directory.GetFiles(_directory.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Retention_RemovesTheOldestMonths_WhileTheFilesAreOverTheCap()
    {
        using var writer = Create(months: 0);
        foreach (var month in new[] { "202607", "202608", "202609" })
        {
            await File.WriteAllTextAsync(Path.Combine(_directory.Path, "audit-" + month + ".jsonl"), new string('x', 100), TestContext.Current.CancellationToken);
        }

        await writer.AppendAsync([Line("RoleCreated")], TestContext.Current.CancellationToken);

        // A cap of the current month and one and a half older ones: the two oldest go.
        using var log = Create(months: 0, cap: new FileInfo(Path.Combine(_directory.Path, "audit-202610.jsonl")).Length + 150);
        var result = await log.ApplyRetentionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["audit-202607.jsonl", "audit-202608.jsonl"], (IEnumerable<string>)result["Deleted"]!);
        Assert.True(File.Exists(Path.Combine(_directory.Path, "audit-202610.jsonl")));
    }

    [Fact]
    public async Task Describe_CountsTheFiles_AndTheLastNumber()
    {
        using var log = Create();
        await log.AppendAsync([Line("RoleCreated"), Line("RoleUpdated")], TestContext.Current.CancellationToken);

        var described = log.Describe();

        Assert.Equal(1, described["Files"]);
        Assert.Equal(2L, described["LastSeq"]);
        Assert.Equal(0, described["WriteErrors"]);
        Assert.Equal(AccessRolesAuditLog.CapBytes, described["CapBytes"]);
    }

    public void Dispose() => _directory.Dispose();

    private string[] FileLines(string month) => File.ReadAllLines(Path.Combine(_directory.Path, "audit-" + month + ".jsonl"));

    private static AuditRecord Line(string action, Guid? user = null, Guid? role = null, string? name = null)
        => new(new AuditActor(AuditActor.AdminKind, Admin, "admin"), action, user, role, name, new Dictionary<string, object?> { ["Reason"] = "Test" });

    private AccessRolesAuditLog Create(int months = 12, long cap = AccessRolesAuditLog.CapBytes)
        => new(_directory.Path, () => months, _logger, _clock, cap);
}
