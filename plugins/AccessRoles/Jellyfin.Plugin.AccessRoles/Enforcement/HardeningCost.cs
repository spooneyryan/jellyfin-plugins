using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// What the admin-surface, server-wide and hardening rules cost: per rule, how many requests it saw and
/// its own time per request (before the action and after it, never the action's) over the last <see cref="Window"/>
/// requests, for Diagnostics' <c>Enforcement.Cost</c> and the cost measurement on a large sandbox.
/// Thread-safe; one lock per request.
/// </summary>
internal sealed class HardeningCost
{
    /// <summary>How many of each rule's latest times are kept.</summary>
    public const int Window = 256;

    private readonly object _lock = new();
    private readonly Dictionary<string, Series> _rules = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one request's time in a rule.
    /// </summary>
    /// <param name="rule">The rule: <c>Hardening.Items</c>, <c>ServerWide.ConfigurationReads</c> and the like.</param>
    /// <param name="elapsed">Its own time.</param>
    public void Record(string rule, TimeSpan elapsed)
    {
        ArgumentException.ThrowIfNullOrEmpty(rule);
        lock (_lock)
        {
            if (!_rules.TryGetValue(rule, out var series))
            {
                series = new Series();
                _rules[rule] = series;
            }

            series.Add(elapsed.TotalMilliseconds * 1000.0);
        }
    }

    /// <summary>
    /// Describes the times: per rule its count since the start, and over the kept window the p50, p95 and maximum in
    /// milliseconds and the times themselves in microseconds, oldest first (the measurement reads the last ones).
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        lock (_lock)
        {
            return _rules.OrderBy(r => r.Key, StringComparer.Ordinal).ToDictionary(
                r => r.Key,
                r =>
                {
                    var recent = r.Value.Recent();
                    var sorted = recent.Order().ToArray();
                    return (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["Count"] = r.Value.Count,
                        ["P50Ms"] = Percentile(sorted, 50),
                        ["P95Ms"] = Percentile(sorted, 95),
                        ["MaxMs"] = sorted.Length == 0 ? null : Math.Round(sorted[^1] / 1000.0, 3),
                        ["RecentUs"] = recent.Select(us => Math.Round(us, 1)).ToArray()
                    };
                },
                StringComparer.Ordinal);
        }
    }

    // Nearest rank, as the cost measurement computes it.
    private static double? Percentile(double[] sorted, int percent)
    {
        if (sorted.Length == 0)
        {
            return null;
        }

        var index = Math.Clamp((int)Math.Ceiling(percent / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1);
        return Math.Round(sorted[index] / 1000.0, 3);
    }

    private sealed class Series
    {
        private readonly double[] _times = new double[Window];
        private int _next;

        public long Count { get; private set; }

        public void Add(double microseconds)
        {
            _times[_next] = microseconds;
            _next = (_next + 1) % Window;
            Count++;
        }

        public double[] Recent()
        {
            var kept = (int)Math.Min(Count, Window);
            var result = new double[kept];
            for (var i = 0; i < kept; i++)
            {
                result[i] = _times[(_next - kept + i + Window) % Window];
            }

            return result;
        }
    }
}
