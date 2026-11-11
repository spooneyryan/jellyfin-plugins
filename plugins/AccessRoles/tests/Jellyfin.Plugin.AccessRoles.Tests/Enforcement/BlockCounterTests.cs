using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Enforcement;

/// <summary>
/// The blocked-request counters (<c>BlockCounter</c>): 10-minute windows from the hour, one
/// audit line per user, rule, action and outcome when a window closes, every window at shutdown, anonymous callers under a
/// null user, and no user in Diagnostics.
/// </summary>
public sealed class BlockCounterTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly ManualTimerProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 3, 0, TimeSpan.Zero));

    [Fact]
    public void Windows_StartFromTheHourInStepsOfTenMinutes()
    {
        Assert.Equal(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), BlockCounter.WindowOf(new DateTime(2026, 10, 20, 12, 9, 59, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 20, 12, 10, 0, DateTimeKind.Utc), BlockCounter.WindowOf(new DateTime(2026, 10, 20, 12, 10, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromMinutes(10), BlockCounter.Window);
    }

    [Fact]
    public void AFlood_IsOneLinePerUserRuleActionAndOutcome_OnceItsWindowClosed()
    {
        var counter = new BlockCounter(_clock);
        var kid = Guid.NewGuid();
        for (var i = 0; i < 500; i++)
        {
            counter.Record(kid, "Settings.Profile", "UserController.UpdateUserPassword", BlockCounter.Refused);
        }

        counter.Record(null, "Settings.Profile", "UserController.UpdateUserPassword", BlockCounter.Refused);
        counter.Record(Guid.Empty, "Settings", "UserController.UpdateUser", BlockCounter.FailedClosed);
        Assert.Empty(counter.Take(all: false, _ => "kid"));

        _clock.Advance(TimeSpan.FromMinutes(7));
        counter.Record(kid, "Settings.Profile", "UserController.UpdateUserPassword", BlockCounter.Refused);
        var lines = counter.Take(all: false, id => id.Equals(kid) ? "kid" : null);

        Assert.Equal(3, lines.Count);
        var flood = Assert.Single(lines, l => l.TargetUserId == kid);
        Assert.Equal((AuditActions.RequestsBlocked, "kid", AuditActor.SystemKind), (flood.Action, flood.TargetName, flood.Actor.Kind));
        Assert.Equal(500, flood.Details!["Count"]);
        Assert.Equal(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), flood.Details["WindowStartUtc"]);
        var anonymous = lines.Where(l => l.TargetUserId is null).ToList();
        Assert.Equal(2, anonymous.Count);
        Assert.All(anonymous, l => Assert.Null(l.Details!["UserId"]));
        Assert.Single(anonymous, l => l.Action == AuditActions.FilterErrors);

        // The new window's count stays until it closes, or until the server stops.
        var open = counter.Describe();
        Assert.Equal(1, ((IDictionary<string, int>)open["OpenWindows"]!)["Settings.Profile Refused"]);
        Assert.Equal(502L, ((IDictionary<string, long>)open["Totals"]!)[BlockCounter.Refused]);
        Assert.DoesNotContain(kid.ToString("N"), System.Text.Json.JsonSerializer.Serialize(open), StringComparison.Ordinal);
        Assert.Equal(1, (int)Assert.Single(counter.Take(all: true, _ => null)).Details!["Count"]!);
    }

    [Fact]
    public async Task TheService_WritesClosedWindowsToTheAuditLog_AndEveryWindowWhenTheServerStops()
    {
        using var audit = new AccessRolesAuditLog(_directory.Combine("audit"), () => 12, NullLogger.Instance, _clock);
        var counter = new BlockCounter(_clock);
        var kid = Guid.NewGuid();
        var users = new FakeServerUsers(id => id.Equals(kid) ? new ServerUser(kid, "kid", false, false, false, NativeAccess.Default) : null);
        using var service = new BlockCounterService(Mock.Of<IServerApplicationHost>(), counter, audit, users, NullLogger.Instance, _clock);

        counter.Record(kid, "Settings.Home", "UserController.UpdateUserConfiguration", BlockCounter.Reverted);
        Assert.Equal(0, await service.FlushAsync(all: false, TestContext.Current.CancellationToken));
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await service.FlushAsync(all: false, TestContext.Current.CancellationToken));
        counter.Record(kid, "Settings.Home", "UserController.UpdateUserConfiguration", BlockCounter.Reverted);
        await service.StopAsync(TestContext.Current.CancellationToken);

        var lines = audit.Read(0, 10, null, null, null).Items;
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal((AuditActions.RequestsBlocked, kid, "kid"), (l.Action, l.TargetUserId!.Value, l.TargetName)));
    }

    public void Dispose() => _directory.Dispose();
}
