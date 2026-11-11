using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Policy;

/// <summary>
/// <c>PolicyWriter</c>, the only writer of users' policies, as measured on a 10.11.5 sandbox:
/// the round trip over every stored field of a deliberately non-default user (a 70-field dump), the lyric flag, the
/// negative controls the fake reproduces, the skip of an unchanged write, the re-read with its rebuild and abort under a
/// concurrent admin edit, the per-user lock, the post-write check and the loop guard.
/// </summary>
public sealed class PolicyWriterTests
{
    private static readonly Guid Movies = TestDocuments.LibraryId(1);
    private static readonly Guid Kids = TestDocuments.LibraryId(2);
    private static readonly Guid Shows = TestDocuments.LibraryId(3);

    private readonly FakeJellyfinUsers _server = new();
    private readonly UserPolicyLocks _locks = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task TheRoundTrip_ChangesOnlyEnabledFolders_AmongEveryStoredField_AndKeepsTheLyricFlag()
    {
        // Measured on 10.11.5: the writer changed EnabledFolders from [Movies] to [Movies, Shows]; of every stored field only that
        // changed, plus the row version (passing the DTO's own schedules keeps their row ids).
        var user = _server.AddRich(Movies);
        var before = FakeJellyfinUsers.Dump(_server.Stored(user.Id));

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Movies, Shows]), TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.Written, result.Outcome);
        var after = FakeJellyfinUsers.Dump(_server.Stored(user.Id));
        Assert.True(before.Count >= 68, "The dump has " + before.Count + " fields.");
        Assert.Equal(["Preference.EnabledFolders", "User.RowVersion"], FakeJellyfinUsers.Differences(before, after));
        Assert.Equal("True", after["Permission.EnableLyricManagement"]);
        Assert.Equal(before["Preference.BlockedMediaFolders"], after["Preference.BlockedMediaFolders"]);
        Assert.Equal(before["Preference.BlockedChannels"], after["Preference.BlockedChannels"]);
        Assert.Equal(before["AccessSchedules"], after["AccessSchedules"]);
        Assert.False(result.Before!.EnableAllFolders);
        Assert.Equal([Movies], result.Before.EnabledFolders);
        Assert.Single(_server.Writes);
    }

    [Fact]
    public async Task TheRoundTrip_ToEveryLibrary_ChangesOnlyTheTwoLibraryFields_AsPoliciesAndAsStoredFields()
    {
        var user = _server.AddRich(Movies);
        var policyBefore = PolicyFields.Dump(FakeJellyfinUsers.PolicyOf(_server.Cached(user.Id)));
        var storedBefore = FakeJellyfinUsers.Dump(_server.Stored(user.Id));

        await Writer().WriteAsync(user.Id, LibraryFields.All, TestContext.Current.CancellationToken);

        Assert.Equal([PolicyFields.EnableAllFolders, PolicyFields.EnabledFolders], PolicyFields.Differences(policyBefore, PolicyFields.Dump(FakeJellyfinUsers.PolicyOf(_server.Cached(user.Id))), []));
        Assert.Equal(["Permission.EnableAllFolders", "Preference.EnabledFolders", "User.RowVersion"], FakeJellyfinUsers.Differences(storedBefore, FakeJellyfinUsers.Dump(_server.Stored(user.Id))));
        Assert.Equal(typeof(UserPolicy).GetProperties().Count(p => p.CanWrite), PolicyFields.Names.Count);
    }

    [Fact]
    public async Task WithoutTheLyricFix_TheSameRoundTripClearsTheLyricFlag_TheNegativeControl()
    {
        // On 10.11.5, GetUserDto never fills EnableLyricManagement, so writing its policy back clears the
        // permission. The fake reproduces it, which is what makes the writer's fix matter.
        var user = _server.AddRich(Movies);
        var policy = FakeJellyfinUsers.PolicyOf(_server.Cached(user.Id));
        policy.EnabledFolders = [Movies, Shows];

        await _server.Manager.Object.UpdatePolicyAsync(user.Id, policy);

        Assert.False(_server.Stored(user.Id).HasPermission(PermissionKind.EnableLyricManagement));
    }

    [Fact]
    public async Task APartialPolicy_ResetsMostStoredFields_TheNegativeControl()
    {
        // On 10.11.5, a policy of only the managed fields and the provider ids reset 32 stored fields.
        var user = _server.AddRich(Movies);
        var before = FakeJellyfinUsers.Dump(_server.Stored(user.Id));
        var partial = new UserPolicy
        {
            EnableAllFolders = false,
            EnabledFolders = [Movies, Shows],
            AuthenticationProviderId = user.AuthenticationProviderId,
            PasswordResetProviderId = user.PasswordResetProviderId
        };

        await _server.Manager.Object.UpdatePolicyAsync(user.Id, partial);

        var reset = FakeJellyfinUsers.Differences(before, FakeJellyfinUsers.Dump(_server.Stored(user.Id)));
        Assert.True(reset.Count >= 29, "Only " + reset.Count + " fields changed: " + string.Join(", ", reset));
        Assert.Contains("AccessSchedules", reset);
        Assert.Contains("Permission.EnableLyricManagement", reset);
        Assert.Contains("User.MaxActiveSessions", reset);
    }

    [Fact]
    public async Task AWriteBuiltFromAStaleRead_ReEnablesADisabledUser_TheRiskTheReReadCloses()
    {
        // Case 1 on 10.11.5: UpdatePolicyAsync writes every field, IsDisabled included, without a lock.
        var user = _server.Add("cc", allFolders: false, folders: [Movies]);
        var stale = FakeJellyfinUsers.PolicyOf(_server.Cached(user.Id));
        _server.AdminPost(user.Id, p => p.IsDisabled = true);
        stale.EnabledFolders = [Movies, Shows];

        await _server.Manager.Object.UpdatePolicyAsync(user.Id, stale);

        Assert.False(_server.Stored(user.Id).HasPermission(PermissionKind.IsDisabled));
    }

    [Fact]
    public async Task AnUnchangedResult_IsSkipped_WhateverTheOrder()
    {
        var user = _server.Add("same", allFolders: false, folders: [Movies, Shows]);

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Shows, Movies]), TestContext.Current.CancellationToken);
        var all = _server.Add("all");
        var allResult = await Writer().WriteAsync(all.Id, LibraryFields.All, TestContext.Current.CancellationToken);

        Assert.Equal((PolicyWriteOutcome.Unchanged, PolicyWriteOutcome.Unchanged), (result.Outcome, allResult.Outcome));
        Assert.Empty(_server.Writes);
        _server.Manager.Verify(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>()), Times.Never);
    }

    [Fact]
    public async Task AnAdminsDisable_BetweenTheReadAndTheReRead_RebuildsOnce_AndKeepsTheDisable()
    {
        // Case 2: "re-read differs: IsDisabled; rebuilding once"; the role's libraries written, the user still
        // disabled.
        var user = _server.Add("cc", allFolders: false, folders: [Movies]);
        _server.BeforeGetUserById = (id, call) =>
        {
            if (call == 2)
            {
                _server.AdminPost(id, p => p.IsDisabled = true);
            }
        };

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Movies, Shows]), TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.Written, result.Outcome);
        Assert.True(result.Rebuilt);
        Assert.Equal(["IsDisabled"], result.Differences);
        var stored = _server.Stored(user.Id);
        Assert.True(stored.HasPermission(PermissionKind.IsDisabled));
        Assert.Equal([Movies, Shows], stored.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ASecondAdminChange_DuringTheRebuild_AbortsTheWrite_AndLeavesTheAdminsEdits()
    {
        // Case 3: WriteAborted; the role result waits for the next reconcile.
        var user = _server.Add("cc", allFolders: false, folders: [Movies]);
        _server.BeforeGetUserById = (id, call) =>
        {
            if (call == 2)
            {
                _server.AdminPost(id, p => p.IsDisabled = true);
            }
            else if (call == 4)
            {
                _server.AdminPost(id, p => p.MaxActiveSessions = 9);
            }
        };

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Movies, Shows]), TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.Aborted, result.Outcome);
        Assert.Equal(["MaxActiveSessions"], result.Differences);
        var stored = _server.Stored(user.Id);
        Assert.True(stored.HasPermission(PermissionKind.IsDisabled));
        Assert.Equal(9, stored.MaxActiveSessions);
        Assert.Equal([Movies], stored.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Empty(_server.Writes);
    }

    [Fact]
    public async Task ALyricPermissionChange_BetweenTheReads_CountsAsAChange()
    {
        var user = _server.Add("lyrics", allFolders: false, folders: [Movies]);
        _server.BeforeGetUserById = (id, call) =>
        {
            if (call == 2)
            {
                _server.AdminPost(id, p => p.EnableLyricManagement = true);
            }
        };

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Shows]), TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.Written, result.Outcome);
        Assert.Equal([PolicyFields.EnableLyricManagement], result.Differences);
        Assert.True(_server.Stored(user.Id).HasPermission(PermissionKind.EnableLyricManagement));
    }

    [Fact]
    public async Task ABusyLock_TimesOut_WithoutAWrite()
    {
        // The user policy filter holds the same per-user lock around an admin's policy save.
        var user = _server.Add("locked", allFolders: false, folders: [Movies]);
        using var held = await _locks.TryAcquireAsync(user.Id, TimeSpan.Zero, TestContext.Current.CancellationToken);
        var writer = new PolicyWriter(_server.Manager.Object, _locks, NullLogger.Instance, _clock, TimeSpan.FromMilliseconds(30), committed: _server.Committed, settleDelay: TimeSpan.Zero);

        var result = await writer.WriteAsync(user.Id, LibraryFields.All, TestContext.Current.CancellationToken);

        Assert.NotNull(held);
        Assert.Equal(PolicyWriteOutcome.LockTimeout, result.Outcome);
        Assert.Empty(_server.Writes);
        Assert.Null(await _locks.TryAcquireAsync(user.Id, TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMissingUser_AndAFailedWrite_AreReported_NotThrown()
    {
        var gone = await Writer().WriteAsync(Guid.NewGuid(), LibraryFields.All, TestContext.Current.CancellationToken);
        var user = _server.Add("fails", allFolders: false, folders: [Movies]);
        _server.FailWrites = true;
        var writer = Writer();

        var failed = await writer.WriteAsync(user.Id, LibraryFields.All, TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.UserNotFound, gone.Outcome);
        Assert.Equal(PolicyWriteOutcome.Failed, failed.Outcome);
        Assert.Contains("refused", failed.Error, StringComparison.Ordinal);
        Assert.Equal(0, writer.PendingVerifications);
    }

    [Fact]
    public async Task AFailedSave_IsWrittenAgain_Forced_BecauseTheCacheMayAlreadyShowIt()
    {
        // Seen in a sandbox run: a sign-in that saved the user row first made our UpdatePolicyAsync fail, after 10.11.5 had
        // already put the new entity into its cache; the cache shows the access, the database doesn't hold it.
        var user = _server.Add("racer", allFolders: false, folders: [Movies]);
        _server.FailingSaves = 1;

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Shows]), TestContext.Current.CancellationToken);

        Assert.Equal((PolicyWriteOutcome.Written, true), (result.Outcome, result.RetriedAfterFailedSave));
        Assert.Equal([Shows], _server.Stored(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Single(_server.Writes);
    }

    [Fact]
    public async Task TwoFailedSaves_AreReportedAsAFailedSave()
    {
        var user = _server.Add("racer", allFolders: false, folders: [Movies]);
        _server.FailingSaves = 2;

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Shows]), TestContext.Current.CancellationToken);

        Assert.Equal((PolicyWriteOutcome.Failed, true, true), (result.Outcome, result.SaveFailed, result.RetriedAfterFailedSave));
        Assert.Equal([Shows], _server.Cached(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Equal([Movies], _server.Stored(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task AnAdminsSave_ThatCommitsFirst_AndMakesOursFail_IsKept_TheWriteAgainIsBuiltFromTheCommittedRow()
    {
        // A race (regression): an admin's Dashboard save (GET /Users/{id}, then POST /Users/{id}/Policy with the user
        // disabled and other fields changed) commits while our UpdatePolicyAsync is on its way; ours fails, and 10.11.5's
        // cache holds our entity, built from the stale read. The write again must not store those stale values.
        var user = _server.AddRich(Movies);
        var page = _server.Load(user.Id);
        var calls = 0;
        _server.DuringSave = id =>
        {
            if (calls++ == 0)
            {
                _server.AdminCommit(id, page, p =>
                {
                    p.IsDisabled = true;
                    p.MaxActiveSessions = 9;
                    p.MaxParentalRating = 7;
                    p.EnableLyricManagement = false;
                });
            }
        };
        var writer = Writer();

        var result = await writer.WriteAsync(user.Id, LibraryFields.Of(false, [Movies, Shows]), TestContext.Current.CancellationToken);

        Assert.Equal((PolicyWriteOutcome.Written, true), (result.Outcome, result.RetriedAfterFailedSave));
        Assert.Contains("affected 0 row", result.FailedSaveError, StringComparison.Ordinal);
        Assert.Equal(["EnableLyricManagement", "IsDisabled", "MaxActiveSessions", "MaxParentalRating"], result.CommittedFields);
        foreach (var stored in new[] { _server.Stored(user.Id), _server.Cached(user.Id) })
        {
            Assert.True(stored.HasPermission(PermissionKind.IsDisabled));
            Assert.False(stored.HasPermission(PermissionKind.IsAdministrator));
            Assert.False(stored.HasPermission(PermissionKind.EnableLyricManagement));
            Assert.Equal((9, (int?)7), (stored.MaxActiveSessions, stored.MaxParentalRatingScore));
            Assert.Equal([Movies, Shows], stored.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        }

        Assert.False(writer.HasFailedSave(user.Id));
    }

    [Fact]
    public async Task WithoutTheCommittedRow_TheSameRaceUndoesTheAdminsDisable_TheNegativeControl()
    {
        // The 0.9.3.0 build's write again: forced from the cache, which held our stale entity. The fake reproduces the loss.
        var user = _server.Add("cc", allFolders: false, folders: [Movies]);
        var page = _server.Load(user.Id);
        var stale = _server.Load(user.Id);
        stale.EnabledFolders = [Movies, Shows];
        _server.DuringSave = id => _server.AdminCommit(id, page, p => p.IsDisabled = true);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => _server.Manager.Object.UpdatePolicyAsync(user.Id, stale));
        _server.DuringSave = null;
        Assert.True(_server.Stored(user.Id).HasPermission(PermissionKind.IsDisabled));
        Assert.False(_server.Cached(user.Id).HasPermission(PermissionKind.IsDisabled));

        await _server.Manager.Object.UpdatePolicyAsync(user.Id, FakeJellyfinUsers.PolicyOf(_server.Cached(user.Id)));

        Assert.False(_server.Stored(user.Id).HasPermission(PermissionKind.IsDisabled));
    }

    [Fact]
    public async Task AnAdminsSave_ThatAlsoChangedTheLibraries_IsWrittenBackAsItIs_AndTheTargetWaits()
    {
        // A concurrent save that changed the library fields too: the plan the write came from is stale (the drift rules
        // decide next run), so the committed row is written back as it is, and the cache holds what the database holds.
        var user = _server.Add("cc", allFolders: false, folders: [Movies]);
        var page = _server.Load(user.Id);
        var calls = 0;
        _server.DuringSave = id =>
        {
            if (calls++ == 0)
            {
                _server.AdminCommit(id, page, p =>
                {
                    p.IsDisabled = true;
                    p.EnabledFolders = [Shows];
                });
            }
        };

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Movies, Kids]), TestContext.Current.CancellationToken);

        Assert.Equal((PolicyWriteOutcome.Resynced, true), (result.Outcome, result.RetriedAfterFailedSave));
        Assert.Equal([Shows], result.Target.EnabledFolders);
        foreach (var stored in new[] { _server.Stored(user.Id), _server.Cached(user.Id) })
        {
            Assert.True(stored.HasPermission(PermissionKind.IsDisabled));
            Assert.Equal([Shows], stored.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        }
    }

    [Fact]
    public async Task AnAdminsDemotion_ThatCommitsFirst_IsNeverUndone()
    {
        var user = _server.Add("admin2", admin: true, allFolders: true);
        var page = _server.Load(user.Id);
        var calls = 0;
        _server.DuringSave = id =>
        {
            if (calls++ == 0)
            {
                _server.AdminCommit(id, page, p => p.IsAdministrator = false);
            }
        };

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Movies]), TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.Written, result.Outcome);
        Assert.Contains("IsAdministrator", result.CommittedFields);
        Assert.False(_server.Stored(user.Id).HasPermission(PermissionKind.IsAdministrator));
        Assert.False(_server.Cached(user.Id).HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task ACacheThatDisagreesWithTheDatabase_IsWaitedForOnce_ThenTheWriteIsBuiltFromTheCommittedRow()
    {
        // The stale object (as on 10.11.5) after an admin's committed disable and rating change: the cache shows the user enabled,
        // the database disabled. Writing from the cache would undo the admin's change in the database too.
        var user = _server.Add("cc", allFolders: false, folders: [Movies]);
        _server.AdminPost(user.Id, p =>
        {
            p.IsDisabled = true;
            p.MaxParentalRating = 7;
        });
        _server.ChangeCacheOnly(user.Id, u =>
        {
            u.SetPermission(PermissionKind.IsDisabled, false);
            u.MaxParentalRatingScore = null;
        });
        var reads = _server.CommittedReads;

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Shows]), TestContext.Current.CancellationToken);

        Assert.Equal((PolicyWriteOutcome.Written, true), (result.Outcome, result.Rebuilt));
        Assert.Equal(2, _server.CommittedReads - reads);
        Assert.Equal(["IsDisabled", "MaxParentalRating"], result.CommittedFields);
        foreach (var stored in new[] { _server.Stored(user.Id), _server.Cached(user.Id) })
        {
            Assert.True(stored.HasPermission(PermissionKind.IsDisabled));
            Assert.Equal(7, stored.MaxParentalRatingScore);
            Assert.Equal([Shows], stored.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        }
    }

    [Fact]
    public async Task ALockoutTheDatabaseNeverGot_StaysOn_TheWriterNeverEnablesAUser()
    {
        // A lockout (SetPermission + UpdateUserAsync) isn't persisted on 10.11.5 (jellyfin/jellyfin#16298): the server
        // enforces the user as disabled while the database has them enabled. The write keeps them disabled, at once.
        var user = _server.Add("locked", allFolders: false, folders: [Movies]);
        _server.ChangeCacheOnly(user.Id, u =>
        {
            u.SetPermission(PermissionKind.IsDisabled, true);
            u.InvalidLoginAttemptCount = 3;
        });

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Shows]), TestContext.Current.CancellationToken);

        Assert.Equal((PolicyWriteOutcome.Written, false, true), (result.Outcome, result.Rebuilt, result.KeptDisabled));
        Assert.Empty(result.CommittedFields);
        Assert.True(_server.Stored(user.Id).HasPermission(PermissionKind.IsDisabled));
        Assert.True(_server.Cached(user.Id).HasPermission(PermissionKind.IsDisabled));
    }

    [Fact]
    public async Task ANativeEditSinceTheCallersRead_IsNeverWrittenOver()
    {
        // The "expected before" check: the caller planned against [Movies]; an admin set [Shows] since.
        var user = _server.Add("edited", allFolders: false, folders: [Movies]);
        _server.AdminPost(user.Id, p => p.EnabledFolders = [Shows]);

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Kids]), TestContext.Current.CancellationToken, expected: LibraryFields.Of(false, [Movies]));

        Assert.Equal((PolicyWriteOutcome.Aborted, true), (result.Outcome, result.NativeChanged));
        Assert.Equal([Shows], result.Before!.EnabledFolders);
        Assert.Empty(_server.Writes);
        Assert.Equal([Shows], _server.Stored(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ACancelDuringTheWaitBeforeTheWriteAgain_KeepsTheFailedSave_AndTheNextWriteMakesIt()
    {
        var user = _server.Add("racer", allFolders: false, folders: [Movies]);
        _server.FailingSaves = 1;
        using var cancel = new CancellationTokenSource();
        var writer = new PolicyWriter(_server.Manager.Object, _locks, NullLogger.Instance, _clock, saveRetryDelay: TimeSpan.FromSeconds(1), committed: _server.Committed, settleDelay: TimeSpan.Zero);

        var pending = writer.WriteAsync(user.Id, LibraryFields.Of(false, [Shows]), cancel.Token);
        await cancel.CancelAsync();
        var result = await pending;

        Assert.Equal((PolicyWriteOutcome.Failed, true, true), (result.Outcome, result.SaveFailed, result.Cancelled));
        Assert.True(writer.HasFailedSave(user.Id));
        Assert.Equal([Movies], _server.Stored(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        // The cache shows the access already, but the failed save makes the next write a real one.
        var again = await writer.WriteAsync(user.Id, LibraryFields.Of(false, [Shows]), TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.Written, again.Outcome);
        Assert.Equal([Shows], _server.Stored(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.False(writer.HasFailedSave(user.Id));
    }

    [Fact]
    public async Task AFailedSave_WithoutTheCommittedRow_IsNotWrittenAgain_AndStaysRecorded()
    {
        var user = _server.Add("racer", allFolders: false, folders: [Movies]);
        _server.FailingSaves = 1;
        _server.BeforeCommittedRead = _ => _server.CommittedUnavailable = _server.FailingSaves == 0;
        var writer = Writer();

        var result = await writer.WriteAsync(user.Id, LibraryFields.Of(false, [Shows]), TestContext.Current.CancellationToken);

        Assert.Equal((PolicyWriteOutcome.Aborted, true, true), (result.Outcome, result.CommittedUnavailable, result.RetriedAfterFailedSave));
        Assert.True(writer.HasFailedSave(user.Id));
        Assert.True(writer.ForgetFailedSave(user.Id));
        Assert.Empty(writer.FailedSaves);
    }

    [Fact]
    public async Task Resync_HealsTheSignInRace_FromTheCommittedRow_AndLeavesAnEditAlone()
    {
        var user = _server.Add("race", allFolders: false, folders: [Movies]);
        var writer = Writer();
        await writer.WriteAsync(user.Id, LibraryFields.Of(false, [Movies, Shows]), TestContext.Current.CancellationToken);
        _server.SignInRace(user.Id);

        var healed = await writer.ResyncAsync(user.Id, LibraryFields.Of(false, [Movies]), attempt: 1, TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.Resynced, healed.Outcome);
        Assert.Equal(new[] { Movies, Shows }.Order(), _server.Cached(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Order());

        // An admin's edit back to [Movies] is in the database too: nothing to heal.
        _server.AdminPost(user.Id, p => p.EnabledFolders = [Movies]);
        var edit = await writer.ResyncAsync(user.Id, LibraryFields.Of(false, [Movies]), attempt: 1, TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.Unchanged, edit.Outcome);
        Assert.Equal([Movies], _server.Cached(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        _server.CommittedUnavailable = true;
        Assert.True((await writer.ResyncAsync(user.Id, LibraryFields.Of(false, [Movies]), attempt: 1, TestContext.Current.CancellationToken)).CommittedUnavailable);
    }

    [Fact]
    public async Task AWrite_SchedulesItsCheck_FiveSecondsLater_WhichFindsWhatTheServerEnforces()
    {
        var user = _server.Add("kid", allFolders: true);
        var writer = Writer();
        var scheduled = 0;
        writer.VerificationScheduled += (_, _) => scheduled++;

        await writer.WriteAsync(user.Id, LibraryFields.Of(false, [Movies]), TestContext.Current.CancellationToken);

        Assert.Equal((1, 1), (scheduled, writer.PendingVerifications));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime + PolicyWriter.VerifyDelay, writer.NextVerificationDue);
        Assert.Empty(writer.TakeDue(_clock.GetUtcNow().UtcDateTime));
        _clock.Advance(PolicyWriter.VerifyDelay);
        var check = Assert.Single(writer.TakeDue(_clock.GetUtcNow().UtcDateTime));
        Assert.Equal((LibraryFields.All.EnableAllFolders, 0), (check.Before.EnableAllFolders, check.Attempt));
        Assert.Equal(VerificationOutcome.Holds, writer.Verify(check).Outcome);
        Assert.Equal(0, writer.PendingVerifications);
    }

    [Fact]
    public async Task TheCheck_FindsTheSignInRacesRevert_InTheCache_WhileTheDatabaseHoldsTheWrite()
    {
        // As on 10.11.5: the cache holds the pre-write user again; GetUserDto(GetUserById(id)) is what the server enforces.
        var user = _server.Add("race", allFolders: false, folders: [Movies]);
        var writer = Writer();
        await writer.WriteAsync(user.Id, LibraryFields.Of(false, [Movies, Shows]), TestContext.Current.CancellationToken);
        _server.SignInRace(user.Id);
        _clock.Advance(PolicyWriter.VerifyDelay);

        var result = writer.Verify(Assert.Single(writer.TakeDue(_clock.GetUtcNow().UtcDateTime)));

        Assert.Equal(VerificationOutcome.Reverted, result.Outcome);
        Assert.Equal([Movies], result.Found!.EnabledFolders);
        Assert.Equal([Movies, Shows], _server.Stored(user.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task TheCheck_TellsAnotherEdit_FromARevert_AndAGoneUser()
    {
        var user = _server.Add("edited", allFolders: false, folders: [Movies]);
        var writer = Writer();
        await writer.WriteAsync(user.Id, LibraryFields.Of(false, [Shows]), TestContext.Current.CancellationToken);
        _server.AdminPost(user.Id, p => p.EnableAllFolders = true);
        _clock.Advance(PolicyWriter.VerifyDelay);
        var check = Assert.Single(writer.TakeDue(_clock.GetUtcNow().UtcDateTime));

        Assert.Equal(VerificationOutcome.Changed, writer.Verify(check).Outcome);
        _server.Delete(user.Id);
        Assert.Equal(VerificationOutcome.UserNotFound, writer.Verify(check).Outcome);
    }

    [Fact]
    public async Task TheLoopGuard_KnowsTheWritersOwnRecentWrite()
    {
        var user = _server.Add("guard", allFolders: true);
        var writer = Writer();
        var target = LibraryFields.Of(false, [Movies]);
        await writer.WriteAsync(user.Id, target, TestContext.Current.CancellationToken);

        Assert.True(writer.IsOwnRecentWrite(user.Id, LibraryFields.Of(false, [Movies])));
        Assert.False(writer.IsOwnRecentWrite(user.Id, LibraryFields.All));
        Assert.False(writer.IsOwnRecentWrite(Guid.NewGuid(), target));
        _clock.Advance(PolicyWriter.OwnWriteWindow + TimeSpan.FromSeconds(1));
        Assert.False(writer.IsOwnRecentWrite(user.Id, target));
    }

    [Fact]
    public void OnlyThePolicyWriter_CallsTheUserManagersWrites()
    {
        // PolicyWriter is the only code in the suite that writes a user's policy, and nothing
        // writes users another way (SetPermission + UpdateUserAsync isn't persisted on 10.11.5, jellyfin/jellyfin#16298).
        string[] writes = [nameof(IUserManager.UpdatePolicyAsync), nameof(IUserManager.UpdateUserAsync), nameof(IUserManager.UpdateConfigurationAsync)];
        var callers = typeof(Plugin).Assembly.GetTypes()
            .Where(t => t.DeclaringType is null)
            .SelectMany(t => MethodBodyReferences.DeclaredMethods(t).Select(m => (Type: t, Method: m)))
            .Where(c => MethodBodyReferences.FindMembers(c.Method).OfType<MethodInfo>().Any(m => m.DeclaringType == typeof(IUserManager) && writes.Contains(m.Name)))
            .Select(c => c.Type.FullName)
            .Distinct()
            .ToList();

        Assert.Equal([typeof(PolicyWriter).FullName], callers);
    }

    [Fact]
    public async Task AUserWhoAlreadyHasTheTarget_IsUnchanged_EvenWhenTheCallerPlannedAgainstOtherAccess()
    {
        // Since 0.9.4 an admin's save of a managed user is pre-merged with the rules' result, so a run that planned against
        // the access before that save finds the target written already: nothing to do, not "changed since".
        var user = _server.Add("premerged", allFolders: false, folders: [Movies]);
        _server.AdminPost(user.Id, p => p.EnabledFolders = [Kids]);

        var result = await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Kids]), TestContext.Current.CancellationToken, expected: LibraryFields.Of(false, [Movies]));

        Assert.Equal((PolicyWriteOutcome.Unchanged, false), (result.Outcome, result.NativeChanged));
        Assert.Empty(_server.Writes);
    }

    [Fact]
    public async Task AnAdminSaveThePreMergeWrote_IsRecordedAsAWrite_ItsCheckScheduled_AndAFailedSaveOfOursForgotten()
    {
        var user = _server.Add("saved", allFolders: true);
        var writer = Writer();
        _server.FailingSaves = 2;
        var failed = await writer.WriteAsync(user.Id, LibraryFields.Of(false, [Kids]), TestContext.Current.CancellationToken);
        Assert.True(failed.SaveFailed || writer.HasFailedSave(user.Id));

        writer.RecordAdminSave(user.Id, LibraryFields.Of(false, [Kids]), LibraryFields.All);

        Assert.False(writer.HasFailedSave(user.Id));
        Assert.Equal(1, writer.PendingVerifications);
        Assert.True(writer.IsOwnRecentWrite(user.Id, LibraryFields.Of(false, [Kids])));

        // A save that left the library fields as they were schedules nothing.
        var other = _server.Add("same", allFolders: false, folders: [Shows]);
        writer.RecordAdminSave(other.Id, LibraryFields.Of(false, [Shows]), LibraryFields.Of(false, [Shows]));
        Assert.Equal(1, writer.PendingVerifications);
    }

    // The optional mirror's write (off by default): only EnableUserPreferenceAccess changes among every stored field, the
    // lyric flag kept; refused, with nothing written, when the flag isn't the one the caller found.
    [Fact]
    public async Task APreferenceWrite_ChangesOnlyThatFlag_AndIsRefusedWhenItIsntTheOneTheCallerFound()
    {
        var user = _server.AddRich(Movies);
        var before = FakeJellyfinUsers.Dump(_server.Stored(user.Id));
        var writer = Writer();

        var refused = await writer.WritePreferenceAccessAsync(user.Id, value: false, expected: true, TestContext.Current.CancellationToken);
        var stale = await writer.WritePreferenceAccessAsync(user.Id, value: true, expected: true, TestContext.Current.CancellationToken);
        Assert.Empty(_server.Writes);
        var written = await writer.WritePreferenceAccessAsync(user.Id, value: true, expected: false, TestContext.Current.CancellationToken);

        Assert.Equal((PolicyWriteOutcome.Unchanged, PolicyWriteOutcome.Aborted, true), (refused.Outcome, stale.Outcome, stale.NativeChanged));
        Assert.Equal((PolicyWriteOutcome.Written, true, false), (written.Outcome, written.Holds, written.Before));
        var after = FakeJellyfinUsers.Dump(_server.Stored(user.Id));
        Assert.Equal(["User.EnableUserPreferenceAccess", "User.RowVersion"], FakeJellyfinUsers.Differences(before, after));
        Assert.Equal("True", after["Permission.EnableLyricManagement"]);
    }

    // A stale cache (the sign-in race after a library write, as on 10.11.5) must not write its old libraries over the database's:
    // the committed row is the base when they disagree, after the one wait.
    [Fact]
    public async Task APreferenceWrite_FromAStaleCache_KeepsTheDatabasesLibraries()
    {
        var user = _server.Add("kid", allFolders: false, folders: [Movies]);
        await Writer().WriteAsync(user.Id, LibraryFields.Of(false, [Kids]), TestContext.Current.CancellationToken);
        _server.SignInRace(user.Id);
        Assert.Equal([Movies], FakeJellyfinUsers.PolicyOf(_server.Cached(user.Id)).EnabledFolders);

        var written = await Writer().WritePreferenceAccessAsync(user.Id, value: false, expected: true, TestContext.Current.CancellationToken);

        Assert.Equal(PolicyWriteOutcome.Written, written.Outcome);
        Assert.Equal([Kids], FakeJellyfinUsers.PolicyOf(_server.Stored(user.Id)).EnabledFolders);
        Assert.Equal([Kids], FakeJellyfinUsers.PolicyOf(_server.Cached(user.Id)).EnabledFolders);
        Assert.False(_server.Stored(user.Id).EnableUserPreferenceAccess);
    }

    private PolicyWriter Writer() => new(_server.Manager.Object, _locks, NullLogger.Instance, _clock, saveRetryDelay: TimeSpan.Zero, committed: _server.Committed, settleDelay: TimeSpan.Zero);
}
