using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Policy;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What Diagnostics shows of the library sync (the last sync, drift count, pending releases, errors): the last
/// run, the totals since the server started, the next run and the last error; since 0.9.4 the user policy hooks' counts
/// (<c>Hooks</c>: pre-merged saves, manual edits overridden, sign-ins corrected and refused, default roles given, the
/// hooks' errors and lock timeouts, the <i>Filter errors</i> count). Thread-safe; counts and ids only.
/// </summary>
internal sealed class LibrarySyncStatus
{
    /// <summary>An admin's policy save the pre-merge decided on.</summary>
    public const string PreMerged = "PreMerged";

    /// <summary>An admin's library edit the rules' result replaced (<c>ManualEditOverridden</c>).</summary>
    public const string ManualEditsOverridden = "ManualEditsOverridden";

    /// <summary>A sign-in whose user's access the hook wrote.</summary>
    public const string SignInsCorrected = "SignInsCorrected";

    /// <summary>A sign-in the hook refused (fail closed).</summary>
    public const string SignInsRefused = "SignInsRefused";

    /// <summary>A new user given the default roles.</summary>
    public const string DefaultRolesGiven = "DefaultRolesGiven";

    /// <summary>A hook that threw (it failed open, or closed for a sign-in).</summary>
    public const string FilterErrors = "FilterErrors";

    /// <summary>A hook that went on without the user's policy lock (5 s, failing open).</summary>
    public const string LockTimeouts = "LockTimeouts";

    private static readonly string[] _hookNames = [PreMerged, ManualEditsOverridden, SignInsCorrected, SignInsRefused, DefaultRolesGiven, FilterErrors, LockTimeouts];

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _hooks = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _drifted = [];
    private SyncReport? _lastRun;
    private DateTime? _lastWriteUtc;
    private DateTime? _nextRunUtc;
    private string? _lastError;
    private DateTime? _lastErrorUtc;
    private int _runs;
    private int _writes;
    private int _releases;
    private int _aborted;
    private int _reverted;
    private int _revertedAgain;
    private int _errors;
    private int _userRuns;

    /// <summary>
    /// Records a finished run. A run for one user (the per-user <i>Revert</i>) counts in the totals and decides
    /// only their drift: the last run and the others' drift stay the last run for everyone's.
    /// </summary>
    /// <param name="report">The run's report.</param>
    public void Record(SyncReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.DryRun)
        {
            return;
        }

