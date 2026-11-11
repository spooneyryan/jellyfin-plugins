using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// The only code of the suite that writes a user's policy (the kit's rule: one writer per plugin).
/// It writes nothing but <c>EnableAllFolders</c> and <c>EnabledFolders</c>, always as a full policy, and never changes a
/// user's other fields from what an admin last saved: <c>IsAdministrator</c> and <c>IsDisabled</c> least of all.
/// </summary>
/// <remarks>
/// <para>Each write, under the user's lock (<see cref="UserPolicyLocks"/>, shared with the user policy filter):</para>
/// <list type="number">
/// <item>reads the user's full current policy fresh, <c>GetUserDto(GetUserById(id)).Policy</c>: on 10.11.5 the user
/// manager's cached object, which the server enforces; a policy with missing fields would reset 29-32 of them (measured
/// on 10.11.5), so a partial policy is never sent;</item>
/// <item>skips the write when the user already has the target, and otherwise writes nothing when the library fields aren't
/// the ones the caller planned against (<c>expected</c>: an edit since the caller's read);</item>
/// <item>reads the user's committed row (<see cref="ICommittedUsers"/>, read only), then re-reads the cache. A
/// re-read that differs in any field but the library ones (the lyric permission too) means someone changed the user while
/// the write was built: it builds again once, <see cref="SettleDelay"/> later, and on a second difference
/// aborts. A committed row that differs from the cache (in any field but the library ones and
/// <c>InvalidLoginAttemptCount</c>, which sign-ins change) is waited for once too, because 10.11.5 puts a save's entity into
/// its cache before it saves (<c>UserManager.cs:719-720</c>), so an admin's Dashboard save may be on its way; if it still
/// differs, the write is built from the committed row, since the cache then holds a save that failed or a sign-in's stale
/// object, and the database what an admin last saved;</item>
/// <item>sets the two library fields, and <c>EnableLyricManagement</c> from the user's permission, because
/// <c>GetUserDto</c> never fills it and <c>UpdatePolicyAsync</c> would clear it (seen on 10.11.5);</item>
/// <item>never enables a user: when the server enforces <c>IsDisabled</c> and the committed row doesn't have it (a lockout
/// 10.11.5 never saves, jellyfin/jellyfin#16298), the write keeps it on;</item>
/// <item>writes, and schedules a check <see cref="VerifyDelay"/> later that the server still enforces what was written
/// (<see cref="Verify"/>: on 10.11.5 a sign-in racing the write can put the old user back into the cache;
/// <see cref="ResyncAsync"/> heals that from the committed row);</item>
/// <item>when <c>UpdatePolicyAsync</c> throws (a concurrent save came first: a sign-in's, or an admin's Dashboard save,
/// whose values the cache then no longer shows, because 10.11.5 put our entity into its cache before it saved), it keeps the
/// committed row it read as the failed save's base and writes once more <see cref="SaveRetryDelay"/> later, from the
/// committed row: the fields a concurrent save changed come from it (a forced write from the cache would store
/// the writer's stale values over an admin's save, <c>IsDisabled</c> included), and when that save changed the library
/// fields too, the committed row is written back as it is (<see cref="PolicyWriteOutcome.Resynced"/>) and the target waits
/// for the next run's rules. Until a write succeeds <see cref="HasFailedSave"/> says so, and every write for the user is
/// built that way.</item>
/// </list>
/// <para>Never <c>SetPermission</c> + <c>UpdateUserAsync</c>: on 10.11.5 that isn't persisted (jellyfin/jellyfin#16298).
/// A test checks that no other type of the plugin calls <c>UpdatePolicyAsync</c>, <c>UpdateUserAsync</c> or
/// <c>UpdateConfigurationAsync</c>. Logs hold ids and counts only.</para>
/// </remarks>
internal sealed class PolicyWriter
{
    /// <summary>How long a write waits for the user's lock (an admin's policy save holds it).</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long after a write the server is asked again what it enforces (about 5 s).</summary>
    public static readonly TimeSpan VerifyDelay = TimeSpan.FromSeconds(5);

    /// <summary>How long after a failed save the write is made again, from the committed row.</summary>
    public static readonly TimeSpan SaveRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a write waits before it builds again after the user changed while it was built, or while the cache and the
    /// database disagreed: long enough for a save on its way to land (10.11.5 sets its cache before <c>SaveChangesAsync</c>).
    /// </summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a write counts as the writer's own, for the loop guard (12.1+ publishes an event per write).</summary>
    public static readonly TimeSpan OwnWriteWindow = TimeSpan.FromSeconds(30);

