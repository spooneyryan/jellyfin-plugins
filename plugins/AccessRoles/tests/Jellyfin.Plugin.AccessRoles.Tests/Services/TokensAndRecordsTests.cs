using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Services;

/// <summary>
/// The <c>IfUpdatedAt</c> tokens, and what 0.9.2 adds to schema 1: the ledger's pending release, which a round
/// trip keeps.
/// </summary>
public sealed class TokensAndRecordsTests
{
    [Fact]
    public void AKeyedLinesToken_IsTheSameWhateverTheOrder_AndRemembersItsLines_SoAStaleOneNamesWhatDiffers()
    {
        // A 409 on a stale members, preview or plan token names only what differs since.
        var then = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "2", ["c"] = "3" };
        var token = AccessRolesTokens.Of(then);
        var now = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "9", ["d"] = "4" };

        Assert.Equal(token, AccessRolesTokens.Of(new Dictionary<string, string>(StringComparer.Ordinal) { ["c"] = "3", ["a"] = "1", ["b"] = "2" }));
        Assert.Equal(32, token.Length);
        Assert.Equal(["b", "c", "d"], AccessRolesTokens.Differing(token, now)!.Order(StringComparer.Ordinal));
        Assert.Empty(AccessRolesTokens.Differing(" " + token.ToUpperInvariant() + " ", then)!);
        Assert.True(AccessRolesTokens.Same(token, " " + token.ToUpperInvariant()));
        Assert.False(AccessRolesTokens.Same(token, AccessRolesTokens.Of(now)));

        // A token this process never made (or forgot) names nothing.
        Assert.Null(AccessRolesTokens.Differing(new string('f', 32), now));
        Assert.Null(AccessRolesTokens.Differing(null, now));
    }

    [Fact]
    public void ATokenMatchesTheExactTime_OrTheSameTimeCutToTheMillisecond_AndNothingElse()
    {
        var stored = new DateTime(2026, 10, 19, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567);

        Assert.True(AccessRolesTokens.Matches(stored, stored));
        Assert.True(AccessRolesTokens.Matches(stored, new DateTime(2026, 10, 19, 12, 0, 0, 123, DateTimeKind.Utc)));
        Assert.False(AccessRolesTokens.Matches(stored, new DateTime(2026, 10, 19, 12, 0, 0, 122, DateTimeKind.Utc)));
        Assert.True(AccessRolesTokens.MatchesOrNone(null, null));
        Assert.False(AccessRolesTokens.MatchesOrNone(stored, null));
        Assert.False(AccessRolesTokens.MatchesOrNone(null, stored));
    }

    [Fact]
    public void EveryChange_MovesTheTimeToALaterMillisecond_EvenWithTheClockBehind()
    {
        var previous = new DateTime(2026, 10, 19, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567);

        var sameMillisecond = AccessRolesTokens.Next(previous, previous);
        var clockBehind = AccessRolesTokens.Next(previous, previous.AddMinutes(-5));
        var later = AccessRolesTokens.Next(previous, previous.AddMinutes(5));

        Assert.Equal(new DateTime(2026, 10, 19, 12, 0, 0, 124, DateTimeKind.Utc), sameMillisecond);
        Assert.Equal(sameMillisecond, clockBehind);
        Assert.Equal(previous.AddMinutes(5), later);
        Assert.False(AccessRolesTokens.Matches(sameMillisecond, previous));
        Assert.Equal(previous, AccessRolesTokens.Next(null, previous));
    }

    [Fact]
    public async Task ALedgerEntrysPendingRelease_SurvivesARoundTrip()
    {
        using var directory = new TempDirectory();
        var file = directory.Combine("accessroles.json");
        var user = Guid.NewGuid();
        using (var store = AccessRolesStore.Create(file, NullLogger.Instance))
        {
            await store.UpdateAsync(
                d => d with
                {
                    Ledger = new Dictionary<Guid, LedgerEntry>
                    {
                        [user] = Ledger(false, Movies) with
                        {
                            Release = new LedgerRelease { Choice = AccessRolesUnmanageChoice.Restore, RequestedUtc = Now, EnabledFolders = [Movies.Id] }
                        }
                    }
                },
                TestContext.Current.CancellationToken);
        }

        using var reopened = AccessRolesStore.Create(file, NullLogger.Instance);
        var release = reopened.Current.Ledger[user].Release!;

        Assert.True(release.Choice.Is(AccessRolesUnmanageChoice.Restore));
        Assert.Equal(Now, release.RequestedUtc);
        Assert.Equal([Movies.Id], release.EnabledFolders);
        Assert.False(release.EnableAllFolders);
        Assert.Contains("\"Release\": {", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken)), StringComparison.Ordinal);
    }
}

/// <summary>
/// The bound on the remembered tokens' lines. Filling the memory forgets every other test's tokens too, so a 409 there
/// would name everyone instead of what differs: this runs alone, after the tests that run in parallel.
/// </summary>
[Collection(RememberedTokensCollection.Name)]
public sealed class RememberedTokensTests
{
    [Fact]
    public void TheRememberedLines_AreBounded_TheOldestForgottenFirst()
    {
        var first = AccessRolesTokens.Of(new Dictionary<string, string>(StringComparer.Ordinal) { ["bounded"] = Guid.NewGuid().ToString("N") });
        for (var i = 0; i <= AccessRolesTokens.RememberedTokens; i++)
        {
            AccessRolesTokens.Of(new Dictionary<string, string>(StringComparer.Ordinal) { ["bounded"] = Guid.NewGuid().ToString("N") });
        }

        Assert.Null(AccessRolesTokens.Differing(first, new Dictionary<string, string>(StringComparer.Ordinal)));
    }
}

/// <summary>Tests that fill the process's remembered tokens don't run beside the tests that make and check tokens.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RememberedTokensCollection
{
    public const string Name = "Remembered tokens";
}