        static bool Drifted(SyncUserResult u) => u.Kind == SyncActionKind.Drift || (u.Reason == SyncReasons.Drift && !u.Wrote);
        lock (_lock)
        {
            if (report.Scope is { } only)
            {
                _userRuns++;
                _drifted.Remove(only);
                if (report.Users.Any(u => u.User.Id.Equals(only) && Drifted(u)))
                {
                    _drifted.Add(only);
                }
            }
            else
            {
                _lastRun = report;
                _runs++;
                _drifted.Clear();
                _drifted.UnionWith(report.Users.Where(Drifted).Select(u => u.User.Id));
            }

            foreach (var user in report.Users)
            {
                if (user.Wrote)
                {
                    _lastWriteUtc = report.StartedUtc + report.Duration;
                    if (user.Kind == SyncActionKind.Release)
                    {
                        _releases++;
                    }
                    else
                    {
                        _writes++;
                    }
                }

                if (user.Outcome == PolicyWriteOutcome.Aborted)
                {
                    _aborted++;
                }

                if (user.Outcome is PolicyWriteOutcome.Failed or PolicyWriteOutcome.LockTimeout)
                {
                    _errors++;
                }
            }
        }
    }

    /// <summary>
    /// Records a one-user run's result (the sign-in hook): the totals and the last write, not the last run.
    /// </summary>
    /// <param name="result">What was done.</param>
    /// <param name="utc">When, UTC.</param>
    public void RecordUser(SyncUserResult result, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            if (result.Wrote)
            {
                _writes++;
                _lastWriteUtc = utc;
                _hooks[SignInsCorrected] = _hooks.GetValueOrDefault(SignInsCorrected) + 1;
            }

            if (result.Outcome == PolicyWriteOutcome.Aborted)
            {
                _aborted++;
            }

            if (result.Outcome is PolicyWriteOutcome.Failed or PolicyWriteOutcome.LockTimeout)
            {
                _errors++;
            }
        }
    }

    /// <summary>
    /// Counts one event of a user policy hook.
    /// </summary>
    /// <param name="name">The count (<see cref="PreMerged"/> and the others).</param>
    public void Hook(string name)
    {
        lock (_lock)
        {
            _hooks[name] = _hooks.GetValueOrDefault(name) + 1;
        }
    }

    /// <summary>
    /// Records a post-write check.
    /// </summary>
    /// <param name="result">What it found.</param>
    /// <param name="rewritten">Whether the sync wrote again because of it.</param>
    public void Verified(VerificationResult result, bool rewritten)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != VerificationOutcome.Reverted)
        {
            return;
        }

        lock (_lock)
        {
            if (rewritten)
            {
                _reverted++;
            }
            else
            {
                _revertedAgain++;
            }
        }
    }

    /// <summary>
    /// Records a run or check that failed.
    /// </summary>
    /// <param name="message">The error.</param>
    /// <param name="utc">When, UTC.</param>
    public void Failed(string message, DateTime utc)
    {
        lock (_lock)
        {
            _errors++;
            _lastError = message;
            _lastErrorUtc = utc;
        }
    }

    /// <summary>
    /// Records when the next run is due.
    /// </summary>
    /// <param name="utc">When, UTC, or <c>null</c>.</param>
    public void NextRun(DateTime? utc)
    {
        lock (_lock)
        {
            _nextRunUtc = utc;
        }
    }

    /// <summary>
    /// Describes the sync for Diagnostics.
    /// </summary>
    /// <param name="paused">Why the sync writes nothing now, or <c>null</c>.</param>
    /// <param name="driftPolicy">The drift policy in effect.</param>
    /// <param name="intervalMinutes">The backstop interval in effect.</param>
    /// <param name="pendingReleases">The pending releases in the store.</param>
    /// <param name="pendingRenames">The pending renames in the store.</param>
    /// <param name="pendingVerifications">The post-write checks waiting.</param>
    /// <param name="failedSaves">The users whose last save failed, to be written again.</param>
    /// <returns>The entry.</returns>
    public IDictionary<string, object?> Describe(string? paused, string driftPolicy, int intervalMinutes, int pendingReleases, int pendingRenames, int pendingVerifications, int failedSaves = 0)
    {
        lock (_lock)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["State"] = paused is null ? "Active" : "Paused",
                ["PausedBecause"] = paused,
                ["DriftPolicy"] = driftPolicy,
                ["ReconcileIntervalMinutes"] = intervalMinutes,
                ["LastRun"] = _lastRun is null
                    ? null
                    : new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Utc"] = _lastRun.StartedUtc,
                        ["Trigger"] = _lastRun.Trigger.ToString(),
                        ["DurationMs"] = (long)_lastRun.Duration.TotalMilliseconds,
                        ["Paused"] = _lastRun.Paused,
                        ["ManagedUsers"] = _lastRun.ManagedUsers,
                        ["Written"] = _lastRun.Written,
                        ["Skipped"] = _lastRun.Skipped,
                        ["Drifted"] = _lastRun.Drifted,
                        ["LibraryChanges"] = _lastRun.Libraries.Count,
                        ["WaitingLibraries"] = _lastRun.WaitingLibraries.Count,
                        ["WithheldLibraries"] = _lastRun.WithheldLibraries.Count,
                        ["SkippedBecause"] = _lastRun.Users.Where(u => u.Kind == SyncActionKind.Skip).GroupBy(u => u.Reason, StringComparer.Ordinal)
                            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
                    },
                ["LastWriteUtc"] = _lastWriteUtc,
                ["NextRunUtc"] = _nextRunUtc,
                ["DriftCount"] = _drifted.Count,
                ["PendingReleases"] = pendingReleases,
                ["PendingRenames"] = pendingRenames,
                ["PendingVerifications"] = pendingVerifications,
                ["FailedSaves"] = failedSaves,
                ["Totals"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Runs"] = _runs,
                    ["UserRuns"] = _userRuns,
                    ["Writes"] = _writes,
                    ["Releases"] = _releases,
                    ["Aborted"] = _aborted,
                    ["RevertedAfterWrite"] = _reverted,
                    ["RevertedAgain"] = _revertedAgain,
                    ["Errors"] = _errors
                },
                ["Hooks"] = _hookNames.ToDictionary(n => n, n => (object?)_hooks.GetValueOrDefault(n), StringComparer.Ordinal),
                ["LastError"] = _lastError,
                ["LastErrorUtc"] = _lastErrorUtc
            };
        }
    }
}
