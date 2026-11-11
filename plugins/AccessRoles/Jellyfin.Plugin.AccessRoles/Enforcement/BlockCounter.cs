using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Audit;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The blocked-request and filter-error counters: every refusal,
/// every merge that reverted a locked value and (since 0.9.7) every answer of a hardening or lock rule that hid something is
/// counted per user, rule, action and outcome in 10-minute windows (UTC,
/// from the hour), and each window becomes one audit line once it has closed: <c>RequestsBlocked</c>
/// (<c>{"Rule":"Settings.Profile","Endpoint":"UserController.UpdateUserPassword","Outcome":"Refused","Count":3,
/// "WindowStartUtc":…}</c>) or, for a rule that failed and refused (fail closed), <c>FilterErrors</c>. So a scripted flood
/// writes at most one line per user, rule, action and outcome every 10 minutes. Thread-safe.
/// </summary>
internal sealed class BlockCounter
{
    /// <summary>A request refused with 403 <c>Locked</c>.</summary>
    public const string Refused = "Refused";

    /// <summary>A save merged: a locked value the post had changed was put back, and the save went through.</summary>
    public const string Reverted = "Reverted";

    /// <summary>A rule that couldn't decide (it threw, or the store couldn't be read) and refused (fail closed).</summary>
    public const string FailedClosed = "FailedClosed";

    /// <summary>
    /// A hardening or lock rule hid something: an answer as if an item, a library or another user didn't exist
    /// (an empty result, 404, a report answered without the action), or a result with what the caller can't see taken out.
    /// </summary>
    public const string Hidden = "Hidden";

    /// <summary>The window's length.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly Dictionary<Key, int> _open = [];
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlockCounter"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public BlockCounter(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the window a time falls in: its start, from the hour in steps of <see cref="Window"/>.
    /// </summary>
    /// <param name="utc">The time, UTC.</param>
    /// <returns>The window's start.</returns>
    public static DateTime WindowOf(DateTime utc) => new(utc.Ticks - (utc.Ticks % Window.Ticks), DateTimeKind.Utc);

    /// <summary>
    /// Counts one request.
    /// </summary>
    /// <param name="userId">The caller, or <c>null</c> for an anonymous one (aggregated under <c>"UserId": null</c>).</param>
    /// <param name="rule">The rule: <c>Settings.&lt;Lock&gt;</c>, or <c>Settings</c> when the rule couldn't tell the section;
    /// the hardening's <c>Locks.&lt;Lock&gt;</c>, <c>ServerWide.&lt;Rule&gt;</c> and <c>Hardening.&lt;Rule&gt;</c>.</param>
    /// <param name="endpoint">The action: <c>UserController.UpdateUserPassword</c>.</param>
    /// <param name="outcome"><see cref="Refused"/>, <see cref="Reverted"/>, <see cref="FailedClosed"/> or <see cref="Hidden"/>.</param>
    public void Record(Guid? userId, string rule, string endpoint, string outcome)
    {
        ArgumentException.ThrowIfNullOrEmpty(rule);
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(outcome);
        var key = new Key(userId is { } id && !id.Equals(Guid.Empty) ? id : null, rule, endpoint, outcome, WindowOf(_timeProvider.GetUtcNow().UtcDateTime));
        lock (_lock)
        {
            _open[key] = _open.GetValueOrDefault(key) + 1;
            _totals[outcome] = _totals.GetValueOrDefault(outcome) + 1;
        }
    }

    /// <summary>
    /// Takes the windows that have closed (or, at shutdown, every window), as audit lines.
    /// </summary>
    /// <param name="all">Whether to take the open windows too (the server stops).</param>
    /// <param name="nameOf">Finds a user's name, or <c>null</c>.</param>
    /// <returns>The lines, oldest window first; the counts are gone from the counter.</returns>
    public IReadOnlyList<AuditRecord> Take(bool all, Func<Guid, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        List<KeyValuePair<Key, int>> taken;
        lock (_lock)
        {
            taken = [.. _open.Where(e => all || e.Key.WindowStartUtc + Window <= now)];
            foreach (var entry in taken)
            {
                _open.Remove(entry.Key);
            }
        }

        return [.. taken
            .OrderBy(e => e.Key.WindowStartUtc)
            .ThenBy(e => e.Key.Rule, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Endpoint, StringComparer.Ordinal)
            .Select(e => new AuditRecord(
                AuditActor.System,
                e.Key.Outcome == FailedClosed ? AuditActions.FilterErrors : AuditActions.RequestsBlocked,
                e.Key.UserId,
                null,
                e.Key.UserId is { } id ? nameOf(id) : null,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Rule"] = e.Key.Rule,
                    ["Endpoint"] = e.Key.Endpoint,
                    ["Outcome"] = e.Key.Outcome,
                    ["Count"] = e.Value,
                    ["WindowStartUtc"] = e.Key.WindowStartUtc,
                    ["UserId"] = e.Key.UserId
                }))];
    }

    /// <summary>
    /// Describes the counter for Diagnostics: the totals since the start by outcome, and the open windows' counts by rule
    /// and outcome (no user).
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        lock (_lock)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Totals"] = new[] { Refused, Reverted, FailedClosed, Hidden }.ToDictionary(o => o, o => _totals.GetValueOrDefault(o), StringComparer.Ordinal),
                ["OpenWindows"] = _open
                    .GroupBy(e => e.Key.Rule + " " + e.Key.Outcome, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Sum(e => e.Value), StringComparer.Ordinal),
                ["WindowMinutes"] = (int)Window.TotalMinutes
            };
        }
    }

    private readonly record struct Key(Guid? UserId, string Rule, string Endpoint, string Outcome, DateTime WindowStartUtc);
}