    private static readonly string[] _libraryFields = [PolicyFields.EnableAllFolders, PolicyFields.EnabledFolders];
    private static readonly string[] _notAgreed = [PolicyFields.EnableAllFolders, PolicyFields.EnabledFolders, PolicyFields.InvalidLoginAttemptCount];

    private readonly IUserManager _userManager;
    private readonly UserPolicyLocks _locks;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeSpan _saveRetryDelay;
    private readonly TimeSpan _settleDelay;
    private readonly ICommittedUsers? _committed;
    private readonly object _pendingLock = new();
    private readonly List<PendingVerification> _pending = [];
    private readonly ConcurrentDictionary<Guid, (DateTime Utc, LibraryFields Fields)> _recent = new();
    private readonly ConcurrentDictionary<Guid, FailedSave> _failed = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PolicyWriter"/> class.
    /// </summary>
    /// <param name="userManager">The server's user manager.</param>
    /// <param name="locks">The per-user locks.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="lockTimeout">How long a write waits for the user's lock; <see cref="LockTimeout"/> by default.</param>
    /// <param name="saveRetryDelay">How long after a failed save the write is made again; <see cref="SaveRetryDelay"/> by default.</param>
    /// <param name="committed">The committed rows (the server's database, read only); <c>null</c> when it can't be read,
    /// which leaves the cache's checks only, and no write again after a failed save.</param>
    /// <param name="settleDelay">How long a write waits before it builds again; <see cref="SettleDelay"/> by default.</param>
    public PolicyWriter(
        IUserManager userManager,
        UserPolicyLocks locks,
        ILogger logger,
        TimeProvider? timeProvider = null,
        TimeSpan? lockTimeout = null,
        TimeSpan? saveRetryDelay = null,
        ICommittedUsers? committed = null,
        TimeSpan? settleDelay = null)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(logger);
        _userManager = userManager;
        _locks = locks;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lockTimeout = lockTimeout ?? LockTimeout;
        _saveRetryDelay = saveRetryDelay ?? SaveRetryDelay;
        _settleDelay = settleDelay ?? SettleDelay;
        _committed = committed;
        if (committed is null)
        {
            _logger.LogWarning("Access Roles can't read users' stored rows: its writes check the server's cache only, and a write whose save failed isn't made again");
        }
    }

    /// <summary>Raised when a post-write check is scheduled, so the library sync can wake up for it.</summary>
    public event EventHandler? VerificationScheduled;

    /// <summary>Gets how many post-write checks are waiting.</summary>
    public int PendingVerifications
    {
        get
        {
            lock (_pendingLock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Gets when the next post-write check is due, UTC, or <c>null</c> when none waits.</summary>
    public DateTime? NextVerificationDue
    {
        get
        {
            lock (_pendingLock)
            {
                return _pending.Count == 0 ? null : _pending.Min(p => p.DueUtc);
            }
        }
    }

    /// <summary>Gets the users whose last save failed with no write succeeding since: the cache may hold our entity.</summary>
    public IReadOnlyCollection<Guid> FailedSaves => [.. _failed.Keys];

    /// <summary>
    /// Gets whether a user's last save failed with no write succeeding since (the library sync writes them again, forced:
    /// <c>RewriteAfterFailedSave</c>).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns><c>true</c> while the server's cache may hold an entity the database doesn't.</returns>
    public bool HasFailedSave(Guid userId) => _failed.ContainsKey(userId);

    /// <summary>
    /// Records a save of a user's library fields that the core made for an admin's (or an API key's) own full-policy save,
    /// pre-merged with the rules' result by the user policy filter under the user's lock: the
    /// loop guard's record and the post-write check, as for the writer's own writes, since a sign-in racing it can put the
    /// old cached user back the same way; and a failed save of ours is forgotten, because the database holds
    /// the admin's save now. Nothing is written here.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="written">The library fields the save stored.</param>
    /// <param name="before">The library fields the server enforced before it.</param>
    public void RecordAdminSave(Guid userId, LibraryFields written, LibraryFields before)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(before);
        _failed.TryRemove(userId, out _);
        if (!written.Same(before))
        {
            Wrote(userId, written, before, attempt: 0);
        }
    }

    /// <summary>
    /// Forgets a user's failed save, when nothing is to be written for them any more: they left library management or were
    /// deleted (otherwise the record kept a retry run every 30 s going until a restart).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns><c>true</c> when there was one.</returns>
    public bool ForgetFailedSave(Guid userId) => _failed.TryRemove(userId, out _);

    /// <summary>
    /// Writes a user's library fields (the steps in the class remarks).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="target">The library fields to write.</param>
    /// <param name="cancellationToken">Cancels waiting for the user's lock and the wait before a write again; once a save
    /// has failed, the write again is made whatever the token says.</param>
    /// <param name="attempt">0 for a write; 1 for the write again after a revert (its check won't write a third time).</param>
    /// <param name="force">Whether to write even when the user manager's object already shows the fields, from the committed
    /// row (after a failed save, which may have left the cache ahead of the database).</param>
    /// <param name="expected">The library fields the caller planned against: when the fresh read finds others (an edit since
    /// the caller's read), nothing is written (<see cref="PolicyWriteResult.NativeChanged"/>); <c>null</c> for no check.</param>
    /// <param name="lockTimeout">How long to wait for the user's lock; the writer's own by default (the sign-in hook's 5 s).</param>
    /// <returns>What happened; this never throws for a failed read or write.</returns>
    public async Task<PolicyWriteResult> WriteAsync(Guid userId, LibraryFields target, CancellationToken cancellationToken, int attempt = 0, bool force = false, LibraryFields? expected = null, TimeSpan? lockTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var timeout = lockTimeout ?? _lockTimeout;
        using var lease = await _locks.TryAcquireAsync(userId, timeout, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _logger.LogWarning("Access Roles didn't write the library access of user {UserId}: their policy lock was busy for {Seconds} s", userId, timeout.TotalSeconds);
            return new PolicyWriteResult(PolicyWriteOutcome.LockTimeout, target);
        }

        var result = await WriteLockedAsync(userId, target, attempt, force, expected, cancellationToken).ConfigureAwait(false);
        if (result is not { Outcome: PolicyWriteOutcome.Failed, SaveFailed: true })
        {
            return result;
        }

        _logger.LogWarning("Access Roles writes the library access of user {UserId} again in {Seconds} s, from the stored row: the save failed, and the server may enforce what the database doesn't hold", userId, _saveRetryDelay.TotalSeconds);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_saveRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The failed save stays recorded (HasFailedSave): the next run writes it again.
            return result with { Cancelled = true };
        }

        // Once a save has failed, the write again is made whatever happens to the caller: the cache may hold our entity.
        var again = await WriteLockedAsync(userId, target, attempt, force: true, expected: null, CancellationToken.None).ConfigureAwait(false);
        return again with { RetriedAfterFailedSave = true, FailedSaveError = result.Error };
    }

    /// <summary>
    /// Writes a user's committed row back as it is, so the server's cache holds what its database holds (the post-write
    /// check's heal: on 10.11.5 a sign-in racing a write puts the old cached object back while the database keeps the write).
    /// Nothing is written when the cache no longer shows <paramref name="expectedCache"/>, or when the database
    /// holds the same library fields as the cache (an edit someone saved, not a stale cache).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="expectedCache">The library fields the check found the server enforcing.</param>
    /// <param name="attempt">The attempt the write's own check carries (1: no third write).</param>
    /// <param name="cancellationToken">Cancels waiting for the user's lock and the read.</param>
    /// <returns><see cref="PolicyWriteOutcome.Resynced"/> for a write, <see cref="PolicyWriteOutcome.Unchanged"/> when the
    /// database holds the cache's library fields, or why nothing was written.</returns>
    public async Task<PolicyWriteResult> ResyncAsync(Guid userId, LibraryFields expectedCache, int attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedCache);
        using var lease = await _locks.TryAcquireAsync(userId, _lockTimeout, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return new PolicyWriteResult(PolicyWriteOutcome.LockTimeout, expectedCache);
        }

        if (ReadCache(userId) is not { } cache)
        {
            return new PolicyWriteResult(PolicyWriteOutcome.UserNotFound, expectedCache);
        }

        if (cache.Error is { } readError)
        {
            return new PolicyWriteResult(PolicyWriteOutcome.Failed, expectedCache) { Error = readError };
        }

        var before = LibraryOf(cache.Policy);
        if (!expectedCache.Same(before))
        {
            return new PolicyWriteResult(PolicyWriteOutcome.Aborted, expectedCache) { Before = before, NativeChanged = true };
        }

        var committed = await ReadCommittedAsync(userId, cancellationToken).ConfigureAwait(false);
        if (committed.Missing)
        {
            _failed.TryRemove(userId, out _);
            return new PolicyWriteResult(PolicyWriteOutcome.UserNotFound, expectedCache) { Before = before };
        }

        if (committed.Row is not { } row)
        {
            return new PolicyWriteResult(PolicyWriteOutcome.Aborted, expectedCache) { Before = before, CommittedUnavailable = true, Error = "The user's stored row couldn't be read." };
        }

        var stored = LibraryOf(row.Policy);
        if (stored.Same(before))
        {
            return new PolicyWriteResult(PolicyWriteOutcome.Unchanged, stored) { Before = before };
        }

        var write = row.Policy;
        var changed = PolicyFields.Differences(Fields(cache.Policy, cache.Lyric), row.Fields, _notAgreed);
        var keptDisabled = KeepDisabled(cache.Policy, write, []);
        write.EnableLyricManagement = row.Lyric;
        try
        {
            await _userManager.UpdatePolicyAsync(userId, write).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles' write of the stored policy of user {UserId} failed", userId);
            _failed[userId] = new FailedSave(row.Fields, stored);
            return new PolicyWriteResult(PolicyWriteOutcome.Failed, stored) { Before = before, Error = ex.Message, SaveFailed = true };
        }

        _failed.TryRemove(userId, out _);
        Wrote(userId, stored, before, attempt);
        _logger.LogInformation("Access Roles wrote the stored policy of user {UserId} back, so the server enforces what its database holds: {Before} → {After}", userId, before, stored);
        return new PolicyWriteResult(PolicyWriteOutcome.Resynced, stored) { Before = before, CommittedFields = changed, KeptDisabled = keptDisabled };
    }

    /// <summary>
    /// Writes a user's native <c>EnableUserPreferenceAccess</c> and nothing else (the optional mirror,
    /// <c>MirrorPreferenceLockToPolicy</c>, off by default), under the user's lock, with the library write's checks:
    /// the full policy read fresh, nothing written when the flag isn't <paramref name="expected"/> (someone changed it since
    /// the caller looked), the re-read just before the write (a change while it was built: built again once
    /// <see cref="SettleDelay"/> later, then aborted), the committed row as the base when the cache and the database disagree
    /// in any field (waited for once; after a failed save of ours, always), <c>EnableLyricManagement</c> restored, never a
    /// user enabled. The flag is a user-row column on 10.11.5, which the sign-in race can write back stale, so it
    /// is read again right after the write (<see cref="PreferenceWriteResult.Holds"/>), and every run of the mirror checks it
    /// again.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="value">The flag to write.</param>
    /// <param name="expected">The flag the caller found.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock and the settle delay.</param>
    /// <param name="lockTimeout">How long to wait for the user's lock; the writer's own by default.</param>
    /// <returns>What happened; this never throws for a failed read or write.</returns>
    public async Task<PreferenceWriteResult> WritePreferenceAccessAsync(Guid userId, bool value, bool expected, CancellationToken cancellationToken, TimeSpan? lockTimeout = null)
    {
        var timeout = lockTimeout ?? _lockTimeout;
        using var lease = await _locks.TryAcquireAsync(userId, timeout, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _logger.LogWarning("Access Roles didn't write the preference access of user {UserId}: their policy lock was busy for {Seconds} s", userId, timeout.TotalSeconds);
            return new PreferenceWriteResult(PolicyWriteOutcome.LockTimeout, null);
        }

        var failed = _failed.ContainsKey(userId);
        for (var round = 0; round < 2; round++)
        {
            if (round > 0 && _settleDelay > TimeSpan.Zero)
            {
                await Task.Delay(_settleDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            if (ReadCache(userId) is not { } cache)
            {
                return new PreferenceWriteResult(PolicyWriteOutcome.UserNotFound, null);
            }

            if (cache.Error is { } readError)
            {
                return new PreferenceWriteResult(PolicyWriteOutcome.Failed, null) { Error = readError };
            }

            var before = cache.Policy.EnableUserPreferenceAccess;
            if (!failed && before == value)
            {
                return new PreferenceWriteResult(PolicyWriteOutcome.Unchanged, before) { Holds = true };
            }

            if (before != expected && !(failed && before == value))
            {
                return new PreferenceWriteResult(PolicyWriteOutcome.Aborted, before) { NativeChanged = true };
            }

            var snapshot = Fields(cache.Policy, cache.Lyric);
            var committed = await ReadCommittedAsync(userId, cancellationToken).ConfigureAwait(false);
            if (committed.Missing)
            {
                _failed.TryRemove(userId, out _);
                return new PreferenceWriteResult(PolicyWriteOutcome.UserNotFound, before);
            }

            if (failed && committed.Row is null)
            {
                return new PreferenceWriteResult(PolicyWriteOutcome.Aborted, before) { Error = "The user's stored row couldn't be read." };
            }

            if (ReadCache(userId) is not { Error: null } again)
            {
                return new PreferenceWriteResult(PolicyWriteOutcome.Failed, before) { Error = "The user couldn't be read again." };
            }

            var differences = PolicyFields.Differences(snapshot, Fields(again.Policy, again.Lyric), []);
            if (differences.Count > 0)
            {
                if (round == 0)
                {
                    continue;
                }

                _logger.LogWarning("Access Roles didn't write the preference access of user {UserId}: their policy changed again while it rebuilt the write ({Count} fields)", userId, differences.Count);
                return new PreferenceWriteResult(PolicyWriteOutcome.Aborted, before) { Differences = differences };
            }

            // The base: the cache's, unless it and the committed row disagree (the library fields included: a stale cache
            // after the sign-in race must not write its old libraries over the database's) or a save of ours failed.
            var write = cache.Policy;
            var lyric = cache.Lyric;
            if (committed.Row is { } row)
            {
                var disagree = PolicyFields.Differences(snapshot, row.Fields, [PolicyFields.InvalidLoginAttemptCount]);
                if (!failed && round == 0 && disagree.Any(f => !(string.Equals(f, nameof(UserPolicy.IsDisabled), StringComparison.Ordinal) && cache.Policy.IsDisabled)))
                {
                    continue;
                }

                if (failed || disagree.Count > 0)
                {
                    write = row.Policy;
                    lyric = row.Lyric;
                }
            }

            KeepDisabled(cache.Policy, write, []);
            write.EnableUserPreferenceAccess = value;
            write.EnableLyricManagement = lyric;
            try
            {
                await _userManager.UpdatePolicyAsync(userId, write).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Access Roles' write of the preference access of user {UserId} failed", userId);
                _failed[userId] = committed.Row is { } stored ? new FailedSave(stored.Fields, LibraryOf(stored.Policy)) : new FailedSave(null, null);
                return new PreferenceWriteResult(PolicyWriteOutcome.Failed, before) { Error = ex.Message, SaveFailed = true };
            }

            _failed.TryRemove(userId, out _);
            _recent[userId] = (_timeProvider.GetUtcNow().UtcDateTime, LibraryOf(write));
            var holds = ReadCache(userId) is { Error: null } check && check.Policy.EnableUserPreferenceAccess == value;
            if (!holds)
            {
                _logger.LogWarning("After Access Roles wrote the preference access of user {UserId}, the server doesn't enforce it; the mirror's next run writes it again", userId);
            }

            _logger.LogInformation("Access Roles wrote the preference access of user {UserId}: {Before} → {After}", userId, before, value);
            return new PreferenceWriteResult(PolicyWriteOutcome.Written, before) { Holds = holds };
        }

        return new PreferenceWriteResult(PolicyWriteOutcome.Aborted, null);
    }

    /// <summary>
    /// Takes the post-write checks that are due.
    /// </summary>
    /// <param name="now">The time, UTC.</param>
    /// <returns>The due checks, oldest first; they no longer wait.</returns>
    public IReadOnlyList<PendingVerification> TakeDue(DateTime now)
    {
        lock (_pendingLock)
        {
            var due = _pending.Where(p => p.DueUtc <= now).OrderBy(p => p.DueUtc).ToList();
            _pending.RemoveAll(p => p.DueUtc <= now);
            return due;
        }
    }

    /// <summary>
    /// Asks the server what it enforces for a user after a write (the check after writing):
    /// <c>GetUserDto(GetUserById(id))</c>, on 10.11.5 the user manager's cached object.
    /// </summary>
    /// <param name="check">The check.</param>
    /// <returns>What it found.</returns>
    public VerificationResult Verify(PendingVerification check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (Read(check.UserId) is not { } found)
        {
            return new VerificationResult(check, VerificationOutcome.UserNotFound, null);
        }

        var outcome = check.Target.Same(found) ? VerificationOutcome.Holds : check.Before.Same(found) ? VerificationOutcome.Reverted : VerificationOutcome.Changed;
        if (outcome != VerificationOutcome.Holds)
        {
            _logger.LogWarning("After Access Roles wrote the library access of user {UserId}, the server enforces {Found} ({Outcome})", check.UserId, found, outcome);
        }

        return new VerificationResult(check, outcome, found);
    }

    /// <summary>
    /// Reads a user's library fields as the server enforces them: <c>GetUserDto(GetUserById(id)).Policy</c>.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The fields, or <c>null</c> when the user is gone or can't be read.</returns>
    public LibraryFields? Read(Guid userId)
        => ReadCache(userId) is { Error: null } cache ? LibraryOf(cache.Policy) : null;

    /// <summary>
    /// The loop guard: whether a user's library fields are what this writer wrote for them a moment ago, so an
    /// event about the write (12.1+ publishes one per <c>UpdatePolicyAsync</c>) can be skipped.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="fields">The fields the event shows.</param>
    /// <returns><c>true</c> for the writer's own recent write.</returns>
    public bool IsOwnRecentWrite(Guid userId, LibraryFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return _recent.TryGetValue(userId, out var recent)
            && _timeProvider.GetUtcNow().UtcDateTime - recent.Utc <= OwnWriteWindow
            && recent.Fields.Same(fields);
    }

    private static LibraryFields LibraryOf(UserPolicy policy) => LibraryFields.Of(policy.EnableAllFolders, policy.EnabledFolders ?? []);

    // Every field of a policy, with the lyric field the user's real permission (GetUserDto never fills it).
    private static Dictionary<string, string> Fields(UserPolicy policy, bool lyric)
    {
        var fields = PolicyFields.Dump(policy).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        fields[PolicyFields.EnableLyricManagement] = lyric ? "true" : "false";
        return fields;
    }

    // The writer never enables a user: one the server enforces as disabled stays disabled, unless a concurrent save
    // committed the change (an admin enabled them).
    private static bool KeepDisabled(UserPolicy cache, UserPolicy write, IReadOnlyList<string> committedChanges)
    {
        if (!cache.IsDisabled || write.IsDisabled || committedChanges.Contains(nameof(UserPolicy.IsDisabled)))
        {
            return false;
        }

        write.IsDisabled = true;
        return true;
    }

    private async Task<PolicyWriteResult> WriteLockedAsync(Guid userId, LibraryFields target, int attempt, bool force, LibraryFields? expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // After a failed save of ours the cache may hold our entity, built from values a concurrent save has since replaced:
        // the committed row is the base then.
        var failed = _failed.TryGetValue(userId, out var failedSave) ? failedSave : null;
        var fromCommitted = force || failed is not null;
        IReadOnlyList<string> firstDifferences = [];
        for (var round = 0; round < 2; round++)
        {
            if (round > 0 && _settleDelay > TimeSpan.Zero)
            {
                await Task.Delay(_settleDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            if (ReadCache(userId) is not { } cache)
            {
                return new PolicyWriteResult(PolicyWriteOutcome.UserNotFound, target);
            }

            if (cache.Error is { } readError)
            {
                return new PolicyWriteResult(PolicyWriteOutcome.Failed, target) { Error = readError };
            }

            var before = LibraryOf(cache.Policy);

            // A user who already has the target needs nothing, whatever the caller planned against: since 0.9.4 an admin's
            // own save of a managed user is pre-merged with the rules' result, so the sync often finds it written already.
            if (!fromCommitted && target.Same(before))
            {
                return new PolicyWriteResult(PolicyWriteOutcome.Unchanged, target) { Before = before, Rebuilt = round > 0, Differences = firstDifferences };
            }

            if (expected is not null && !expected.Same(before))
            {
                _logger.LogInformation("Access Roles didn't write the library access of user {UserId}: it changed since the sync read it", userId);
                return new PolicyWriteResult(PolicyWriteOutcome.Aborted, target) { Before = before, NativeChanged = true };
            }

            var snapshot = Fields(cache.Policy, cache.Lyric);
            var committed = await ReadCommittedAsync(userId, cancellationToken).ConfigureAwait(false);
            if (committed.Missing)
            {
                _failed.TryRemove(userId, out _);
                return new PolicyWriteResult(PolicyWriteOutcome.UserNotFound, target) { Before = before };
            }

            if (fromCommitted && committed.Row is null)
            {
                _logger.LogWarning("Access Roles didn't write the library access of user {UserId} again: their stored row couldn't be read, and the server's cache may hold a save that failed", userId);
                return new PolicyWriteResult(PolicyWriteOutcome.Aborted, target) { Before = before, CommittedUnavailable = true, Error = "The user's stored row couldn't be read." };
            }

            // The re-read just before the write: nothing else may have changed since the read the write was built from.
            if (ReadCache(userId) is not { } again)
            {
                return new PolicyWriteResult(PolicyWriteOutcome.UserNotFound, target);
            }

            if (again.Error is { } rereadError)
            {
                return new PolicyWriteResult(PolicyWriteOutcome.Failed, target) { Before = before, Error = rereadError };
            }

            var differences = PolicyFields.Differences(snapshot, Fields(again.Policy, again.Lyric), _libraryFields);
            if (differences.Count > 0)
            {
                if (round == 0)
                {
                    _logger.LogInformation("The policy of user {UserId} changed while Access Roles prepared its write ({Count} fields); building it again", userId, differences.Count);
                    firstDifferences = differences;
                    continue;
                }

                _logger.LogWarning("Access Roles didn't write the library access of user {UserId}: their policy changed again while it rebuilt the write ({Count} fields)", userId, differences.Count);
                return new PolicyWriteResult(PolicyWriteOutcome.Aborted, target) { Before = before, Differences = differences, Rebuilt = true };
            }

            // The policy the write starts from: the cache's, unless the cache and the committed row disagree, or a
            // save of ours failed; then the committed row's, because the cache may hold a failed save's entity (ours, or an
            // admin's) or a sign-in's stale object, and the database holds what an admin last saved.
            var write = cache.Policy;
            var lyric = cache.Lyric;
            IReadOnlyList<string> committedFields = [];
            IReadOnlyList<string> concurrent = [];
            var resync = false;
            FailedSave? baseline = null;
            if (committed.Row is { } row)
            {
                baseline = new FailedSave(row.Fields, LibraryOf(row.Policy));
                var disagree = PolicyFields.Differences(snapshot, row.Fields, _notAgreed);

                // A user the server enforces as disabled while the database has them enabled needn't be waited for: the
                // write keeps them disabled (a lockout 10.11.5 never saved).
                if (!fromCommitted && round == 0 && disagree.Any(f => !(string.Equals(f, nameof(UserPolicy.IsDisabled), StringComparison.Ordinal) && cache.Policy.IsDisabled)))
                {
                    _logger.LogInformation("The server's cache and its database disagree on {Count} policy fields of user {UserId}; Access Roles waits for a save on its way", disagree.Count, userId);
                    firstDifferences = disagree;
                    continue;
                }

                if (fromCommitted || disagree.Count > 0)
                {
                    // From the committed row: what a concurrent save changed since our failed save (an admin's Dashboard
                    // save), or what the cache and the database disagree on.
                    concurrent = fromCommitted && failed?.Fields is { } failedBase ? PolicyFields.Differences(failedBase, row.Fields, _libraryFields) : [];
                    committedFields = fromCommitted && failed?.Fields is not null ? concurrent : disagree;
                    resync = fromCommitted && (failed?.Library is not { } failedLibrary || !failedLibrary.Same(LibraryOf(row.Policy)));
                    write = row.Policy;
                    lyric = row.Lyric;
                }
            }

            var keptDisabled = KeepDisabled(cache.Policy, write, concurrent);
            if (keptDisabled)
            {
                committedFields = [.. committedFields.Where(f => !string.Equals(f, nameof(UserPolicy.IsDisabled), StringComparison.Ordinal))];
            }

            var written = resync ? LibraryOf(write) : target;
            write.EnableAllFolders = written.EnableAllFolders;
            write.EnabledFolders = written.EnableAllFolders ? [] : [.. written.EnabledFolders];
            write.EnableLyricManagement = lyric;
            try
            {
                await _userManager.UpdatePolicyAsync(userId, write).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Access Roles' write of the library access of user {UserId} failed", userId);
                _failed[userId] = baseline ?? new FailedSave(null, null);
                return new PolicyWriteResult(PolicyWriteOutcome.Failed, target) { Before = before, Error = ex.Message, SaveFailed = true, Differences = firstDifferences, CommittedFields = committedFields };
            }

            _failed.TryRemove(userId, out _);
            Wrote(userId, written, before, attempt);
            if (resync)
            {
                _logger.LogWarning("Access Roles wrote the stored policy of user {UserId} back instead of its library access: a save that came first changed the library fields too; the next run decides", userId);
                return new PolicyWriteResult(PolicyWriteOutcome.Resynced, written) { Before = before, Rebuilt = round > 0, Differences = firstDifferences, CommittedFields = committedFields, KeptDisabled = keptDisabled };
            }

            _logger.LogInformation("Access Roles wrote the library access of user {UserId}: {Before} → {After}", userId, before, target);
            return new PolicyWriteResult(PolicyWriteOutcome.Written, target) { Before = before, Rebuilt = round > 0, Differences = firstDifferences, CommittedFields = committedFields, KeptDisabled = keptDisabled };
        }

        // Not reached: the second round returns.
        return new PolicyWriteResult(PolicyWriteOutcome.Aborted, target) { Differences = firstDifferences, Rebuilt = true };
    }

    // The user manager's object (on 10.11.5 its cache: what the server enforces) and its full policy, or null for no user.
    private CacheRead? ReadCache(Guid userId)
    {
        User? user;
        try
        {
            user = KitUsers.Find(_userManager, userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't look up user {UserId}", userId);
            return null;
        }

        if (user is null)
        {
            return null;
        }

        try
        {
            var policy = _userManager.GetUserDto(user).Policy ?? throw new InvalidOperationException("The user's DTO has no policy.");
            return new CacheRead(policy, user.HasPermission(PermissionKind.EnableLyricManagement), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't read the policy of user {UserId}", userId);
            return new CacheRead(new UserPolicy(), false, ex.Message);
        }
    }

    // The committed row and its policy as GetUserDto builds it, so it compares field by field with the cache's.
    private async Task<CommittedRead> ReadCommittedAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_committed is null)
        {
            return new CommittedRead(false, null);
        }

        var read = await _committed.ReadAsync(userId, cancellationToken).ConfigureAwait(false);
        if (read.User is not { } user)
        {
            return new CommittedRead(read.Read, null);
        }

        try
        {
            var policy = _userManager.GetUserDto(user).Policy ?? throw new InvalidOperationException("The user's DTO has no policy.");
            var lyric = user.HasPermission(PermissionKind.EnableLyricManagement);
            return new CommittedRead(true, new CommittedRow(policy, lyric, Fields(policy, lyric)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't read the stored policy of user {UserId}", userId);
            return new CommittedRead(false, null);
        }
    }

    private void Wrote(Guid userId, LibraryFields written, LibraryFields before, int attempt)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _recent[userId] = (now, written);
        Schedule(new PendingVerification(userId, written, before, now + VerifyDelay, attempt));
    }

    private void Schedule(PendingVerification check)
    {
        lock (_pendingLock)
        {
            _pending.RemoveAll(p => p.UserId.Equals(check.UserId));
            _pending.Add(check);
        }

        VerificationScheduled?.Invoke(this, EventArgs.Empty);
    }

    // The cache's read: the policy, the lyric permission, or the error of a read that failed.
    private sealed record CacheRead(UserPolicy Policy, bool Lyric, string? Error);

    // The committed row as a policy, the lyric permission, and every field for the comparisons.
    private sealed record CommittedRow(UserPolicy Policy, bool Lyric, IReadOnlyDictionary<string, string> Fields);

    // A read of the committed row: whether the database answered, and the row when it has one.
    private sealed record CommittedRead(bool Answered, CommittedRow? Row)
    {
        public bool Missing => Answered && Row is null;
    }

    // What the database held just before a save of ours that failed: the base the write again compares the committed row
    // with, to tell what a concurrent save changed. Null parts when the database couldn't be read then.
    private sealed record FailedSave(IReadOnlyDictionary<string, string>? Fields, LibraryFields? Library);
}
