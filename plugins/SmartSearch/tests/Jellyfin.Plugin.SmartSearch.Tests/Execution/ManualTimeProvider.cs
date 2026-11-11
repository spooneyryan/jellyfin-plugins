using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.SmartSearch.Tests.Execution;

/// <summary>
/// A clock the test moves (<see cref="Advance"/>), with timers that fire when it passes their time: what the engine's
/// timeout (<c>new CancellationTokenSource(delay, timeProvider)</c>) and the result cache's lifetime read.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    /// <summary>Moves the clock on, firing the timers that come due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            due = _timers.Where(t => t.Due <= _now).ToList();
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; } = DateTimeOffset.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._lock)
            {
                clock._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = clock._now + dueTime;
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire()
        {
            lock (clock._lock)
            {
                if (!clock._timers.Remove(this))
                {
                    return;
                }
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (clock._lock)
            {
                clock._timers.Remove(this);
            }
        }

        public System.Threading.Tasks.ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
